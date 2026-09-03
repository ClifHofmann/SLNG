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
decode — bounded to the CPU, `priority`-ordered so on-camera textures finish first. The `[TexPipe]`
avg is now measured after the throttle = true decode time. If it stays ~90 ms, Magick.NET's
OpenJPEG is the bottleneck and the next lever is a **reduce-level decode** (decode fewer wavelet
resolution levels for the first display, upgrade the near ones on demand — the reference viewer's
`parameters.cp_reduce`).

The 403 on `33192a49` is `from asset-cdn.glb.agni.lindenlab.com/` — the **generic** asset CDN,
not a bake-style different URL: a real permission denial / non-persisted asset, nothing to route
differently.

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
