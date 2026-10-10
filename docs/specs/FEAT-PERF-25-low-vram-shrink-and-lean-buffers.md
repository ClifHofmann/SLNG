# [FEAT-PERF-25] Low-VRAM cards: a shrink pass that works, lean render buffers

- **Feature ID:** `FEAT-PERF-25`
- **Track:** `render`
- **Status:** `🚧 In Progress` (built v0.27.35-alpha, in-world check open)
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](../ROADMAP.md) -- ADR 0004 P4, MVP6-1 principle 5 (the viewer must work well on 4 GB cards)

## Overview & Goal

In-world test 2026-10-10, Sirens (Agni), `tools/run-client.ps1 -VramBudget 3500` on an RTX 4070: no paging
(GPU 8 ms, 22 fps), but textures stayed blurry for a long time, near ones too. `slng-perf.log`: the
GpuCache budget from `VramBudgetPolicy` collapsed to 500-700 MB because ~2.8 GB of the 3.5 GB was not the
texture cache (Godot: video 3966 MB, textures 3351 MB, buffers 463 MB; cache ~1.9 GB); `[GpuCache]
admission: ~60 textures took extra discard ... resident 1866 + in flight 0 of 552 MB` every 10 s; LOD bias
2; and not one `texture.shrink` `[WorkCost]` entry or `shrank` / `shrink ... skipped` line all run.

## Root cause (from the code, v0.27.34)

1. **The shrink pass starved.** `GpuCache.StartShrink` got its pixels from
   `AssetService.TryGetLocalDecodedAsync`, which waited on the shared texture-decode gate at priority
   **-1000**, behind every texture request ("a shrink never queues ahead of a texture someone is waiting
   to see"). On a busy region that queue never empties, so the first shrinks never got a slot. They sat
   in `_shrinkPending`, and once `MaxShrinksInFlight` (16) were parked, `Tick` never picked another
   texture. Nothing logs before the decode returns: hence no `texture.shrink`, no `shrank`, no `skipped`.
2. **Its choice was arbitrary.** The walk took the first two entries at the LRU head. Every offer moves a
   texture to the tail, and ObjectRenderer offers every shown object every 2 s near or far, so the order
   said nothing about distance. A texture without pixels stayed at the head and was retried every frame.
3. **Near textures paid for far ones.** Admission cut every arriving texture against a cache full of old
   far textures, and the global LOD bias (2 for the whole run) landed on everything, near walls included.
   A near texture cut at upload waited for "below low water", which a cache held at its budget never
   reached. A sharpen under the raised bias reported "nothing sharper" and the texture then waited for the
   camera to come 4x closer again, long after the bias was gone.
4. **Parked avatars stayed protected.** The nearest three *shown* avatars were protected, parked or not.
5. **The non-cache VRAM was never sized for the card**: MSAA at window size, SSIL, a 1024 x 4 reflection
   atlas (~270 MB), a 4096 shadow atlas, the planar mirror's second scene render (kept at up to 1920 px
   per side while idle).

## What changed

**Part 1 -- shrink that works (`app/scripts/GpuCache.cs`)**
- Pixels for a shrink come from `AssetService.GetShrinkSourceAsync` with **its own two slots**: memory
  cache, decoded disk cache at the reduce level the half size needs, else a reduced decode of the cached
  `.j2c` (stored back). Bakes fall back to the bake host. No local pixels -> blocked 60 s, the next scan
  picks something else. An in-flight shrink older than 20 s is given up (it no longer blocks the cap).
- **Ranked candidates** (`RebuildShrinkCandidates`, every 0.5 s while over budget, best 96 kept):
  *free* = not asked for within 6 s (out of range, parked outfit) or >= 4 texels per screen pixel of its
  largest recent area; ranked bytes x texels-per-pixel x time unseen. *visible* (right-sized, near ones
  last) only when > 105 % over with nothing free, and then guarded 30 s against sharpening for room.
  Never: textures smaller than their screen area, protected avatars (self + nearest 3 FULL avatars, their
  bakes too), no LOD info (terrain, UI), sharpened in the last 30 s. Areas come from the existing offers
  (`Lookup`) and, for avatars, from `AvatarRenderer` once a second (`NoteAreas`).
- **Near textures get room**: admission and sharpen of a texture >= 128x128 px on screen may go over the
  budget by what the last scan can give back (at most budget/8); a near texture that was still cut asks
  for room (`_roomWanted`) instead of waiting for low water. The LOD bias no longer applies at >= 128x128 px
  (`TextureLod.BiasExemptAreaPx`); "nothing sharper" under a bias is retried, not final.
- A shrink records the texture as requested for its new texel count, so a re-offer that has not come 4x
  closer waits (far) or asks for room (near) -- no shrink/sharpen ping-pong.
- Far bakes are shrinkable and come back sharp from the bake host when the wearer gets close or becomes
  one of the nearest.
- `[GpuShrink]` line every 10 s while over budget or active (perf sidecar; godot.log under `--diag`):
  over-budget time, started/done/freed/noSource/timedOut/raced, in flight + oldest, room wanted, last
  scan: seen, free, visible, reclaimable MB, skipped MB per reason.

**Part 2 -- lean buffers on small budgets**
- `SLNG.Core.LowVramPolicy`: tier from the smoothed OS budget (< 6 GB small, < 4.5 GB tiny; caps tighten
  at once, relax 5 % past the threshold). Small: MSAA <= 2x, SSAO half (Godot's default already), SSIL off,
  shadow atlas <= 2048, reflection atlas 256, planar mirror off, mirror probe off. Tiny: MSAA off as well.
- `GraphicsSettings.Apply` applies the capped options (`Effective`) unless the user switched
  "Teure Effekte bei kleinen Grafikkarten begrenzen" off (Preferences > Graphics > Hardware, default on).
  The page shows what is capped right now. `[VramCaps]` logs the tier, the budget and every cap.
- Reflection atlas: Godot 4.7 has no runtime call for it; an UPDATE_ALWAYS probe resizes the scenario's
  atlas to 256 for good (light_storage.cpp `reflection_probe_instance_begin_render`), so the follow probe
  is switched to Always for 3 frames once. Godot prints a warning about the atlas size then; expected.
- `[VramBreakdown]` (one-shot: 60 s after login, 20 s after the caps change, when the texture budget falls
  below 60 % of its peak; at most 4): DXGI budget/usage, Godot video/texture/buffer totals, the cache's
  textures/meshes, what is outside the cache, est. render buffers per viewport (main, mirror, HUD, ...),
  shadow and reflection atlases (`SLNG.Core.VramEstimate`), and what the estimates do not explain.

## Acceptance Criteria
- [ ] `pwsh tools/run-client.ps1 -VramBudget 3500` at Sirens: texture budget >= 1.2 GB.
- [ ] Shrinks visibly happen when over budget (`[GpuShrink] ... done=N freed=M MB`, `texture.shrink` in `[WorkCost]`).
- [ ] Near objects sharp within a few seconds; no paging (`renderGpuMs` stable); no shrink/sharpen churn.
- [ ] Real 12 GB budget: nothing worse (`[VramCaps] ... no caps`).
- [x] `dotnet build` (solution + app), `dotnet test`, `dotnet format` (solution).
- [x] Unit tests: `LowVramPolicyTests`, `VramEstimateTests`, `ReduceLevelDecodeTests` (bias exemption,
  reduce level); self test `CheckShrinkCandidateRanking`.

## Technical Specs & Affected Files
- `app/scripts/GpuCache.cs`, `app/scripts/AvatarRenderer.cs`, `app/scripts/Boot.cs`,
  `app/scripts/Boot.VramCaps.cs`, `app/scripts/MirrorReflection.cs`, `app/scripts/UI/GraphicsSettings.cs`,
  `app/scripts/UI/GraphicsPreferencesPage.cs`, `app/i18n/*.json`
- `src/SLNG.Assets/AssetService.cs` (`GetShrinkSourceAsync` replaces `TryGetLocalDecodedAsync`),
  `src/SLNG.Assets/TextureLod.cs`, `src/SLNG.Core/LowVramPolicy.cs`, `src/SLNG.Core/VramEstimate.cs`

## Open / known limits
- `VramBudgetPolicy` takes non-cache memory as DXGI usage minus the cache. If the allocator keeps freed
  blocks, a shrinking cache does not lower DXGI usage at once and the budget can keep falling; the
  breakdown's `driver/allocator` figure (DXGI usage - Godot video) shows whether that happens.
- The HUD SubViewport renders a window-sized 3D pass with its own buffers even without a HUD worn; it is
  in the breakdown, not capped.
