# HANDOVER

**One file.** Overwrite the section below when you hand over new work; do not create
`handover-<date>-<topic>.md` alongside it.

---

# 2026-09-03 (later) — `v0.20.51` → `v0.20.58`

**`v0.20.50`'s log was unusable.** `godot.log`'s body came back as 437 kB of NUL bytes (the
engine's buffered log loses everything unflushed when a session doesn't end cleanly), so the
`[TexPipe] distinct=` / `[GpuCache]` counters that build was shipped to take are gone. This round
was settled from the `v0.20.49` log, the code, and an offline decode benchmark instead.

### `v0.20.51` — BUG-NET-11: the serialising stage is the MAIN-THREAD QUEUE, not the decoder

- The "~200 distinct textures" assumption was **wrong**: `[FaceAlpha]` alone names **2861 distinct
  texture ids** in the v0.20.49 session, and the disk cache holds 20 708 `.j2c` (4.5 GB). So
  `req=8800` is a ~3× repeat factor over a genuinely large scene, not 13× over a tiny one.
- **The real cost:** `MainThreadWorkQueue` runs a **3 ms/frame** budget and always runs at least
  one item per lane per frame. Every texture upload was ONE queued item doing
  `Image.CreateFromData` + `FixAlphaEdges` + `Resize` + `GenerateMipmaps` + `CreateFromImage`
  (10–30 ms for a 1024²) — so the lane drained **exactly one texture per frame**. 8800 ÷ 30–60 fps
  = 2.5–5 minutes, independent of decode speed. That is the reported wait, and it explains
  `[TexPipe] inflight` spiking to 765–1000+ (decodes finishing and piling up behind the queue).
- Fixes: `GpuCache.PrepareImageAsync` moves everything but `ImageTexture.CreateFromImage` onto a
  worker (bounded by a `ProcessorCount-2` semaphore; the `Task.Run` is unconditional because an
  AssetService memory-cache hit returns a completed task and `ObjectRenderer` calls in from
  `_Process`). Same split for the sharpen path. Plus an **adaptive budget**
  (`RenderConfig.MainThreadWorkBudgetFor`): 3 ms while the queue is ≤200 deep, ramping to 9 ms at
  2000 — steady state unchanged.
- `[GpuCache]` now also reports **`pinned=` / `pinnedMB=` / `mainQueue=`**. This is the one open
  question: `AvatarRenderer` `Put`s every avatar face texture with `initialRefCount: 1` (never
  released — it has no `AddRef`/`ReleaseRef` bookkeeping) **and passes no `screenPixelArea`, so
  they upload at FULL resolution**. If `pinnedMB` nears the 1536 MB budget, `EvictIfNeeded` has
  nothing left to reclaim, object textures are evicted the moment they land, and ObjectRenderer's
  4 Hz texture re-offer re-decodes them forever — the ~3× repeat factor. **Next log decides it.**
- **`PerfSidecar`** (`ConsoleToGodotLog.cs`): `[TexPipe]` + `[GpuCache]` are mirrored to
  `user://logs/slng-perf.log` with `AutoFlush`, so an unclean exit can't erase the measurement
  again. Read that file, not godot.log, for these two.
- Offline benchmark, 40 real cached `.j2c`, Magick.NET Q8 14.15.0, single-threaded: full decode
  **47.6 ms** (not the 90 ms `[TexPipe]` showed — the rest was queueing). And ImageMagick's
  `jp2:reduce-factor` divides each dimension by **4^N, not 2^N** (r1 → 13.1 ms at ¼ size, r2 →
  3.8 ms at 1/16). Reduce-level decode is the next structural lever: SLNG always decodes full-res
  and only *then* downsamples, paying 47.6 ms to produce a 64×64 upload.

### `v0.20.52` — BUG-RENDER-09: `prim_hash_avatar`, option 1c

New `PrimShaderFamily.Kind.Hash` + `app/materials/prim/prim_hash.gdshader` and
`prim_hash_avatar.gdshader` — `prim_scissor`'s body with `ALPHA_SCISSOR_THRESHOLD` swapped for
`ALPHA_HASH_SCALE`, `depth_draw_opaque` kept, `alpha_to_coverage` deliberately dropped. Routing is
the same scope as the reverted v0.20.41 (`wasBom && ClassifyAlpha == Scissor`) but to a
depth-WRITING variant, so the head-sort regression that killed `Blend` is structurally
unreachable. Hair / clothing / non-BoM and the system-bake path are untouched. Selftest is now
**32/32** (was 29/29 — two new shaders + one variant-pair check).

### Live result of `v0.20.51`/`.52` (user, 2026-09-03 17:42 session)

**Textures: fixed, and the numbers now say so.** `[TexPipe] req=6400 distinct=4422` — a repeat
factor of **1.45×**, down from ~3×, on a scene that genuinely has ~4400 distinct textures.
`diskCacheHit=6381/6400`. `[GpuCache] hit=7 075 438 / get=7 232 200` = **97.8 %**.
`mainQueue` started at 563–704 during the load burst (adaptive budget doing its job) and settled at
178. User: *"das rendern scheint schneller zu gehen"*.

**The pin question is answered, and the answer is "everything".** `entries=6415 pinned=6415
pinnedMB=2156 sizeMB=2156/1536` — **100 % of the cache is un-evictable and it is 40 % over
budget.** Not only AvatarRenderer's `initialRefCount: 1`: ObjectRenderer's own AddRef/ReleaseRef
keeps a texture pinned for as long as an object in the scene uses it, which on a scene this size is
all of them. So the 1536 MB budget is simply not enforceable at full-resolution uploads — it is not
a leak, the scene really does want 2.1 GB of texture. The fix for that is **reduce-level decode**
(smaller uploads, 16× less VRAM and 4–12× less decode), not more eviction. Filed, not started.

**The console log exploded — that was my own diagnostic.** `[GpuCache]` fired every 200 gets, and a
session makes **7.2 million** gets (ObjectRenderer re-offers every used texture id of every culled
object at 4 Hz), so it wrote **45 061 of the log's 49 273 lines**. `v0.20.53` gates it to one line
per 10 s. Worth remembering separately: 7.2 M `lock`-ed dictionary + LRU-splice operations per
session is itself real main-thread cost that nobody has looked at.

**Alpha: `Kind.Hash` tried and REJECTED.** Screenshot of the legs shows large axis-aligned
**rectangular patches** of skin dropping out. Godot's hashed alpha is Wyman & McGuire's algorithm —
the threshold is `hash(floor(pix_scale * position))` with `pix_scale = 1/(alpha_hash_scale × max
screen-space position derivative)`, so close up on a large surface whole *blocks* of object space
share one threshold. Coarse patchwork, not a dither. Tuning the scale only trades block size for
skin speckle. `Kind.Hash` and both shaders are kept but **nothing routes to them** — do not
re-point BoM faces at it.

### `v0.20.53` — BUG-RENDER-09 option 1b, and the reason the first two attempts were misconceived

Both 1a (`Blend`) and 1c (`Hash`) assumed a wide soft gradient that needs fading. `ClassifyAlpha`
says there isn't one: a face only reaches `Scissor` when `fracMid <= 0.06` **and**
`fracClear <= 0.5` — predominantly opaque with a *thin* AA border; anything genuinely graded
already goes to `Blend`. So the defect is only **where the binary cut falls**. `v0.20.53` keeps the
`Kind`, the shader, the depth write and the sort behaviour and moves the cut from 0.25 to
`BomAlphaScissorThreshold = 0.04` — the ragged contour lands where the mask is already ~96 %
transparent instead of in near-opaque skin.

### `v0.20.54` — the alpha problem is a SORT failure, and the user was right that the hair is the same bug

`v0.20.53`'s threshold change (0.25 → 0.04) made **no visible difference** — new screenshot of the
same shins, same jagged translucent patches. That is itself the finding: those faces never reach
the `Scissor` branch at all.

`ClassifyAlpha` sends a face to **`Blend`** when `fracMid > 0.06` **or `fracClear > 0.5`**. A
lower-body BoM bake with **two** alpha-layer wearables worn (the user's session: *"Super High
Cutoffs Alpha Layer"* + *"Camden Boots Alpha Layer"*) has most of its texture cleared → over the
50 % line → `Blend`. And every `prim_blend_*` variant is `depth_draw_opaque`, which for a
transparent material means **no depth write** — on `cull_disabled` avatar geometry the front and
back of the same shin, and two alpha layers over the same skin, then have no defined order. That
is the hard-edged translucent patchwork in the screenshots, and it is exactly what the user
described: *"Probleme mit mehreren alpha layern"*. One alpha layer stays under 50 % clear and
classifies as `Scissor` (which does write depth, so it looks fine); two push it over.

- **Fix:** `depth_prepass_alpha` on `prim_blend_avatar.gdshader`. Godot runs a depth prepass for
  the near-opaque part of the surface (cut at alpha 0.99), so solid skin / strand cores establish
  depth like an opaque face while soft edges still blend. Standard remedy for hair and foliage,
  and the same thing StandardMaterial3D's opaque-prepass depth mode does. It cannot reintroduce the
  `v0.20.41` regression: that was `Scissor` faces *losing* depth write, this only gives depth write
  back to faces that had none.
- **New diagnostic `[AvatarAlpha]`** — one deduplicated line per texture per verdict:
  `[AvatarAlpha] <id> bom=<ch> minA=.. fracMid=.. fracClear=.. -> Blend`. Two rounds of
  BUG-RENDER-09 were spent guessing which branch a face took. Now it says so.
- The `v0.20.53` `BomAlphaScissorThreshold = 0.04` is **kept** — it is still correct for the BoM
  faces that genuinely do classify as `Scissor`, it just was not the faces in the screenshot.

### `v0.20.54` — BUG-RENDER-10: Firestorm shows `dda710d4`, so "no transport will get it" was wrong

User confirmed Firestorm renders that hair correctly. So the asset exists and is servable; the
generic `ViewerAsset` cap simply refuses it (`403 from asset-cdn.glb.agni.lindenlab.com/`).
`v0.20.40` had reasoned that a 403 is a permission decision no transport can beat and skipped the
UDP fallback too — that half is now disproven.

`FetchTextureDataAsync` now splits the one set in two: `_httpDeniedTextures` (403'd on HTTP — skip
the caps forever, they will not change their mind) and `_permanentlyDeniedTextures` (403 on HTTP
**and** nothing over UDP — the only state that returns `Gone`). So a CDN-refused texture costs one
UDP attempt through LibreMetaverse's legacy image transfer, the same fallback the reference viewer
uses, and the anti-flicker property is kept because HTTP is never retried and the permanent set
short-circuits everything after the first failed UDP round. Logs
`HTTP 403 but UDP delivered N bytes -- the asset exists, the CDN just would not serve it` when it
works.

### `v0.20.55` — reduce-level decode (BUG-NET-11's last structural lever)

The decoder now gets `screenPixelArea` and skips the wavelet levels the screen cannot show, instead
of decoding everything at full resolution and downsampling afterwards. Measured on 40 real cached
assets: 47.6 ms full, 13.1 ms at quarter size, 3.8 ms at a sixteenth.

`TextureLod` (new, engine-agnostic) is the single copy of the arithmetic — `AssetService` picks the
reduce level from the SIZ header before decoding, `GpuCache` applies the leftover discard after.
`TextureData.SourceWidth/Height` keep a reduced upload eligible for sharpening. Only full decodes
are memoized. Sculpts are never reduced. The degraded check now expects the reduced size, which
mattered: without it every reduced decode would have read as truncated and the disk-cache path
would have deleted the `.j2c` that produced it.

**Honest scope:** this saves decode CPU and transient RAM (16 KB instead of 4 MB per 1024² at
reduce 2), **not VRAM** — object uploads were already downsampled by `screenPixelArea`, which is why
the cache averages ~336 KB/entry. The 2156 MB / 100 %-pinned GPU cache is still open, and avatar
textures still upload full-resolution on purpose. `[TexPipe]` now reports `reduced=` /
`reduceRetry=`.

### `v0.20.56` — "das Backen geht nicht mehr": the sim sent no self appearance, and nothing said so

User screenshot: blank white head, an orange uncut **system-hair helmet**, toes through the boots.
That last one is the giveaway — it is the documented "alpha forced to 255, system hair renders its
full uncut card" state, i.e. **no bake at all**, not a bad bake.

**It is not a regression from the texture work.** Read from the logs, comparing the good v0.20.54
session (18:27) with the bad v0.20.55 one:

| | good (18:27) | bad (v0.20.55) |
|---|---|---|
| `[VisualParams] seeded 253 params from self AvatarAppearance relay` | present | **absent** |
| distinct bake ids resolved | 11 | 6 |
| our own ids (`784033ee` ch8, `7f78129e` ch9, `a4f66bf1` ch10, `1e70f9f4` ch11, `14b37fca` ch42) | present | **all absent** |
| other avatars' bakes | fine | fine |

So the simulator never sent the local agent its own `AvatarAppearance`; every one of our channels
stayed `Guid.Empty`. Bake ids come from that packet, nothing in the decode/upload path can remove
them, and other avatars in the same scene baked normally. `SelfBake=0` also happened on a
**v0.20.53** session (17:55), before any of this round's asset work — it is intermittent, not
version-linked.

Two fixes, both about never losing another round to this:

1. **`[SelfBake]` could not report the state that matters.** It was gated on `anyBakeChanged`, which
   only turns true for a NON-empty id — so "every channel empty" printed nothing at all. Now it logs
   on any signature change, with `-- NO BAKE AT ALL` and what to do about it.
2. **`GridSession.ArmSelfBakeWatchdog`** — one-shot per session, armed at `EventQueueRunning` (when
   the caps handshake is done). If no self `AvatarAppearance` carrying bake ids has arrived 25 s
   after login, it POSTs `{ cof_version }` to `UpdateAvatarAppearance` — the same thing the
   reference viewer does on every login (`LLAppearanceMgr::serverAppearanceUpdateCoro`) — and once
   more 30 s later if still nothing. Explicitly the pure cap POST, **not** `RequestSetAppearance`,
   which drops worn attachments on a rate-limited grid (BUG-AVATAR-03). Two attempts, then it stops.
   SSB regions only; the POST already refuses to send with an unknown `cof_version`.

Also confirmed this session: **reduce-level decode works** — `[TexPipe] req=8000 distinct=4410
reduced=6523 reduceRetry=0`, **avg decode 31 ms, down from ~107 ms**. And the UDP fallback for
`dda710d4` ran and came back empty (`pipeline reported Timeout`), so that texture is now correctly
marked gone for the session; whatever Firestorm shows there, it is not coming from either transport
we have.

### `v0.20.57` — BUG-INV-01's last three pieces

- **All 14 remaining `Callable.From(lambda).CallDeferred()` sites were on worker threads.** Every
  async method in `InventoryPanel.cs` awaits with `ConfigureAwait(false)` and Godot's main thread
  has no `SynchronizationContext`, so every one of them ran the pattern that crashed `GpuCache`
  (fatal `AccessViolationException`) and silently no-op'd "Ablegen" in this very file (`v0.20.34`).
  They covered every Outfits-tab action, the landmark teleport, and the main tree's folder fetch.
  Replaced by one `RunOnMainThread(Action)` helper — `ConcurrentQueue` + `CallDeferred(nameof(
  DrainUiWork))`, which is a StringName dispatch with no delegate marshalling and therefore the
  documented-safe form. FIFO, per-item try/catch.
- **Worn marker**: `✔ ` glyph on top of the gold colour (colour alone was subtle and useless to a
  colour-blind reader), and `ApplyWornMarker` is now one idempotent function shared by the build
  path and by `RefreshWornMarkers`, which walks the tree on every `WornItemsChanged`. Before, a
  marker was only correct at the moment its folder was fetched.
- **Per-folder load indicator**: the `…` placeholder becomes `⏳ lädt…` while a fetch is in flight
  and `⚠ Fehler — nochmal aufklappen` on failure. `SetFolderPlaceholder` only touches a lone child
  with empty metadata, so it can never overwrite real contents.

### `v0.20.58` -- "Outfit aufraeumen" was a permanent no-op

Two attachments showed as worn-but-`(nicht aktiv)` and cleanup would not remove them. The log:
`[OutfitCleanup] deferred - still loading (links=28 unresolved=10 ...)` on a fully-loaded session.

`storeReady` required `linkUnresolved == 0`, which **cannot be satisfied by waiting** --
LibreMetaverse's store only holds folders somebody fetched, so a COF link into a never-opened
folder never resolves. The button deferred every time, forever. (And a COF link carries the target
item's NAME, which is how the Worn tab could list items whose targets the cleanup could not judge.)

`CleanUpCurrentOutfitAsync` now fetches the missing targets first (`RequestFetchInventoryAsync` +
`Inventory.UpdateNodeFor`), then **polls the store** for up to 5 s rather than trusting that call's
completion semantics, then runs the existing cleanup with `targetsResolved: true`. Anything still
unresolved is skipped individually as before, so the relaxed gate cannot delete a link we failed to
understand -- and `sceneReady`, the half that actually caused the v0.20.36 grey-avatar regression,
is untouched.

### What to check in the next live session

1. The shins under two alpha layers: still a jagged translucent patchwork, or solid skin with a
   clean edge? And are BoM **heads** and **hair** still correct (hair is the other big consumer of
   `prim_blend_avatar`, so `depth_prepass_alpha` touches it too).
2. Does that remote avatar's hair (`dda710d4`) now appear? Look for
   `[TextureFetch] … UDP delivered N bytes`.
3. `[AvatarAlpha]` in the log — confirms which branch the shin faces actually take. If they say
   `-> Blend` with a high `fracClear`, the diagnosis above is right.
4. Is the console log back to a normal size?
5. `[TexPipe]` reduce-level decode: **already confirmed** (`reduced=6523/8000`, `reduceRetry=0`,
   31 ms avg). Only remaining question is visual: anything blurrier than it should be when you walk
   up to it would mean `TryUpgradeCachedTexture` is not firing.
6. `[SelfBake]`: should now print on every login. If it says `NO BAKE AT ALL`, watch for
   `[Appearance] the sim has not sent our own bake ids -- nudging a server re-composite` ~25 s
   later and whether a `[SelfBake]` line with real ids follows it.
7. Inventar: `✔` auf getragenen Zeilen, und dass er sich beim An-/Ausziehen **sofort** mitändert
   (auch in dem Ordner, in dem das Item wirklich liegt — nicht nur im Current Outfit). Beim
   Aufklappen eines Ordners `⏳ lädt…` statt `…`. Und die Outfits-Tab-Aktionen (Anziehen /
   Ersetzen / Hinzufügen / Entfernen / Speichern / Umbenennen) müssen alle noch tun, was sie
   sollen — die liefen bis eben alle über das kaputte Marshalling.
8. „Outfit aufräumen": sollte jetzt `[OutfitCleanup] resolved N/10 previously-uncached COF link
   target(s)` loggen und danach wirklich aufräumen statt zu deferren. Die beiden `(nicht aktiv)`
   Anhänge sollten verschwinden.

---

# 2026-09-03 — long live-testing arc on Agni (v0.20.33 → v0.20.50)

One continuous session. **Working tree clean, HEAD `da928ad`, all pushed to `origin/main`.**
Earlier per-fix sections for this day were consolidated into this one; the specs, `docs/ROADMAP.md`
rows and git history keep the detail.

## Shipped & confirmed in-world

- **BUG-INV-01 inventory search** (`v0.20.35`) — search now crawls the subtree (`ContinueSearchCrawl`
  one level per `Populate`), `FilterTree` gained `ancestorMatched` so a name-matched folder opens
  with contents. User: *"geht"*.
- **BUG-INV-01 detach / cleanup** (`v0.20.32`–`.34`) — context-menu id→index crash; COF-link
  removal switched from `MoveItem → Trash` (400s on SL) to `RemoveItemsAsync`; refresh marshalled
  off the `Callable.From(lambda)` anti-pattern. User: *"funktioniert recht gut und schnell"*.

## Shipped, NOT re-verified in-world

- **BUG-INV-01 `CleanUpCurrentOutfit` safety gate** (`v0.20.36`) — `v0.20.33`'s durable delete
  stripped the avatar bake when run on a still-loading COF. Now gated on `storeReady` (every
  link's target resolved) + `sceneReady` (≥1 scene attachment); else `OutfitCleanupResult.Deferred`.
- **BUG-AVATAR-03 SSB rebake** (`v0.20.37` reverted → `v0.20.39` real fix). `RequestSetAppearance`
  reconciles the worn set from a COF fetch and drops worn attachments on a rate-limited grid
  (user lost hair+shoes twice). Now `SendServerAppearanceUpdateAsync` POSTs `{ "cof_version": N }`
  straight to the `UpdateAvatarAppearance` cap (mirrors `LLAppearanceMgr::serverAppearanceUpdateCoro`),
  a pure nudge; `Ctrl+Alt+R` and the reinstated debounced auto-rebake both use it. Log from a
  later session shows `[Appearance] server appearance update accepted (cof_version=31, HTTP 200)`
  → fresh `[SelfBake]`, so the nudge itself works.
- **BUG-RENDER-10 403-denied texture** (`v0.20.40`) — `TextureFetchResult.Gone` on a 403/401 from
  the generic cap (bake path excluded); no LMV UDP-pipeline fallback; session-permanent
  `AssetService._goneTextures`. `v0.20.43` logs the failing host+path (`33192a49` / `dda710d4`
  both `403 from asset-cdn.glb.agni.lindenlab.com/` — the generic CDN, NOT a bake-style URL).
- **BUG-RENDER-10 untextured avatar face** (`v0.20.42` Opaque → `v0.20.44` **invisible**). A face
  referencing a real texture that won't load: `Blend`-from-tint = translucent double-sided card
  that sorts wrong ("hair inside-out"); Opaque = blocky patch. Now renders invisible (albedo α 0)
  until the texture arrives.
- **BUG-NET-11 material-request caps flood** (`v0.20.45`) — `ApplyFaceMaterialsAsync` fired one
  single-id `RenderMaterials` cap POST per face → `Caps rate limiter queue full`. Now prefetches
  every distinct legacy+PBR material id its faces reference in one batch. Confirmed via
  `[TexPipe]`: zero rate-limiting after, `[LegacyMat] POST` lines now multi-id.
- **BUG-NET-11 decode throttle** (`v0.20.48`) — the disk-cache-hit decode was a bare `Task.Run`
  per texture; now `_textureDecodeThrottle` (`PriorityGate(ProcessorCount-2)`), priority-ordered.
- **BUG-NET-11 GPU-cache reuse** (`v0.20.49`) — `GetOrUploadTextureAsync` did
  `cached = rejectDegraded ? null : Get(id)`, so every avatar face bypassed the GPU cache. Now
  only a **degraded** cached upload is bypassed (`_uploadFromDegraded`, set from
  `textureData.IsDegraded`). **Did NOT fully fix it** — see below.

## Reverted this session (do not re-apply without the noted change)

- **BUG-AVATAR-03 auto-rebake `v0.20.37`** (`git revert df99875`) — auto-fired `RequestSetAppearance`
  after every wearable edit → worsened the attachment loss. The `v0.20.39` cap-POST path is the
  safe replacement and the auto-rebake is back on top of that.
- **BUG-RENDER-09 BoM `Scissor → Blend` `v0.20.41`** (reverted `v0.20.46`) — also caught BoM
  head/body faces (their bake has a soft neck-blend alpha → `Scissor` verdict) → `Blend` on
  `cull_disabled`/no-depth-write → blocky see-through chunks across the face. **`Blend` is off the
  table for any BoM face.**

## OPEN — the three the user is still hitting (all bigger pieces, no quick fix)

### 1. Textures still slow — `[TexPipe] req` climbs past 8600 for a static scene

`v0.20.49` did **not** stop the re-decode loop. `[TexPipe]` (v0.20.47) shows `req` climbing into
the thousands with `diskCacheHit ≈ req` (~99.9 %) and `inflight` spiking to 1000+ then draining —
i.e. the same ~200 textures decoded ~13× each. `v0.20.50` adds `[TexPipe] distinct=N` and a
`[GpuCache] get/hit/bypassDegraded/entries/sizeMB/degradedIds` line every 200 gets — **the next
`v0.20.50` session log will show whether `GetOrUploadTextureAsync` is hitting its own `_cache`
and why not.** Leads to check: `AvatarRenderer` has **no `AddRef`/`ReleaseRef` bookkeeping**
(comment at `AvatarRenderer.cs:893`) — it relies on `initialRefCount: 1` pinning; if the pin
isn't holding (or `_pendingRefDelta` folds a stray `ReleaseRef` to 0 on `Put`), avatar textures
evict and re-decode. `AssetService._memCache` is only 256 MB (`Size = W·H·4`, ~60×1024²) so it
thrashes and gives no RAM rescue either.

Structural regardless of the loop: SLNG re-decodes J2K (~86–99 ms each, OpenJPEG via Magick.NET,
measured post-throttle so that's the real cost). Firestorm uses **KDU** (SIMD, ~10–20×) **plus a
decoded-texture cache** (stores the decoded/transcoded result, skips J2K on a hit). Next
structural steps: (a) a **decoded / BC7 disk cache** keyed by texture id, or (b) **reduce-level
decode** (decode fewer wavelet levels for the first display, sharpen on demand — `cp_reduce`).

### 2. Other avatar's hair — `dda710d4` genuine 403

`403 from asset-cdn.glb.agni.lindenlab.com/` — the generic CDN, confirmed. Not a wrong-URL case
like BUG-AVATAR-02's bakes. Either a dead / non-persisted (local-only) texture, or a real
permission block. `v0.20.44` renders it invisible. **Need one data point: does Firestorm show
that exact hair correctly?** Yes → HTTP-capture SLNG vs FS for `dda710d4`. No → nothing to fix.

### 3. Self avatar alpha — "2 alpha layers overlapping" render wrong

Same root as BUG-RENDER-09's banding: two alpha-blended avatar surfaces overlapping have no
reliable depth order in Godot's transparent queue (no depth write). `Scissor` bands a soft
gradient; `Blend` broke heads (reverted). The answer is a **`prim_hash_avatar` shader variant**
(alpha-hash + depth write): dithered but depth-correct, no hard edge, no sort failure. New shader
+ `check_shader_globals` + selftest + a live A/B. Filed in `docs/specs/BUG-RENDER-09-*.md`.

## Filed, not started

- **FEAT-ANIM-01** — self-avatar locomotion prediction (walk anim lags the server echo under
  load; `AvatarController` should drive the built-in gait locally).
- **FEAT-INV-05** — per-item actions in the Outfits view.
- **BUG-INV-01 remaining** — visible worn marker in the main tree, per-folder load spinner, the
  ~15 other `Callable.From(lambda)` sites in `InventoryPanel.cs`.

## Diagnostics currently in the build (remove once their questions are answered)

- `[TexPipe]` (v0.20.47/.50) — texture pipeline: req / distinct / diskCacheHit / decode ms / http / inflight.
- `[GpuCache]` (v0.20.50) — get / hit / bypassDegraded / entries / sizeMB / degradedIds.
- `[TextureFetch] … 403 from <host><path>` (v0.20.43).

---

# 2026-09-02 — BUG-INV-01 progressing + FEAT-INV-05 filed

**BUG-INV-01 fixes shipped & confirmed** (*"Funktioniert recht gut und schnell jetzt"*):
- `v0.20.32` context-menu id→index (Detach was greyed / `Index 6 out of bounds` crash).
- `v0.20.33` `CleanUpCurrentOutfit`: `MoveItem(link → Trash)` **400s on SL** (user log:
  `Move item … to <Trash>: Bad Request (400)`) — a COF link cannot be *moved* to Trash. Switched
  to `RemoveItemsAsync()` (AIS `DELETE` on SL, `RemoveInventoryObjects` packet on OpenSim), the
  same operation the reference viewer uses.
- `v0.20.34` `DetachWornAsync` / `DetachAndRefreshAsync` / `AttachAndRefreshAsync` were
  `Callable.From(lambda).CallDeferred()` from a worker thread (`BUG-RENDER-01` anti-pattern) —
  detach packet went out but the refresh silently never ran, so "Ablegen" looked dead. Now
  `CallDeferred(nameof(Finish…))`.
Still open in BUG-INV-01: the visible worn marker in the "Inventar" tree, and a per-folder load
spinner; plus the ~15 other `Callable.From(lambda).CallDeferred()` sites in `InventoryPanel.cs`
(mostly the outfit methods). Search crawl fixed in `v0.20.35` (see 2026-09-03 section above).

**FEAT-INV-05 filed (not started):** per-item actions in the Outfits view (Anziehen / Ausziehen /
Aus diesem Outfit entfernen when right-clicking an item inside an expanded outfit).
`docs/specs/FEAT-INV-05-outfit-item-actions.md`.

---

# 2026-09-02 — BUG-INV-01 filed (not started)

User reported three inventory problems in one go; filed as `BUG-INV-01` (Medium),
`docs/specs/BUG-INV-01-inventory-worn-state-and-load-ux.md`, ROADMAP row added. No code yet.
1. "Inventar" tab has no visible worn marker (the main tree *does* gild worn rows via
   `GetWornItemsMap()` but faintly and only for already-expanded folders).
2. "Angezogen" tab "Ablegen" does nothing — prime suspect: `DetachWornAsync` in
   `InventoryPanel.cs` does `await …DetachItemAsync().ConfigureAwait(false)` then
   `Callable.From(lambda).CallDeferred()` on the resulting worker thread — the `BUG-RENDER-01`
   anti-pattern (`[[godot-callable-from-not-threadsafe]]`).
3. Inventory loads slowly with no spinner / progress anywhere.

---

# 2026-09-02 (cont.) — BUG-RENDER-08: particle system pass (3 fixes shipped, 2 low-pri gaps)

`v0.20.31-alpha`, on `main`. `app/` build clean (0 warnings), `--selftest` 29/29. Only `app/`
changed (`ObjectParticles.cs` net ~290 lines, `ObjectRenderer.cs` one line, `Boot.cs` version).

Long live session on Agni vs Firestorm, orange lantern flame + a blue-flame prim running the
static **"SLS Particle Script 0.3" by Ama Omega** (user pasted the full script — that's what
proved the remaining issues are SLNG-side, not script jitter).

**Fixed & confirmed in-world:**
1. *"zu breit"* on a square-ish particle — `ScaleCurveZ` was `BuildScaleCurve(1f,1f)` while X/Y
   held the ~0.06 m sizes; `BillboardKeepScale` multiplies MODELVIEW by
   `diag(len col0, len col1, len col2)`, and col2=1 vs ~0.06 is a ~16:1 anisotropy → horizontal
   stretch. `ScaleCurveZ` now tracks X. → *"die flamme sieht jetzt gut aus."*
2. *"an/aus"* strobe — `Restart()` (pool wipe) on every re-send + `_sourceAge` only re-armed on a
   structural change, so a ~10 Hz re-send with a short `PSYS_SRC_MAX_AGE` expired between sends.
   `Restart()` now first-Apply-only (viewer keeps emitted particles alive, only the source is
   rebuilt — `LLViewerObject::setParticleSource`); `_sourceAge` re-armed every Apply.
3. *"in FS one morph ≈ 1 s, in SLNG 5 loops"* — `Apply` re-assigned `CpuParticles3D.Amount` (+
   `Lifetime`/`Preprocess`/`ConfigureEmission`) every re-send; assigning `Amount` in Godot
   rebuilds the buffer and deactivates every live particle. Pool-shaping props now set **only on a
   structural change**; scale/colour re-send re-points persistent curves/gradient in place. →
   *"es morpht jetzt langsam."*

Also: `FixedFps = 0` (default 30 batches emission+death into 30 steps/s), gentle τ≈1.2 s
shared-endpoint ease, always-on `[Particles]` diagnostic (`obj=`, scale/flags/accel/srcMaxAge/
curve values, `albedo resolved WxH`).

**Still open, LOW priority** (decorative, no crash/data/correctness impact — see the spec):
- (A) blue-flame (`4a548641`, non-square `endSize <.5,1.0>`) still renders wider than FS. Split
  curves log the right values, texture is ~square, node scale unity — and **swapping X/Y in the
  scale curves had zero visible effect**, so the width isn't from the curves. Unresolved
  `CpuParticles3D` + `BILLBOARD_PARTICLES` + `keep_scale` interaction; needs RenderDoc / live
  Godot inspection. Also check particle-quad UV V-flip and the `PSYS_SRC_OMEGA` node-spin path.
- (B) no per-particle X/Y morph — architectural: one shared scale curve, no per-particle birth
  snapshot. The current shared ease approximates it pool-wide only.
- (C) real fix for A+B (and `PSYS_PART_FOLLOW_VELOCITY`): a dedicated particle path —
  `GpuParticles3D`+process shader or a hand-managed `MultiMesh` pool. Scoped task, not a tweak.

`docs/specs/BUG-RENDER-08-particle-fidelity.md`. **`BUG-AVATAR-02` (424ea1a) and `BUG-RENDER-06`
(41824b3) are already committed + pushed to `origin/main`.**

---

# 2026-09-02 (cont.) — other people's mesh bodies rendered white on Agni

`v0.20.12-alpha`, on `main`. Solution + `app/` build clean, **567 tests green**
(Core 195, Assets 79, Net 293), `dotnet format` clean, shader globals 27, `--selftest` 29/29.

**Symptom:** the 18:48 `client-output.log` from an Agni session was flooded with
`[FaceTex] … fetch/decode returned null` — 214× `8a2f74fd`, 135× `27d8904e`, 78× `333778c6`,
75× `2cae1bdb`, plus `warn: Failed to fetch texture … over HTTP: Forbidden`. **None of those
ids were self bake channels** (`[SelfBake]` = `8=784033ee 9=9965f08e 10=e1baf1d1 …`, all fine).
The self avatar textured correctly; every *other* mesh-body avatar was white.

**Root cause:** `GridSession.FetchBakeTextureDataAsync` hardcoded `_client.Self.AgentID` in the
bake-texture CDN URL. Confirmed against viewer source this time (`llvoavatar.cpp` `getImageURL`):
`url = appearance_service_url + "texture/" + getID().asString() + "/" + mDefaultImageName + "/"
+ uuid.asString()` — `getID()` is the **displayed** `LLVOAvatar`, not `gAgentID`. Asking
`…/texture/<our-id>/<slot>/<their-texture>` → 403, then the generic fallback → 403, face left
untextured, and it retried in a loop (bake path has no negative cache).

**Fix:** thread the wearing avatar's id down — `AvatarVisual.AgentId` (from
`AvatarComponent.AgentId`) → `LoadAndApplyTextureAsync` / `BuildFaceMaterialAsync` →
`GpuCache.GetOrUploadTextureAsync(bakeAgentId:)` → `AssetService.GetBakeTextureAsync` →
`FetchBakeTextureDataAsync(…, Guid agentId = default, …)` (empty → falls back to self). URL
construction extracted to `GridSession.BuildBakeTextureUrl` (pure, `internal static`);
`BakeTextureUrlTests` (3) pin that the wearer's id, not the viewer's, is in the path.
[Spec](file:///E:/Git/SLNG/docs/specs/BUG-AVATAR-02-bake-texture-403.md) has a "Follow-up" section.
**Not yet re-verified in-world.** Open on the same spec: the `appearance_service_url` base is
still built from the parsed grid name, not read from the login response's `agent_appearance_service`.

---

# 2026-09-02 (cont.) — BUG-RENDER-06: the conifer canopy flicker, actual root cause

`v0.20.15-alpha`, on `main`. sln + `app/` build clean, **570 tests** (Core 195, Assets 79, Net 296),
`dotnet format` clean, shader globals 27, `--selftest` 29/29.

The double-sided-material fix (`v0.20.7`) was real but not why the reported tree flickered. New
diagnostics settled it live on Agni:

- `[FaceAlpha]` (always on) — one line per world-prim face: which signal chose the alpha mode
  (legacy material / glTF material / tint / `DetectAlpha()` guess) and the shader Kind.
- `[FaceParams]` faces line now prints `mat=<id>` / `pbr=<id>` per face.
- `[LegacyMat]` on the RenderMaterials fetch: `requested N -> resolved M (cap present/MISSING)`.

The tree's 45 leaf faces all carry legacy material `6ad7601a`, but **every `[LegacyMat]` line was
`resolved 0`, cap present, no error** — region-wide, not one Blinn-Phong material resolved. The leaf
diffuse then went `DetectAlpha()=Blend` → `prim_blend` → the sorted transparent pass, whose
per-object centroid sort flips as the camera orbits → flicker. Firestorm uses the real material
(Alpha-Masking) → opaque/tested pass → stable.

**Root cause:** LibreMetaverse 3.1.3's `RequestMaterialsAsync` serialises each id in the
RenderMaterials query as an LLSD `uuid` element. The real viewer sends **binary(16)**
(`llmaterialmgr.cpp` `processGetQueue` → `LLMaterialID::asLLSD()`). SL's sim reads the entries with
`.asBinary()` → nothing for a `uuid` → zero matches, empty result, no error. Worked on OpenSim
(lenient) the whole time.

**Fix:** `GridSession.BuildRenderMaterialsQuery` builds the zipped query itself with
`OSD.FromBinary(id.GetBytes())` per id and POSTs directly via `HttpCapsClient` (same pattern as
`BUG-NET-07`). Response shape unchanged — LMV's `LegacyMaterial(OSDMap)` still parses each entry.
`RenderMaterialsQueryTests` (3) pin binary(16), not `uuid`. **Confirmed in-world 2026-09-02** —
user, orbiting the same conifer: *"yay es blinkt nicht mehr!!!"*. Spec:
`docs/specs/BUG-RENDER-06-double-sided-material-culling.md` (new section at the bottom). Latent
follow-up noted there: material-free soft-alpha faces still land in the sorted transparent pass, so
a purely textured (no-material) overlapping-alpha object could still sort-flicker.

---

# Six ways to bake nothing, and the test that could not see any of them

**State:** `v0.19.3-alpha`, **on `main`**, 2026-09-01. Solution and `app/` both build clean,
**490 tests green** (Core 173, Assets 79, Net 238), `dotnet format` clean, shader globals
consistent (27 registered), `--selftest` 26/26.

**37 commits pushed, `d568077..db71623`.** `git log --oneline d568077..db71623` reads them in
order; each carries its own reasoning and measurement, so the commit messages are the record
and this file is the map.

The session closed MVP 2, then spent most of its length on **FEAT-AVATAR-01**, which turned
out to be six independent defects stacked on top of each other. It is **not Done** — see §3,
which is the most important section here.

---

## Build and verify

`/slng-verify` covers it. The trap that matters: `dotnet build SLNG.sln` compiles **none** of
`app/`, so build both. `godot --headless` rewrites `app/project.godot` (drops
`directional_shadow/size=4096`) — revert before staging.

---

## 1. MVP 2 is closed

BUG-UI-05, FEAT-UI-18 and BUG-NET-03 all confirmed in-world. BUG-NET-03's root cause was
`Settings.Agent.MultipleSims` defaulting to false in LMV 3.1.3, not the render stack. Two
follow-ups came out of it and are also in: the 1 m terrain seam between regions (the mesh was
built only to `width-1`), and an `ObjectDisposedException` storm once neighbour sims started
streaming avatar updates. Also landed: FEAT-ENV-02's preset picker, and a teleport overlay.

## 2. FEAT-AVATAR-01 — the bake works, and here is why it did not

SLNG now composites, encodes, uploads and applies its own avatar bake, and the result is
visible without a relog. Confirmed in-world: *"das Baken funktioniert jetzt"*.

Six separate defects, none guessable from the symptom. Each is pinned by tests:

1. **CoreJ2K 2.3.3.91 has no working lossy encoder.** Every preset flattens a 512² gradient to
   ~292 bytes, and `WithBitrate`/`WithQuality` are **inert** — 244 bytes at any setting. This
   is why LibreMetaverse's `AssetTexture.Encode` produced 507-byte blanks for months. Only
   `ForLossless()` works. → `IBakeTextureEncoder` (Net) / `J2KBakeTextureEncoder` (Assets),
   wired in `Boot`. The tell was that channels with wildly different content produced
   *byte-identical* sizes.

2. **`AppearanceManager` keeps one `TextureData` per texture slot** and calls
   `DecodeWearableParams` once per wearable into the same array, so five worn Tattoo layers
   overwrote each other — four HeadTattoo textures lost, and the last one's `DEFAULT` emptied
   the slot entirely. The head baked the built-in Linden skin. `Baker` itself is fine:
   `AddTexture` appends to a flat list. The viewer keeps a texture per *(slot, wearable)* —
   `LLLocalTextureObject`. Each wearable now decodes into its own scratch array.

3. **`Baker`'s built-in layers are 512² while it composites at 1024²**, and `DrawLayer` walks
   one flat index over both — 512 source pixels laid across each 1024-pixel row, then a bounds
   check silently stopping a quarter of the way down. The head bake held the face twice,
   sheared, with a diagonal seam. → `BakeResourceLayers` rewrites them at bake size.
   **Trap:** `Baker.LoadResourceLayer` **caches**, so it cannot witness its own fix — it kept
   reporting 512×512 after the file on disk had become 4,194,322 bytes.

4. **Layer order lives in the COF link's description**, not in `GetWearables()` order:
   `build_order_string` → `@` + type*100 + index, sorted by `WearablesOrderComparator`
   (`llappearancemgr.cpp`). Index 0 is the bottom (`gatherAlphaMasks` takes `num_wearables - 1`
   as "the top wearable"). → `WearableLayerOrder`. And `WearWearableAsync` now **writes** that
   token, so anything worn in SLNG lands on top instead of at the bottom of its stack.

5. **The worn set must come from the COF.** The legacy `AgentWearablesReply` is only what the
   region was last told — a Firestorm login rewrote it from 9 wearables to 5, dropping the
   tattoo layer carrying the skin actually being worn. Duplicate COF links are de-duplicated,
   keeping the tokened one (this account had **20 links for 10 wearables**).

6. **CoreJ2K wraps its output in JP2 boxes.** Every SL texture is a raw codestream starting
   `FF 4F FF 51` — verified by parsing an asset out of SLNG's own cache (2048², 4 components,
   MCT=1). Our uploads began `00 00 00 0C 6A 50 20 20`. The grid **accepts, stores and serves**
   such an asset, and it renders flat grey in Firestorm while decoding perfectly here. →
   `WithFileFormat(false)`.

Alongside those: `AgentAppearanceParams` builds the visual-param array in wire order
(LibreMetaverse mis-orders 195 of 218 and truncates 35 — `VisualParamOrderTests`);
`AvatarAppearanceReceived` is raised locally after our own send, because the sim does not echo
one and that event is the only path carrying bake ids into the scene; and **body parts now
replace rather than layer, and cannot be taken off** (raised in review — neither rule existed,
so removing a shape was possible and wearing a second one stacked it).

Controls: `SLNG_BAKE_VERBOSE=1` for the full trace and PNG previews of every input and
composite, `SLNG_BAKE_DRY=1` to composite without touching the account, and
*Developer → Testmuster backen*, which bakes generated known-answer textures (head green,
upper blue, lower red, with a grid, a diagonal, a brightness ramp and four corner markers)
with **no inventory involved**.

## 3. FEAT-AVATAR-01 is NOT Done — start here

It was marked ✅ after one successful in-world bake and rolled back the same session, on the
user's objection: *"nur weil ich einmal das bake gesehen hab heißt nicht, dass das Feature
geht"*. That was correct. Open, in the roadmap entry too:

- **(a) The upload read-back has never been observed.** `verified WxH` has not appeared in a
  log once. This matters more than it looks: `MergeBakeSlots` fills empty slots from the
  simulator's relay, so a *silently failing upload still produces a correct-looking avatar*,
  for the wrong reason. Every successful observation so far also had a Firestorm bake on the
  account to fall back on. `v0.18.7` added the check; nobody has run it.
- **(b) Alpha wearables never reach the bake.** `[XBakes]: Number of alpha wearable textures:
  0` on **every** run. An alpha layer therefore cannot hide anything — which is the thing M4-7
  needed this task for in the first place. A real functional gap, not a test.
- **(c)** Repeatability — wear, bake, remove, bake, several times.
- **(d)** Persistence across a relog with no other viewer involved.
- **(e)** A **third party** must see the avatar correctly. Every check so far used two viewers
  on the same account, which could be reading the same cache.
- **(f)** A cold account, with no Firestorm bake as a safety net.
- **(g)** The body-part rules from §2, in-world: take-off refused, wearing replaces.

**(d) and (f) are not measurable right now** — see §5.

## 4. Inventory icons (FEAT-UI-16)

Items and folders in the inventory tree carry their type's icon; system folders (Trash,
Current Outfit, Favorites, Inbox…) are findable at a glance. One shared table,
`app/scripts/UI/InventoryIcons.cs` — the Outfits tab had its own coarser set of four, so the
same item could show a different glyph depending on the list. HUDs keep their own glyph via
`ForWornItem`, since they are Objects by asset type. The wire values it keys off joined
`AssetTypeIds` in `SLNG.Core`, alongside a new `FolderTypeIds`.

## 5. OSGrid lost inventory — reinterpret several findings

Reported late in the session: an OSGrid service bug wiped inventory, which is also why the
default avatars are gone. This weakens several conclusions above. The test skin not surviving
a relog, `legacy-not-stored` on the Bodypart create, Body Parts holding three items, 20 COF
links for 10 wearables — all consistent with grid damage rather than defects here.

What SLNG can repair: duplicate COF links (done, in *"Outfit aufräumen"*). What it cannot:
lost inventory, and the **default avatars live in the grid-owned Library**, not the user's
inventory — that is OpenSim-side. Proposed but unbuilt: generate replacement default body
parts locally, since the test skin proved SLNG can create wearables end to end.

## 6. Next: Second Life, not OpenSim

New priority, stated at the end of the session. **On SL the bake is server-side** — the whole
client-side bake of §2 is the OpenSim path and is not needed there. What *does* carry over,
and matters more on SL: the COF-based worn set, the layer-ordering tokens, the body-part
rules, the duplicate cleanup, and the whole render side (BoM channel binding, bake fetch,
local appearance refresh). Open item (a) becomes meaningless on SL; **(b) does not**.

To reach the beta grid (Aditi) without breaching the TPV Policy — read from the policy, not
from memory (<https://secondlife.com/corporate/third-party-viewers>):

- **Directory listing is not required to connect** (§6); it only matters for distribution, as
  do the disclosure duties in §1.c.
- **Already satisfied:** `Boot.cs` reports the real `AppVersion` (§1.b half), shown on the login
  screen.

**All findings so far are closed in code — `FEAT-SL-01`, `v0.20.0-alpha`, branch
`feature/FEAT-SL-01-second-life-readiness`.** Full detail in
[the spec](file:///E:/Git/SLNG/docs/specs/FEAT-SL-01-second-life-readiness.md); the short version:

- **§1.f was a live violation, not a missing feature.** LibreMetaverse's `LoginParams`
  constructor defaults `AgreeToTos` **and** `ReadCritical` to **true** — verified by reflection
  against the pinned 3.1.3 assembly — and SLNG never touched either, so every login it has ever
  sent asserted acceptance of that grid's Terms of Service, sight unseen. Both now default to
  false on `LoginCredentials` and are set only by a retry following a real acceptance in
  `TermsOfServiceWindow`, exactly as `lllogininstance.cpp` does it ("Always false here. Set true
  in handleTOSResponse").
- **A second defect fell out of it:** `LoginWithResponseAsync` returns the parsed response *only*
  on success, so every failure reached SLNG as a bare null reported as `"no-response"` — a wrong
  password and a ToS refusal were indistinguishable, and the gate could never have fired. The
  reason is now read back off `NetworkManager.LoginErrorKey`.
- **§1.g:** an About window (`SLNGWindow`), reachable from the login screen and App → About,
  carrying the same name/version/channel the grid is told.
- **Aditi** is a grid entry, listed ahead of Agni. It has **separate passwords** — the account is
  a periodic copy, and the password is whatever it was at copy time.

**A second pass over §2/§5, requested explicitly as a follow-up audit, found two more findings
that were live, not merely absent:**

- **§2.b, twice.** The inventory "Export (Full Perm)" context-menu entry was gated on
  copy/modify/transfer alone — exactly the shortcut §2.b calls out by name as not exempting
  "full permissions" content, since it does not verify the SL creator name matches the viewer
  user's own. Removed (it had no handler behind it — a dead menu item). Separately,
  `SLNG_BAKE_VERBOSE=1` wrote every DECODED wearable texture feeding a bake — other people's
  skins, tattoos, clothing — to `%TEMP%\slng_bake\*.png`, with no creator check at all. Both were
  harmless on OpenSim and invisible there; both are now refused outright on a Linden grid.
- **§1.a, pre-emptively.** The manual client-side bake ("Avatar neu backen", "Testmuster backen")
  and the test-skin generator never checked `RegionHasServerSideBaking()` — the same check the
  wearable-edit path already makes correctly. Unguarded, both would run SLNG's OpenSim (XBakes)
  composite-and-upload path against a region that already composites bakes server-side: an
  unrequested protocol departure, and for the test-skin generator specifically, real L$ upload
  fees spent on a diagnostic tool built for OpenSim. Both now refuse with a chat message on SSB.
- **§5.b.** The channel sent at login — the "viewer identifier" a sim log actually shows — was
  `"SLNG"`, which starts with "SL" and fails the trademark-fragment rule on the letter even
  though nothing about it was ever meant to imply a Linden Lab connection. Renamed to `"Puris"`,
  the viewer's real public name. `BuildInfo.Name`, the local chat-log directory name, and the LMV
  logger category are left as `"SLNG"` — none of them is grid-facing or user-facing.

**Update, same session: a login to Aditi happened and reached the world** (chat, travel/teleport
activity observed) — the first real confirmation this branch connects to a Linden grid at all.
Not separately confirmed: whether the login actually went through the ToS gate (an account
already accepted via another viewer would never see it), and whether the two
`RegionHasServerSideBaking()` refusals or the §2.b bake-dump refusal have fired for real — none
of that was exercised this session. What WAS exercised, and found broken, is covered in
`FEAT-SL-02` (Age Settings showed "grid doesn't support it" on Aditi, a stale-capability-read
bug, fixed same session) and `BUG-RENDER-01` (a fatal crash, also found live, also fixed — see
its own note below).

**Also added, requested directly: "Age settings"** — `FEAT-SL-02`, same branch, same version.
Second Life gates regions/content by a content-rating preference (General/Moderate/Adult) SLNG
never had any control for; without it an account defaults to General and cannot see
Moderate/Adult regions regardless of what it's actually verified for. Not a TPV gap, a real
missing feature. Confirmed against the reference viewer's own source: login's
`agent_access_max` is the account's verified ceiling, `agent_region_access` its current
preference, and changing it POSTs to the `UpdateAgentInformation` capability, which echoes back
what the server actually granted (it clamps an unverified account's request down, same as
`LLAgentAccess::setMaturity`). New "Age Settings" preferences tab, `GridSession.
AccountMaturityMax`/`PreferredMaturity`/`SetPreferredMaturityAsync`. **OpenSim has no
equivalent** — no `UpdateAgentInformation` capability anywhere in its source — so the tab says so
there instead of showing a control that would do nothing.

**Tested on Aditi the same session it shipped, and it was wrong:** the tab said "grid doesn't
support it" — on a grid that does. `SupportsMaturityPreference` reads the region's capability
list live, but the page was only ever refreshed once, right after login, before the capability
seed necessarily resolves (the same `SimConnected`-vs-`EventQueueRunning` race `GridSession`
already documents for the environment capabilities — never applied here). Fixed by refreshing
again every time Preferences opens, matching the codebase's existing pattern for the same class
of problem. [Spec](file:///E:/Git/SLNG/docs/specs/FEAT-SL-02-age-settings.md) has the "Found live
on Aditi" section.

**`BUG-RENDER-01`, same session, more serious: a fatal crash.** Reported mid-session on Aditi —
`System.AccessViolationException` inside `godotsharp_callable_call_deferred`, stack through
`GpuCache.FetchAndUploadTextureAsync`, taking the whole process down. This project's own
documented trap ([[godot-callable-from-not-threadsafe]]): `Callable.From(lambda).CallDeferred()`
is not safe off the main thread, and this method is reached from asset-decode worker threads,
never the main thread. **Auditing every `Callable.From(` in `app/scripts/*.cs` found the same
anti-pattern at 13 MORE sites** across `AvatarRenderer.cs` and `ObjectRenderer.cs` — bake apply,
rigged-mesh skin, HUD geometry/materials, animation apply, legacy normal/specular maps, every
glTF PBR channel. One had crashed; the other 13 were the same fuse, unlit. The fix already
existed in the codebase — `MainThreadWorkQueue`, a thread-safe budgeted queue, was built for
exactly this and sits a few dozen lines above the crashing method in the same file, already used
correctly by its neighbour (`TryUpgradeCachedTexture`) — it was just never applied to these 14
call sites. All 14 converted; no behaviour change beyond thread safety, every lambda body
untouched. **Not yet re-verified against the traffic that produced the original crash.**
[Spec](file:///E:/Git/SLNG/docs/specs/BUG-RENDER-01-callable-from-crash.md).

**`BUG-NET-04`, same session, third live finding: teleport left the old sim rendered.** *"nach
dem Teleport sehe ich noch die sim auf der ich gerade war also auch die objekte."* BUG-NET-03's
cleanup (grid sends `DisableSimulator` → `World.RemoveRegion`) only really works for a walking
border-crossing — the old region stays a genuine neighbor circuit and the grid decides when to
drop it. A teleport is different: `NetworkManager.Connect(..., setDefault: true, ...)` (what
fires on `TeleportFinish`) never disconnects the old `CurrentSim`, it only swaps the pointer, so
cleanup depended entirely on the ORIGINATING sim eventually sending `DisableSimulator` — not
guaranteed promptly for a teleport to a distant region. Fixed with a new `GridSession.
OnSimChanged` on LibreMetaverse's `Network.SimChanged` (fires exactly when `CurrentSim` changes,
carries the previous simulator): removes the old region EAGERLY through the same
`RegionDisconnectedReceived` path, but only when it's more than one region-grid step away — an
adjacent region is left entirely to the existing, working `DisableSimulator` path so this can't
race BUG-NET-03's own cleanup. `v0.20.0-alpha`, 12 new tests. **Not yet re-verified in-world.**
[Spec](file:///E:/Git/SLNG/docs/specs/BUG-NET-04-teleport-leaves-old-region-rendered.md).

**`BUG-AVATAR-01`, same session, fourth live finding: "das BOM fehlt" — self avatar head/hands
rendered blank white**, screenshot confirmed. Traced to "Avatar neu backen" (Ctrl+Alt+R) being a
COMPLETE NO-OP on every grid, SL included: a 2026-08-31 hard stop refuses
`AppearanceManager.RequestSetAppearance()` unconditionally, because on the legacy (client-side
baking) path it sends whatever `MakeAppearancePacket` produces — a scrambled/all-zero bake from a
cold state, which is what broke this avatar repeatedly back then. That does not hold on a
server-side-baking region: verified by reading the pinned package's own branch
(`RequestSetAppearanceAsync` on SSB calls `UpdateAvatarAppearanceAsync`, a `{ cof_version }`
capability POST that never reaches `MakeAppearancePacket`). Fix: `RebakeAvatar()` now branches on
`RegionHasServerSideBaking()` and calls `RequestSetAppearance(forceRebake: true)` directly on SSB;
the OpenSim hard stop is untouched. **Tried in-world, same session: avatar is STILL blank after
this fix.** So the no-op was real and worth fixing, but not the (or not the only) cause. **Why it
went blank in the first place is still genuinely open** — next step, not yet tried:
`SLNG_LMV_DEBUG=1`, watching for LibreMetaverse's "Dropping stale AvatarAppearance for self" line
(a COF-version staleness guard that silently discards an inbound self `AvatarAppearance` packet
if its version isn't newer than LMV's own internal tracker — `Logger.DebugLog` only, easy to
miss). If that line never appears either, the packet may not be arriving at all, which is a
different investigation.
[Spec](file:///E:/Git/SLNG/docs/specs/BUG-AVATAR-01-rebake-noop-on-ssb.md).

**`BUG-RENDER-02`, same session, fifth live finding — WRONG DIAGNOSIS, corrected same session by
`BUG-RENDER-03` below.** First report: "the water plane overwrites textures", clarified as
*"das mit dem wasser tritt auf der ganzen sim bei den bäumen auf"* — every TREE, sim-wide. Read
that as SL system trees (Tree/NewTree/Grass pcodes) — `PrimMeshService` gives those a crossed-planes
placeholder mesh, and `ApplyFaceMaterialsAsync` fetched `prim.TextureId` for it as a real texture,
meaningless for that pcode. Fixed that (new `SLNG.Core.PrimPCode`, opaque-placeholder
short-circuit) — a real, separate bug, kept — **but the user corrected it directly: "das hat nix
mit den SL Bäumen zu tun das sind mesh bäume und die Blätter sind Texturen mit Transparenz."**
Mesh trees, alpha-blended leaf textures, nothing to do with system-tree pcodes at all. Lesson: the
"sim-wide, at trees" clue was right, the mechanism guessed from it was wrong — should have compared
shader `render_mode`s before reaching for the tree-geometry code.
[Spec](file:///E:/Git/SLNG/docs/specs/BUG-RENDER-02-tree-water-like-artifact.md) (now carries a
correction note pointing here).

**`BUG-RENDER-03`, same session, the ACTUAL fix for the water/tree report.** Grepped every
shader's `render_mode`: `prim_opaque`, `prim_blend`, `prim_scissor` and every `_avatar`/`_hud`
variant all use `depth_draw_opaque` — `water.gdshader` was the ONE outlier, `depth_draw_always`,
no comment, nothing depending on it. `depth_draw_always` forces water to write depth
unconditionally; Godot's transparent-pass order between two SEPARATE transparent objects (water,
a tree's alpha-blended leaf mesh) is only an approximate object-level sort, not a guaranteed
per-pixel one — wherever water rendered first in that approximate order, its forced depth write
could fail the depth test for leaf fragments that were meant to BLEND with it, not be occluded,
discarding them outright: a hole cut at the water's height, on every tree crossing it, sim-wide.
Fix: `water.gdshader` → `depth_draw_opaque`, matching every sibling shader's own convention.
`--selftest` confirms it still compiles (11 uniforms, unchanged). **Not yet re-verified
in-world.** [Spec](file:///E:/Git/SLNG/docs/specs/BUG-RENDER-03-water-depth-draw-always.md).

**`BUG-NET-05`/`BUG-NET-06`, found via HTTP Toolkit (MITM proxy) in a parallel debugging
session, same day.** Two kinds of console spam on Aditi: repeated
`EnvironmentSettings GET non-success: ServiceUnavailable` (Aditi's legacy Windlight cap looks
like a permanent 503 stub post-EEP) and repeated `Failed to fetch texture <id> over HTTP:
Forbidden` for the same id. The env-poll half was fixed correctly: a 60s backoff in
`RepollEnvironmentLoopAsync` on a `ServiceUnavailable` response.

**The first attempt at the texture half was a regression, found and corrected in THIS session
right after.** It set `Settings.TexturePipeline.UseHttpTextures = false` — which does nothing
for the actual spam (`FetchTextureDataAsync`, SLNG's own world-object fetch, never reads that
flag at all) but DOES gate every avatar-BAKE texture fetch:
`GridClientBakingTextureProvider.RequestTextureAsync` → `RequestImageAsync` →
`RequestImageInternal`, which falls back to the legacy UDP pipeline when the flag is off —
exactly the path this same file's own adjacent comment already documents as unreliable ("UDP
transfers time out and hand back truncated JPEG2000 streams... white untextured objects"). A
real risk of making `BUG-AVATAR-01`'s blank avatar WORSE while fixing nothing. **Reverted to
`true`.** The actual fix: `FetchTextureViaHttpRangeAsync`'s retry set dropped `Forbidden` — a
403 is a permanent, deliberate denial (unlike a transient 503 or a not-yet-propagated 404), so
retrying it 5× at 2s intervals was the wasted load actually producing those log lines. Also ran
`dotnet format` — the originating commit had left whitespace violations in `GridSession.cs`.
**Not yet re-verified in-world.**
[Spec](file:///E:/Git/SLNG/docs/specs/BUG-NET-05-06-texture-and-env-fetch-spam.md).

**`BUG-AVATAR-02` — the white avatar's actual root cause, found and fixed.** Finished the
`godot.log` search myself (`grep -n "SelfBake\|Bake\]\|Appearance\]" godot2026-*.log` across the
rotated logs, not just the live one) instead of leaving it to a later session: `[SelfBake]`
proved the sim relayed real, stable bake ids — the SAME ten channel ids across every relog from
13:48 to 15:31. Every one of them failed with `HTTP 403` (`dda710d4` was indeed unrelated, a
plain world-object texture). The user then captured the actual HTTP exchange with HTTP Toolkit
(MITM proxy): SLNG's request to `asset-cdn.glb.agni.lindenlab.com/?texture_id=...` got a raw AWS
S3 `AccessDenied` — and a side-by-side capture of **Firestorm** fetching the identical id showed
it using a totally different host and shape:
`bake-texture.glb.agni.lindenlab.com/texture/<agent-id>/eyes/<texture-id>` → 200 OK, real bytes.
Avatar bakes are served through their OWN dedicated CDN, not the generic one every other texture
uses — real reference-viewer infrastructure (`llappcorehttp.h`'s `bake-texture` HTTP pool,
`llavatarappearancedefines.cpp`'s slot-name table), which neither LibreMetaverse 3.1.3 nor SLNG's
own fetch code had ever implemented. Fix: new `SLNG.Core.BakeChannelNames` (all 11 slot names,
read from the reference table, not guessed from the one — "eyes" — actually observed),
`GridSession.FetchBakeTextureDataAsync` + `ParseLindenGridShortName`, `AssetService.
GetBakeTextureAsync`, wired into both places a bake texture is fetched
(`AvatarRenderer.LoadAndApplyTextureAsync` for the system mesh, and the BoM face-material path
for a mesh body/head). `v0.20.0-alpha`, 16 new tests. **Not yet re-verified in-world — untried
against a real avatar.** Also notable: this also happens to be the reason **also on real SL,
`asset-cdn.glb.agni.lindenlab.com` matches what a legitimate `GetTexture`/`ViewerAsset`
capability resolves to** — the 403 there is real and by design; SLNG was simply asking the wrong
host for a bake specifically, not doing anything wrong for ordinary textures.
[Spec](file:///E:/Git/SLNG/docs/specs/BUG-AVATAR-02-bake-texture-403.md).

**A process failure worth remembering, found while investigating the above.** Grepping for a
comment string from earlier this session (the §2.b `DumpPreview` gate) turned up nothing at all —
`BUG-AVATAR-01`'s `RebakeAvatar` SSB branch, the `BakeAvatarAsync`/`CreateTestSkinAsync` SSB
guards, the `_isLindenGrid` field and its login-time assignment, and `BUG-NET-04`'s
`OnSimChanged` subscription had ALL silently vanished from `GridSession.cs` — even though
`godot.log` proves the `RebakeAvatar` fix was tested and working at 13:48. Best explanation:
between then and the `dd1f2ab` commit (~15:15, "Restored FEAT-SL-01 uncommitted state" + the
BUG-NET-05/06 work), whatever process wrote that commit worked from a partial/stale copy of this
file and silently dropped everything added after that snapshot. All of it was re-applied from
the original reasoning (still fresh in-session) rather than re-derived from scratch — but the
lesson stands: **when a file this large is being edited across tools/sessions in parallel, verify
a fix is still present by grep after any external commit, don't assume "committed" means
"complete."**

**`BUG-NET-07`, found re-testing `BUG-NET-06` — the 60s backoff was dead code.** The user went
back to Agni with HTTP Toolkit and still saw a sustained burst of 503 `cap invocation rate
exceeded` for the same `/cap/<uuid>`, with `Retry-After: 4`. First guess in this session (mine,
not the user's) was EventQueueGet — wrong, and never actually said to the user before being
caught: a captured 200-OK body for the identical cap UUID decodes to `ambient`/`blue_density`/
`sun_angle`/`waterFogColor`/`wave1Dir` — exactly the legacy `EnvironmentSettings` keys
`EnvironmentLlsdParser.cs` already parses. **The user's own read — "Das 503 ist das windlight
zeugs" — was correct.** Root cause: `BUG-NET-06`'s cooldown (`capture?.Error?.Contains
("ServiceUnavailable")`, wait 60s before re-polling) could never fire, because LibreMetaverse's
`EnvironmentManager.GetRegionEnvironmentAsync`/`GetParcelEnvironmentAsync`/
`GetLegacyEnvironmentAsync` all swallow the HTTP status on a non-2xx response internally
(`Logger.Warn`, return `null`) — `FetchRegionEnvironmentAsync` only ever set its own `error` from
a caught exception, never a plain failed response, so `capture.Error` stayed null on every 503 and
`RepollEnvironmentLoopAsync` kept re-asking every 2.5s forever. Checked whether upgrading the
pinned LibreMetaverse 3.1.3 → 3.1.4 would help (`gh api repos/cinderblocks/libremetaverse/tags`,
nuspec diff) — turned out unnecessary: 3.1.3 already ships its own client-side rate limiter
(`CapsRateLimiter`/`RateLimitingCapsHandler`, confirmed by reflection against the actual pinned
assembly, not just the newer `scratch/libremetaverse_src` checkout). The gap was entirely
GridSession's own status-signal plumbing. Fix: new `GridSession.GetCapabilityMapAsync` calls
`_client.HttpCapsClient.GetAsync` directly (same client, same one request — no added traffic)
instead of the three LMV wrappers, so the real `HttpStatusCode` reaches `error` and the cooldown
can actually engage. `v0.20.1-alpha`, 563/563 tests unchanged. **Not yet re-verified in-world.**
Two loose ends from the same screenshot batch, not yet resolved: a 404 `Hash mismatch:
c0799934-...` on the bake-texture `head` channel (a locally-cached bake id that may be stale
relative to the server's current record — plausibly downstream of this very rate-limiting, if the
`AvatarAppearance` update carrying the corrected id got lost in the same storm), and a UUID
(`70d4f143-...`) the user mentioned in chat ("firestorm fragt die UUID garnicht ab") that does not
appear in any local `godot.log` or in any of the three screenshots actually shared this thread —
worth asking the user to confirm which capture that id came from before chasing it further.
[Spec](file:///E:/Git/SLNG/docs/specs/BUG-NET-07-environment-503-cooldown-unreachable.md).

**`BUG-NET-08`, found seconds after reporting `BUG-NET-07` — the actual "every second" cause.**
User re-tested immediately: **"OK die 503 kommt immernoch im sekundentakt."** `BUG-NET-07` was a
real fix but structurally could not explain that cadence — `RepollEnvironmentLoopAsync` throttles
to one fetch per 2.5s no matter what. Traced further: `GridSession.OnEventQueueRunning` (hung off
LibreMetaverse's `NetworkManager.EventQueueRunning`) calls the FULL `FetchRegionEnvironmentAsync()`
— legacy Windlight cap included — with zero throttling, on the assumption (stated in its own doc
comment) that the event fires once per region. It does not: `EventQueueClient.ConnectedResponseHandler`
invokes its `OnConnected` callback after EVERY successful `EventQueueGet` poll, not just the first,
despite that method's own source comment claiming "the event queue is starting up for the first
time" — no first-time gate exists anywhere in `EventQueueClient.cs`. On SL, `EventQueueGet`
resolves roughly once a second under normal traffic, so this call site was re-triggering a full
environment capture — including the 503-prone legacy fetch — about once a second, completely
bypassing the OTHER call site's throttle. This, not the dead-cooldown bug, was the dominant spam
source the whole time; `BUG-NET-07` was necessary but not sufficient. Fix: a new
`_environmentCapturedForSim` field guards the handler to fire once per live `Simulator` instance
(`ReferenceEquals`, not the region handle — so a genuine reconnect to a previously-visited region
still captures fresh; LMV hands out a new `Simulator` object per connection, the same lifecycle
`BUG-NET-04` already depends on). Live environment changes remain covered by the separate,
already-throttled `RegionInfo`-driven repoll path. `v0.20.2-alpha`, 563/563 tests unchanged.
**Not yet re-verified in-world — this should be the one that actually stops the pattern the user
is watching live.** [Spec](file:///E:/Git/SLNG/docs/specs/BUG-NET-08-eventqueuerunning-fires-every-poll.md).

**`BUG-NET-09` — the follow-up question, "was passiert wenn jemand das WL auf der sim wechselt".**
Split the answer in two and verified both against the real reference viewer's own source
(`llenvironment.cpp`, `llviewerparcelmgr.cpp`), not from memory. A REGION-wide change: already
works live, unaffected by anything else today — `RegionInfo` packet → `RepollEnvironment()`,
which turns out to be EXACTLY how the real viewer wires it too (`LLRegionInfoModel`'s update
callback → `requestRegion()`). A PARCEL-only change (only the parcel you're standing on, not the
whole region): had no equivalent in SLNG at all, and — the actual finding, not just a missing
feature — **cannot be made to push the way the real viewer does it.** The real viewer detects it
from a `ParcelEnvironmentVersion` field inside an unsolicited `ParcelProperties` push;
LibreMetaverse's `ParcelPropertiesMessage` never parses that field, and the one API shape that
would have exposed the raw LLSD instead of a typed message exists in the pinned source only as a
commented-out delegate — dead, unreachable. `libremetaverse` is a compiled NuGet dependency here,
not vendored source, so there's no patching around it locally. Asked the user how to handle it
(periodic re-check vs. accept the gap) via `AskUserQuestion`; the user dismissed that question in
the moment ("wait for next instruction"), then a message later gave the answer directly: **"lass
uns mal auf 30 sekunden gehen und wir gehen dann runter."** Fix: a new `PeriodicTimer` loop
(`ParcelEnvironmentPollLoopAsync`, 30s, cancelled in `Dispose`) that does nothing but call the
EXISTING `RepollEnvironment()` — reuses all of `BUG-NET-07`/`08`'s throttling for free (the
`hasExt` branch of `FetchRegionEnvironmentAsync` already re-resolves and re-fetches the CURRENT
parcel's EEP settings on every call, unconditionally; this was always true, just never triggered
by anything except login or a `RegionInfo` packet). No new request shape, one new trigger. Also
found in passing, while tracing why LMV's own client-side rate limiter never protected the
environment capabilities: `CapsRateLimiter`'s name→category table (`CapsRateLimiter.cs`) has no
entry for `EnvironmentSettings`/`ExtEnvironment` at all — they fall into the generic `Default`
bucket (20 burst, 10/s refill), far too generous to have ever throttled the spam `BUG-NET-07`/`08`
fixed; that table is purely client-side self-limiting and unrelated to the actual SL server
threshold, which stays unknown (closed-source simulator) beyond the one captured data point,
`Retry-After: 4`. `v0.20.3-alpha`, 563/563 tests unchanged. **Not yet re-verified in-world — needs
an actual parcel-only edit made by a second party while standing on that parcel.** The 30s
interval is explicitly a first-verification value; the user's own plan is to relax it once
confirmed working. [Spec](file:///E:/Git/SLNG/docs/specs/BUG-NET-09-parcel-only-environment-poll.md).

**"Super das geht jetzt erst mal" — then two cleanup asks: build/startup warnings, and shutdown
errors.** Handled the warnings fully; the shutdown errors are explained but not yet fixed (see
below).

**`BUG-NET-10` — the one build warning turned out to be a real, broken feature, not noise.**
`CS0067: MaturityPreferenceChanged is never used`. Traced it and found `FEAT-SL-02` (Maturity/Age
preferences, shipped earlier this session) silently broken three separate ways at once, all inside
the same `// Restored Uncommitted Methods` block — very likely a 4th casualty of the silent-restore
incident already documented for `BUG-AVATAR-01`/`BUG-NET-04`/the TPV guards, just never caught
until a compiler warning happened to point at one corner of it. (1) The event was declared,
subscribed to by `MaturityPreferencesPage.cs` (whose own comment assumes it already works), but
never actually raised. (2) `SupportsMaturityPreference` was a plain stored `false` with nothing
anywhere ever setting it true — confirmed via `git log -S` across all history, zero hits —
contradicting `FEAT-SL-02`'s own spec, which documents it as a LIVE capability read. (3)
`AccountMaturityMax`/`PreferredMaturity` were never populated from the login response's
`agent_access_max`/`agent_region_access` at all — stuck at General regardless of account or grid,
for the whole session, always. Net result before the fix: Preferences always claimed "grid doesn't
support this," a successful change never showed up in the UI, and the displayed ceiling never
matched the real account — a real, working-looking feature that in fact never worked, since the
day it shipped. Fixed all three: login-response population, `SupportsMaturityPreference` made a
live computed property (matching the `hasExt`/`hasLegacy` pattern already used elsewhere in this
file), event now raised on a successful change. `v0.20.4-alpha`, warnings 1→0, 563/563 tests
unchanged. **Not yet re-verified in-world.**
[Spec](file:///E:/Git/SLNG/docs/specs/BUG-NET-10-maturity-preference-never-worked.md).

**`BUG-RENDER-04` — 474 "Vector3 cannot be normalized" warnings, traced and fixed with a measured
probe, not a guess.** Right after the six system body-part meshes load, `godot.log` printed the
warning 474 times. Wrote a throwaway console probe (`scratch/DecodeTest`, gitignored — already
referenced `SLNG.Assets`) against the REAL, unmodified `AvatarBodyMeshService.Load` output before
touching any fix code. First hypothesis (UV-degenerate triangles only) measured out to just 7
triangles — nowhere near enough to explain 474 — so didn't stop there: re-measured position-space
degeneracy too and found the real dominant cause, 152 triangles with genuinely zero 3D area (two or
three coincident/collinear vertices) in LL's own `avatar_head`/`avatar_eye`/`avatar_upper_body`
`.llm` meshes — 159 total × 3 vertices ≈ 477, matching the observed 474. `GenerateTangents()`
divides by each triangle's UV-gradient area; either kind of degeneracy drives that toward 0/0.
Fixed both, differently, verified against the same probe (re-checked: zero triangles still
degenerate after the fix, in every affected part): position-degenerate triangles dropped from the
index buffer outright (zero screen-space area, so this changes nothing visible); UV-degenerate-only
triangles get 3 freshly duplicated vertices with one UV nudged by an invisible sub-texel amount, so
`GenerateTangents()` has something non-degenerate to compute from, while every well-formed triangle
keeps its original shared vertices untouched. `v0.20.4-alpha`, warnings 1→0 (0 total combined with
`BUG-NET-10`'s fix — build now fully clean), 563/563 tests unchanged, `--selftest` uniform counts
unchanged. **Not yet re-verified in-world.**
[Spec](file:///E:/Git/SLNG/docs/specs/BUG-RENDER-04-avatar-mesh-tangent-warnings.md).

**The shutdown errors — explained, not yet fixed.** User's second paste: `ERROR: Pages in use
exist at exit in PagedAllocator: ...GeometryInstanceSurfaceDataCache`, `1 RID allocations of type
MeshStorage::Mesh` and `SceneCull::Instance` leaked, `...GeometryInstanceForwardClustered` pages in
use, a leaked instance dependency, 1 `IndexArray` + 1 `IndexBuffer` + 3 `VertexBuffer` RIDs leaked,
3 `ObjectDB` instances leaked. Reading the type names: this is ONE `MeshInstance3D`-shaped GPU
resource (mesh + its render-server instance + index/vertex buffers) that is never freed before the
engine tears down — something creates a mesh/instance and either never calls `QueueFree()` on its
owning node, or holds a reference to it (a cache dictionary, a static) that outlives the scene tree
and prevents Godot's own cleanup from ever running. Harmless in the sense that the OS reclaims
everything at process exit regardless — but a real leak while the app is actually running (repeated
across relogs) would grow unbounded. **Not root-caused yet** — the engine's own error message says
"3 ObjectDB instances were leaked at exit (run with `--verbose` for details)," which would name the
exact object; guessing across `AvatarRenderer`'s and `GpuCache`'s several mesh/skin caches without
that would risk a wrong fix (freeing something still legitimately in use) for what is currently a
shutdown-only symptom. Next step: capture one `--verbose` close and grep it for the leaked type
names above, or spawn `graphics-engineer`/`performance-engineer` on it with a live repro.

**`BUG-UI-01` — login screen, before even trying the render problem.** User sent a screenshot of
the login screen: the saved-profile dropdown showed the raw login URL
(`purisViewer resident @ https://login.agni.lindenlab.com/cgi-bin/login.cgi`) instead of a grid
name, and — the sharper half — the separate grid dropdown right underneath was still on "OSGrid"
while the login URL actually loaded was Agni's. *"Das was oben ausgewählt ist sollte auch in der
Auswahl stehen."* Both gaps were pre-existing `Boot.cs` wiring, not something this session broke:
the profile dropdown's text was literally the `ConfigFile` section key, never reformatted; and
loading a saved profile set the login-URL text field but never touched `GridDropdown`'s own
selection — nothing synced the other direction. Fixed with two small helpers,
`GetGridDisplayName` (one source of truth: reads `GridDropdown`'s own item text rather than a
second name table, falls back to just the host for a custom grid) and `SyncGridDropdownToUri`
(selects the matching entry or clears it) — wired into `LoadProfiles()`'s dropdown text and
`OnProfileSelected`. Storage format untouched, so an existing `logins.cfg` still loads fine.
`v0.20.5-alpha`, 563/563 tests unchanged. **Not yet re-verified in-world.**
[Spec](file:///E:/Git/SLNG/docs/specs/BUG-UI-06-login-screen-profile-grid-mismatch.md).

**`BUG-RENDER-05` — back to the render problem: the water/canopy cut was STILL there.** User sent
two screenshots of the same tree (untextured canopy fine, textured canopy cleanly cut at the
water/horizon height) plus a Firestorm material-inspector shot confirming the leaves really are
`Alpha-Blending`, not a mask — ruling out a classification mistake and confirming this was the same
bug `BUG-RENDER-03` already tried to fix, still happening. `BUG-RENDER-03`'s `depth_draw_opaque`
fix was real but only addressed a depth-TEST failure; the actual dominant cause was Godot's
SEPARATE transparent-pass SORT — water and every ordinary alpha-blended material share the default
`RenderPriority` (0), so two overlapping transparent objects fall back to an approximate per-object
distance heuristic, and water's plane (up to the `VoidWaterPlane`'s 16384m) has one computed
distance that means nothing for which of its fragments a given leaf pixel is really behind. Found
the exact precedent already in the codebase: `ObjectParticles.cs` gives particles `RenderPriority =
1` to beat water, with a comment citing the real viewer's own dedicated water-pass split — but that
was only ever fixed for particles, one consumer at a time. Fix: `TerrainRenderer._waterMaterial.
RenderPriority = -1` — `RenderPriority` buckets are compared BEFORE any distance tiebreak, a hard
guarantee, so this protects EVERY default-priority transparent object against water at once.
`BUG-RENDER-03`'s spec got a correction note (not a silent edit) pointing here. `v0.20.6-alpha`,
563/563 tests unchanged, no shader touched (a `Material` property). **User confirmed live:
"passt jetzt mit dem wasser."** [Spec](file:///E:/Git/SLNG/docs/specs/BUG-RENDER-05-water-transparent-sort-priority.md).

**`BUG-RENDER-06` — same tree, the next thing the user flagged: "die texturen flackern... vor
allem wenn texturen noch laden oder wenn man sich bewegt/zoomt."** Asked a clarifying question
first (`AskUserQuestion`) — confirmed camera-movement-triggered, not an idle/stationary flicker.
Traced end-to-end before writing any fix: LibreMetaverse already parses glTF's `doubleSided` flag
(`AssetMaterial.DoubleSided`), and `ObjectRenderer.cs` already had a comment describing the real
viewer's exact exception (culling lifted only for particles and explicitly double-sided GLTF
materials) — but `PbrMaterialData`, the neutral DTO crossing the `SLNG.Assets` boundary, never
carried the field forward, so it was parsed and then silently dropped before any renderer ever saw
it. No shader variant existed to route a double-sided face to either — `cull_back` is baked into
`render_mode`, compile-time in Godot. For a mesh tree's leaf cards (near-universally authored
double-sided — confirmed via the user's own Firestorm screenshot of the leaf material's
`Alpha-Blending` mode), every triangle was culled like an ordinary one-sided face, so individual
leaves popped in and out purely as a function of view angle while orbiting/zooming — the reported
flicker. User confirmed the direction before the larger change: **"Direkt implementieren
(Empfehlung)."** Fix: `PbrMaterialData.DoubleSided`; three new WorldPrim-only shader variants
(`prim_opaque/scissor/blend_doublesided.gdshader`, added alongside the existing ones rather than
modifying them — `prim_opaque.gdshader`'s own comment explicitly warns against ever making
`cull_back` conditional there, since that was the exact 2026-08-01 "glassy shell" bug); `PrimShader
Family.Select` gains a `doubleSided` parameter (a no-op for Avatar/Hud, already unconditionally
cull_disabled — only WorldPrim needed new variants); `ObjectRenderer.cs`'s PBR branch swaps to the
double-sided twin of whichever Kind the existing `alphaMode` logic already chose, via a small
reference-equality helper (`DoubleSidedTwin`). `v0.20.7-alpha`, 563/563 tests unchanged,
`--selftest` 29/29 (26 + the 3 new shaders, uniform counts matching their base variants). **Not yet
re-verified in-world.** The "flicker while textures are still loading" half of the report is a
separate, one-time placeholder→texture transition, not addressed here.
[Spec](file:///E:/Git/SLNG/docs/specs/BUG-RENDER-06-double-sided-material-culling.md).

**Re-tested live, on a DIFFERENT tree (a conifer/needle tree, not the earlier broad-leaf one):
"flippt immernoch"** — confirmed running `v0.20.7-alpha` (the double-sided fix), so the fix itself
built and shipped correctly; it just doesn't explain this tree's flicker. Rather than guess a
fourth time, added one line of always-on diagnostic logging instead:
`ObjectRenderer.cs`'s PBR branch now prints `[PbrMaterial] {id8} alphaMode=... doubleSided=...`
once per distinct material id (same "one line per material, not per face" convention as
`[LegacyMaterial]`). Next test's log settles it directly: if this conifer's material logs
`doubleSided=False`, the fix's own routing is working correctly and this is a genuinely different
tree whose content just isn't authored double-sided (or uses a legacy, non-PBR material, which
cannot carry the flag at all) — a different root cause, not yet found. If it logs `doubleSided=True`
and still flickers, `DoubleSidedTwin`'s routing itself has a bug. `v0.20.8-alpha`, diagnostic-only,
no behaviour change — 563/563 tests unchanged, `--selftest` 29/29, `dotnet format` clean.

**Result: zero `[PbrMaterial]` lines at all, for either tree.** User, independently, same
conclusion: *"Ich vermute reine Texturen"* — no PBR/glTF material at all, just a plain textured
face going through `ApplyAlphaCutout`'s `DetectAlpha()` fallback (`ObjectRenderer.cs`). That path
has structurally NO double-sided signal to read (glTF's `doubleSided` doesn't exist for non-PBR
content in SL's protocol at all) — `BUG-RENDER-06`'s fix could not have applied here regardless of
correctness. Flipped `DebugLegacyMaterials` to `true` (was `false`) to settle whether these two
trees carry a legacy Blinn-Phong material (also has no double-sided concept) or genuinely nothing —
`v0.20.9-alpha`, still diagnostic-only. **Open question this needs before continuing:** does
Firestorm render this SAME tree without the flicker? The already-verified reference-viewer source
(`lldrawpoolalpha.cpp`) says culling is lifted ONLY for particles and explicitly double-sided glTF
materials — if this content has neither, the real viewer would cull it identically, meaning this
could be a genuine content/asset limitation (single-sided leaf cards popping in every viewer, not
an SLNG bug) rather than something more to fix here.

**Answered, and then some — paused for the day with a narrowed, still-open lead.** Firestorm:
**stable, no flicker**, on the same content — so this IS a real SLNG-side gap, not a content
limitation. Pure rotation with NO zoom/distance change still flickers (user-confirmed) — angle-
dependent, not distance-dependent, which is what sent this toward culling in the first place.
Before digging further, went back and RE-VERIFIED the foundational claim against real
`lldrawpoolalpha.cpp` source directly (not trusted from the existing in-repo comment) — confirmed
character-for-character: `LLGLDisable cull_face(draw->mGLTFMaterial->mDoubleSided ? GL_CULL_FACE :
0)`, three call sites, culling lifted ONLY for an explicitly double-sided GLTF material. Since
BOTH trees confirmed zero `[PbrMaterial]` AND zero `[LegacyMaterial]` lines (`DebugLegacyMaterials`
flipped to `true` then back to `false` once answered — see `ObjectRenderer.cs`'s own comment there),
they carry no material object of either kind — so SLNG's culling behavior, verified, already
matches the real viewer's rule exactly for this content. That rules out "wrong culling rule" as the
explanation and points somewhere else entirely. Also ruled out, with code evidence, not guesses:

- **Mesh LOD switching** — `AssetService.GetMeshAsync(Guid meshId)` takes no LOD parameter at all
  and caches one decoded `MeshData` per mesh id; `PickPrimDetailLevel`'s distance-based LOD only
  exists for procedural `PrimShape` geometry (curved-profile prims), never uploaded mesh assets.
- **Texture discard-level resharpening** (`GpuCache.TryUpgradeCachedTexture`, the abrupt
  `cached.SetImage(image)` swap when `screenPixelArea` grows past its 4x-area guard) — real
  mechanism, but requires a distance/size change to trigger at all; ruled out by the pure-rotation
  test above.
- **SLNG.Assets' own mesh decode dropping geometry** — `AssetService.Decode` is a faithful,
  unmodified copy of LibreMetaverse's `FacetedMesh.TryDecodeFromAsset` output (positions/normals/
  UVs/indices copied 1:1 per face); no dedup, no degenerate-triangle filtering anywhere in it
  (that logic exists only in `AvatarRenderer.BuildPartMesh`, for the system avatar body mesh
  specifically — `BUG-RENDER-04` — a completely separate code path from general world-object mesh
  decode).

**Leading remaining suspect, not yet investigated:** LibreMetaverse's own
`FacetedMesh.TryDecodeFromAsset` possibly drops or mis-orders triangles for this content — this
project has hit real LMV mesh-decode bugs before (the 4-influence skin-weight parser, `Face.ID`
never populated) — worth checking with the same rigor next time. **User's call, given how much
landed today (water, warnings, login UI, double-sided materials): pause here, pick it up in a
future session** rather than push further tonight. Nothing to revert — `DebugLegacyMaterials` is
back at its default `false`, all other work from today stands. `v0.20.10-alpha`.

User then sent two Firestorm reference screenshots of the same conifer from two angles, as
evidence for next time: needle-cluster shapes read consistent between the two angles, nothing
visibly missing or popping — supports the "stable in Firestorm" answer above. No file path saved
(inline chat images, not files on disk) — if this needs re-confirming later, ask the user to
re-share or take fresh ones once the LibreMetaverse mesh-decode lead is actually being worked.

**User then said "mach mal einen commit und merge zum aktuellen stand das hat ja nix mehr mit dem
readyness zu tun"** — committed everything from BUG-NET-07 through BUG-UI-06 (ten fixes) in one
commit, fast-forward merged `feature/FEAT-SL-01-second-life-readiness` into `main` (clean, no
merge commit needed — main had nothing the feature branch didn't already have plus one prior
commit). Not pushed to `origin/main` yet — asked the user first. Work continues directly on `main`
from here per the user's own framing (no longer scoped to FEAT-SL-01).

**`BUG-RENDER-07` — a real, separate, structural bug found while chasing BUG-RENDER-06's
flicker.** User sent a Firestorm Texture-tab screenshot of the SAME conifer: a genuine
**Blinn-Phong** material (Alpha-Masking, cutoff 100, a normal map) — directly contradicting the
app's own `[LegacyMaterial]` log, which had shown zero lines for that object across every test.
Traced it: `PrimitiveComponent`'s constructor never had a `legacyMaterialId` parameter at all — an
object's FIRST load (`WorldSimulation.ApplyObjectUpdate`'s "new entity" branch) built the component
with no way to receive the incoming event's legacy material id, so it stayed `Guid.Empty` forever;
only a SECOND OR LATER update to the same entity ever set it correctly (a separate, already-correct
line in the `else` branch). A static object loaded once and never incidentally re-sent by the sim
afterward silently lost its whole legacy material — real alpha cutoff, normal map, specular map —
for the entire session, falling back to a pixel-content alpha guess with a hardcoded 0.5 threshold.
Fix: added the missing constructor parameter, wired through at the one call site, audited every
other field in that branch against the constructor (nothing else was missing). `v0.20.11-alpha`,
563/563 tests unchanged. **Not yet re-verified in-world**, and — important to not overclaim —
**this probably does NOT fix BUG-RENDER-06's flicker by itself**: legacy materials have no
double-sided concept either, per the same reference-viewer rule already verified for
BUG-RENDER-06. Still a real, independently-worth-shipping bug — likely affects any legacy-
materialed object anywhere in the world that loads once and sits still.
[Spec](file:///E:/Git/SLNG/docs/specs/BUG-RENDER-07-legacy-material-id-lost-on-create.md).

## 7. Also still open

- **The avatar stands too low**, feet sunk into the ground. Predates this session. Concrete
  lead: `AvatarRenderer.cs:2323` measures the **rest** pose (`GetBoneRest`) while the comment
  above it says the *live* pose (`GetBoneGlobalPose`). Worn mesh joint overrides are invisible
  to the rest pose, which would sink the avatar by exactly that difference.
- **Attachments occasionally drop out of BoM** — 25 registrations of "NO BoM channels" against
  197 good ones, with real (non-magic) texture ids. `v0.18.5` logs the ids when it happens.

## 8. Method notes, from what actually cost the most

- **A round trip through the same library proves nothing.**
  `SLNGs_bake_encoder_round_trips_the_image_exactly` passed with error 0 while every upload
  was unreadable by any other viewer. Decoding with the encoder's own library proves the data
  survives, not that anyone else can read it. The tests now parse the SIZ/COD markers and
  compare against a real grid asset.
- **An upload or create returning an id is not proof the asset exists.** Three times this
  session: `NewFileAgentInventory` answered four creates with real `new_inventory_item` +
  `new_asset` ids and stored none of them; the bake upload capability has never been verified
  at all. Read back what you wrote.
- **Byte counts do not tell you whether a picture is right.** Much of this task was spent on
  numbers. Dumping the composite and the inputs as PNGs and *looking* found the doubled face,
  the wrong skin and the missing layers within one run each.
- **The reference viewer is the only arbiter.** The container bug was invisible from inside
  SLNG by construction. One screenshot from Firestorm found it.
- **`scratch/libremetaverse_src` is not the pinned package.** `Targa.Decode`,
  `ManagedImage(bitmap)` and `ExportTGA` exist in the vendored source and not in the assembly;
  `CreateItemAsync` exists in the assembly and not in the source. Reflect over the DLL when the
  API matters.
- **`scratch/slviewer` has no `newview` sources.** Use
  `gh api repos/secondlife/viewer/contents/...` — that is how `build_order_string` and
  `LLTexLayerTemplate` were settled.
- **Inventory item names go through the legacy packet.** A name containing `:` or `—` came
  back as a hex dump of itself, truncated at the colon. Plain ASCII.
