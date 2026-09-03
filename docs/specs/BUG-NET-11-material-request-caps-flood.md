# [BUG-NET-11] Per-face material requests flood the caps rate limiter → slow textures

- **Feature ID:** `BUG-NET-11`
- **Track:** `net` / `render`
- **Status:** `🧪 Review`
- **Owner:** `claude`
- **Depends on:** `BUG-RENDER-06` (which made legacy materials actually resolve — and thereby
  exposed this)
- **Reported:** live, Agni, 2026-09-03. *"Die Texturen kommen extrem langsam, eigentlich sollten
  die alle schon im Cache liegen"* + a half-loaded avatar; *"die Transparenzen sind komplett
  falsch an den anderen Avataren"* (blocky untextured hair while textures crawl in).

## What's happening

`AvatarRenderer.ApplyFaceMaterialsAsync` builds a mesh's faces **one at a time**
(`await BuildFaceMaterialAsync` per surface), and each `BuildFaceMaterialAsync` `await`s
`GetLegacyMaterialAsync` / `GetMaterialAsync`. `AssetService.QueueLegacyMaterialAsync` batches
pending ids behind a **100 ms** window — but on a mesh with many differently-materialled faces
the per-face requests arrive **> 100 ms apart** (each waits on the previous face's texture
fetch), so the window never catches more than one: **one single-id `RenderMaterials` cap POST per
face**. Session log with two nearby avatars: `[LegacyMat] POST 200: 1 ids` ×41, plus dozens more
small POSTs — ~200+ `RenderMaterials` POSTs, which fill SLNG's caps rate-limiter queue
(`warn: Caps rate limiter queue full for simhost-…agni…; proceeding without throttle`). Texture
fetches (also cap requests) queue behind that → everything crawls, even with 21 k textures on
disk. Before `BUG-RENDER-06` this was hidden: every legacy material resolved to *nothing*, so
`_recentMaterialMisses` deflected each id for 2 min and far fewer POSTs went out.

## Fix (`v0.20.45-alpha`)

`ApplyFaceMaterialsAsync` now **prefetches every distinct material id its faces reference, up
front, before the per-face loop** — one tight loop firing all `GetLegacyMaterialAsync` /
`GetMaterialAsync` calls, then a single `Task.WhenAll`. Because they all land in
`_pendingMaterialIds` within microseconds, `AssetService`'s batch window coalesces them into
**one** `FetchLegacyMaterialsAsync` call per mesh (`RenderMaterials` sends up to 50 ids/POST).
Every per-face `await` in the loop is then a cache hit.

## `[TexPipe]` result (`v0.20.47`) + decode fix (`v0.20.48`)

Live session with the new diagnostic: `req=1600 diskCacheHit=1598 httpFetch=1`, **zero
`Caps rate limiter queue full`**, `[LegacyMat] POST` lines now multi-id. So the material-POST
flood is fixed and the disk cache IS serving ~99.9 % of a familiar scene. The remaining slowness
is the **J2K decode**: `[TexPipe]` reported avg "91 ms" per cached texture because the cache-hit
path was a bare `Task.Run(DecodeTexture)` — ~1600 decodes dumped on the thread pool at once, so
that number is mostly pool-queue wait.

`v0.20.48`: `_textureDecodeThrottle` (`PriorityGate(ProcessorCount - 2)`) around the cache-hit
decode — bounded to the CPU, `priority`-ordered. `[TexPipe]` avg stayed ~86–99 ms *after* the
throttle → a single OpenJPEG decode really is ~90 ms; not the main problem though —

**`v0.20.49` — the actual cause: repeat decodes.** Next session `[TexPipe] req` climbed past
**2600** for a scene of maybe ~200 distinct textures — each decoded ~13× from disk.
`GpuCache.GetOrUploadTextureAsync` did `var cached = rejectDegraded ? null : Get(id)`, so **every
avatar face texture (`rejectDegraded: true`) bypassed the GPU cache** and re-fetched through
`AssetService`; avatar faces rebuild constantly (`[BomFace] registered` ~660×/session), and
`AssetService._memCache` (256 MB, `Size = W·H·4`) only holds ~60 × 1024² so it thrashes too.
Fix: GpuCache tracks per-id whether the cached `ImageTexture` came from a **degraded** decode
(`_uploadFromDegraded`); a `rejectDegraded` caller re-fetches only those, a clean cached upload is
reused by everyone. `initialRefCount: 1` on avatar textures keeps them un-evicted. This should
collapse `[TexPipe] req` to ~one per distinct texture.

**Why Firestorm is faster:** KDU (commercial SIMD J2K, ~10–20× OpenJPEG) + a **decoded**-texture
cache (stores the decoded/transcoded result, not the `.j2c`, so a hit skips J2K entirely). SLNG's
re-decode bug above is the bulk of the gap; a decoded/BC7 disk cache is the next structural step
if `[TexPipe]` after `v0.20.49` shows first-time decodes still dominate.

The 403 on `33192a49` is `from asset-cdn.glb.agni.lindenlab.com/` — the **generic** asset CDN,
not a bake-style different URL: a real permission denial / non-persisted asset, nothing to route
differently.

## `v0.20.51` — the actual serialising stage was the MAIN-THREAD QUEUE, not the decoder

The `v0.20.50` session log was unusable (`godot.log`'s body came back as 437 kB of NUL bytes —
the engine's buffered log loses everything not yet flushed when a session does not end cleanly),
so this round was settled from the `v0.20.49` log plus the code and an offline decode benchmark.

**What the v0.20.49 log actually says.** `[TexPipe] req=8800 diskCacheHit=8795 httpFetch=2`. The
"~200 distinct textures" the previous round assumed was wrong: `[FaceAlpha]` alone names **2861
distinct texture ids** in that one session, and the on-disk cache holds 20 708 `.j2c` files
(4.5 GB). So the repeat factor is ~3×, not ~13× — real, but not the headline.

**The headline.** `MainThreadWorkQueue` runs on a **3 ms/frame** budget and always runs at least
one item per lane per frame (by design, so one huge item can't deadlock against a budget it can
never fit in). Every texture upload was queued as a single `Lane.Visual` item that did *all* of
`Image.CreateFromData` + `FixAlphaEdges` + `Resize(Lanczos)` + `GenerateMipmaps` +
`ImageTexture.CreateFromImage` — 10–30 ms for a 1024×1024. An item that big means the budget is
blown by item #1, so the lane drains **exactly one texture per frame**. 8800 requests ÷ 30–60 fps
= **2.5–5 minutes**, which is the reported wait, and it is independent of how fast the decode is.
That also made the [TexPipe] `inflight` spike to 765–1000+: the decodes were finishing fine and
piling up behind the queue.

Fixes, all in `v0.20.51`:

1. **`GpuCache.PrepareImageAsync`** — every step except the GPU upload now runs on a worker
   thread (`Image` is one of Godot's thread-safe data types; only `ImageTexture.CreateFromImage`
   touches the RenderingServer). The queued main-thread item is now `CreateFromImage` + `Put`.
   `Task.Run` is unconditional and gated by a `ProcessorCount-2` semaphore: `GetTextureAsync`
   returns a *completed* task on an AssetService memory-cache hit, and `ObjectRenderer` calls
   `GetOrUploadTextureAsync` straight from `_Process`, so without the hop that case would have run
   the whole image build inline on the main thread with no budget at all. The sharpen path
   (`TryUpgradeCachedTexture`) got the same split.
2. **Adaptive budget** (`RenderConfig.MainThreadWorkBudgetFor`) — 3 ms while the queue is short
   (≤200), ramping to 9 ms at a backlog of 2000. A deep backlog is the user watching a half-built
   scene; a few dropped frames there buy back minutes. Steady state is unchanged.
3. **`[GpuCache]` now reports `pinned=` / `pinnedMB=` and `mainQueue=`.** This is the one number
   still missing: `AvatarRenderer` `Put`s every avatar face texture with `initialRefCount: 1`
   (pinned, never released — it has no `AddRef`/`ReleaseRef` bookkeeping, see its own comment at
   `LoadAndApplyTextureAsync`) **and passes no `screenPixelArea`, so they upload at full
   resolution**. If `pinnedMB` approaches the 1536 MB budget, `EvictIfNeeded` has nothing left to
   reclaim, every object texture is evicted the moment it lands, and `ObjectRenderer`'s 4 Hz
   texture re-offer re-decodes them forever — which is exactly what a ~3× repeat factor on a
   static scene looks like. Next session's log decides it.
4. **`PerfSidecar`** (`ConsoleToGodotLog.cs`) — `[TexPipe]` and `[GpuCache]` are mirrored to
   `user://logs/slng-perf.log` with `AutoFlush`, so an unclean exit can no longer erase the
   measurement the build was shipped to take.

### Offline decode benchmark (real cached assets, this machine)

40 random `.j2c` from the live cache, Magick.NET Q8 14.15.0, single-threaded:

| `jp2:reduce-factor` | linear scale | avg decode |
|---|---|---|
| 0 | 1/1 | **47.6 ms** |
| 1 | 1/4 | 13.1 ms |
| 2 | 1/16 | 3.8 ms |
| 3 | 1/64 | 2.6 ms |

So a full decode is ~48 ms, not the ~90 ms `[TexPipe]` reported (the rest was queueing), and
**ImageMagick's `reduce-factor` divides each dimension by 4^N, not 2^N** — worth writing down,
the obvious assumption is wrong. Reduce-level decode remains the next structural lever: SLNG
currently always decodes at full resolution and only *then* downsamples locally for distant
objects, paying 47.6 ms to produce a 64×64 upload it could have decoded in 3.8 ms. It would also
cut GPU-cache pressure by the same factor, which is the other half of the repeat-decode loop.

## Live result (`v0.20.51`/`.52`, 2026-09-03 17:42) — textures fixed, VRAM budget is not

`[TexPipe] req=6400 distinct=4422` — repeat factor **1.45×** (was ~3×) on a scene that genuinely
holds ~4400 distinct textures; `diskCacheHit=6381/6400`. `[GpuCache] hit=7 075 438 /
get=7 232 200` = **97.8 %**, `bypassDegraded=0`. `mainQueue` peaked at 563–704 during the load
burst and settled at 178. User: *"das rendern scheint schneller zu gehen"*. The queue was the
bottleneck; it no longer is.

**But `entries=6415 pinned=6415 pinnedMB=2156 sizeMB=2156/1536`.** Every entry is un-evictable and
the cache is 40 % over budget. That is not only AvatarRenderer's `initialRefCount: 1` — ObjectRenderer's
own AddRef/ReleaseRef keeps a texture pinned as long as a scene object uses it, and on a scene this
size that is all of them. So the 1536 MB budget cannot be enforced while uploads are full
resolution; the answer is smaller uploads (**reduce-level decode**, see the benchmark above),
not more eviction. Raising the budget would change nothing.

**Diagnostic footgun, fixed in `v0.20.53`:** `[GpuCache]` dumped every 200 gets on the assumption
that a session makes thousands. It makes **7.2 million** (ObjectRenderer re-offers every used
texture id of every culled object at 4 Hz), so the line wrote **45 061 of the session log's
49 273 lines**. Now gated to one line per 10 s. Separately worth noting: 7.2 M `lock`-ed dictionary
lookups + LRU splices per session is real main-thread cost nobody has profiled.

## Still open / verify

- **Not re-verified in-world.** Confirm `[LegacyMat] POST` lines now carry many ids each (not
  `1 ids`), `Caps rate limiter queue full` stops, and textures stream in at a normal rate.
- If the flood persists, the next suspects are (a) `ApplyFaceMaterialsAsync` itself re-running
  too often (per terse update / animation change — `[BomFace] … registered` fired ~660× in one
  session), which a debounce would cut, and (b) whether `_textureHttpClient` shares the same
  rate-limited path as the cap POSTs.
- The blocky/see-through untextured avatar faces are `BUG-RENDER-10`'s follow-ups
  (`v0.20.44`: an untextured avatar face renders invisible until its texture arrives) — this bug
  is about *why* they stay untextured so long.
