# [BUG-AVATAR-19] Reduced avatars draw their bakes

- **Feature ID:** `BUG-AVATAR-19`
- **Track:** `render`
- **Status:** `🧪 Review` (decisions 1-5 accepted by the maintainer 2026-10-31 as recommended; built, awaiting the cold runs)
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md), [ADR 0005](file:///E:/Git/SLNG/docs/adr/0005-region-arrival-texture-streaming.md) task 2

This spec **amends ADR 0004 P3** (done in the same commit), which kept the jelly doll as the stand-in "for avatars never loaded". The maintainer
accepted all five decisions below as recommended (2026-10-31).

## Overview & Goal

An avatar over the cap (`RenderConfig.MaxFullyRenderedAvatars`, 11 by default) is **reduced** (FEAT-PERF-08): it keeps its skeleton
and system body, wears nothing, and is painted in a flat jelly-doll colour. Its bakes are never fetched
(`AvatarRenderer.cs`, "A reduced avatar is drawn in the jelly-doll colour, so its bakes are not fetched").
`RenderConfig.cs:22` and `GraphicsSettings.cs:182` describe the stand-in as "base body with bake textures"; the code does not do that.

On a crowded arrival this is most of the people. Measured at Sirens, `docs/perf/baseline-arrival-2026-10-31.md`:
reduced avatars without any bake at +60 s were **35 of 39, 8 of 9** (cold) and **33 of 38, 37 of 39, 37 of 40** (warm),
and in the later A/B runs **16 of 23** and **24 of 28**, with the network idle. They stay grey until promoted. The reference viewer
does not do this: an avatar over its limit is an **impostor**, the avatar rendered into a texture and refreshed less often, so its
bakes still load (`llvoavatar.cpp:10996-11012`). A flat colour is only for avatars that are visually muted: too complex, too slow
or on the mute list (`llvoavatar.cpp:3923-3962`).

**Goal:** a reduced avatar shows its system body with its bakes. The jelly colour stays only until the bake of a part has arrived.

## What a reduced avatar is today (code, read 2026-10-31, `main` at `ac501dbe`)

- `SetAvatarReduced` parks the outfit (or leaves it unloaded) and calls `ApplyJellyDoll`: every system part's material is stashed in
  `AvatarVisual.StashedPartMaterials` and replaced by one shared opaque jelly material (`GetJellyMaterial`, one per colour).
- `UpdateVisual` skips the bake loop while `visual.IsReduced` (`AvatarRenderer.cs`, the `!visual.IsReduced` test before
  `LoadAndApplyTextureAsync`). `SetAvatarFull` restores the stashed materials and the next `UpdateVisual` loads the bakes.
- A bake that lands while the avatar is reduced is already handled: `LoadAndApplyTextureAsync` writes it into the **stashed** material, so
  promotion shows it at once. The plumbing for "a bake exists while reduced" is therefore half there; only the request is missing.
- The bake path forces `screenPixelArea = 0` ("bakes stay full resolution, exempt from distance LOD", BUG-PERF-13) and the apply
  item uses `PrimShaderFamily.Kind.Scissor` at 0.5 with `alpha_to_coverage`.

## Acceptance Criteria

- [ ] A reduced avatar whose appearance has arrived requests its system bakes (head 8, upper 9, lower 10, eyes 11, hair 20) and draws each
      part with its bake when that bake is applied. A part whose bake has not arrived stays on the jelly colour.
- [ ] At +60 s after the handshake, `[ArrivalCount] reducedNoBakes` is **0** in a cold run at Sirens, for avatars whose appearance has
      arrived. (Today 16 to 37.)
- [ ] The bake's alpha channel cuts **no holes** in the stand-in (see decision 1).
- [ ] Promotion (`reduced->full`) and demotion keep working: no bake is requested twice, no texture reference leaks, the outfit load is unchanged.
- [ ] Texture memory stays bounded (see decision 2): the reduced avatars' bakes are the first textures to be shrunk or dropped under VRAM
      pressure, and the measured extra VRAM is written into this spec.
- [ ] No regression of `[ArrivalCount]` full-avatar numbers or of "no face waiting for a texture" beyond the run-to-run spread in the baseline.
- [ ] Unit tests for the pure policy (below) written first and passing; `/slng-verify` clean; `AppVersion` bumped.

## Decisions (accepted as recommended)

1. **Bake alpha on a stand-in.** An avatar in a mesh body hides its system body with alpha layers painted into the bake, and the RGB under
   them is conventionally black. A reduced avatar wears no mesh, so the system body is all there is.
   - **A. Respect the alpha** (as full avatars do): the stand-in has holes, and a full-mesh-body avatar disappears entirely.
   - **B. Ignore the alpha** (opaque variant): no holes, but black patches where the wearer hides the body.
   - **C. (recommended) Ignore the alpha for the shape, and draw the jelly colour where the bake's alpha is below the cutoff**: no holes, no black.
     Needs one extra uniform on the Avatar surface's opaque variant (a "stand-in" mode); cost: one texture read and a `mix`, only on stand-ins.
2. **Resolution.** A remote bake is up to 1024 x 1024; five of them per avatar for 40 to 60 reduced avatars is on the order of 1 to 1.5 GB
   of VRAM at full size (estimate, to be measured), on a budget of 3.4 to 4.8 GB that is already under pressure. **Recommended:** a reduced
   avatar's bakes pass the avatar's real screen pixel area (not 0), so the existing decode reduce and the shrink pass cut them to what is
   visible, and they are **not** exempt from shrinking. Promotion re-sharpens through the existing `TryUpgradeCachedTexture` path.
   Alternative: always full size and accept the VRAM.
3. **How many.** Every reduced avatar inside the draw distance, or only the nearest N (for example 32)? **Recommended:** all inside the draw
   distance; their requests rank by size (BUG-PERF-24, merged), so the far ones queue last, and under pressure they are dropped first.
4. **Which parts.** Only the system parts a stand-in actually draws (`SystemPartNames`). Skirt (19) has no part and is not requested.
5. **ADR 0004 P3.** Replace "The jelly doll stays the fallback for avatars never loaded" by: "A reduced avatar draws its system body with
   its bakes; the jelly doll shows only until a part's bake has arrived, and for avatars the viewer would mute."

## Technical Specs & Affected Files

Pure policy, in `SLNG.Core`, test first:

- `StandInBakePolicy` (new, `src/SLNG.Core`):
  - `WantedBakes(IReadOnlyDictionary<int, Guid> baked)` -> the (index, id) pairs a stand-in requests: the five system channels, non-empty ids only.
  - `PartState(bool bakeApplied)` -> bake or jelly, per part.
  - `PixelAreaForStandIn(float area)` -> the area handed to the cache so the bake is reduced (never 0; decision 2).
  - Tests: empty or all-`Guid.Empty` appearance requests nothing; channels outside the five are ignored; the area is never 0; a part with
    no bake reports jelly; a part with a bake reports bake.

Renderer (`app/`), after the policy is green:

- `AvatarRenderer.cs`: `UpdateVisual` requests the stand-in bakes while `IsReduced` (through the policy); `LoadAndApplyTextureAsync` takes the
  real pixel area for a reduced wearer and is no longer "bakes stay full resolution" for them; the apply item **un-stashes that one part** to
  its bake material while the others stay on jelly (`StashedPartMaterials` gets a per-part remove).
- `PrimShaderFamily` / the Avatar opaque shader: the stand-in alpha mode of decision 1C (one uniform), declared in the shader and, if it is a
  global, in `project.godot` (`tools/check_shader_globals.py`).
- `GpuCache.cs`: a reduced avatar's bakes are not in `_noShrink` and are the first candidates of the shrink pass (check the existing
  `_bakeTextures` / `_protectedBakes` rules; they must not protect a stand-in's bakes).
- `Boot.cs`: `AppVersion`. `docs/ROADMAP.md`: this entry. `docs/adr/0004-performance-architecture.md`: P3 sentence (decision 5).

## Measurement

Before and after, the same procedure as the baseline (`tools/baseline-run.ps1`, Sirens, 3 cold runs):

- `reducedNoBakes` at +30 s and +60 s (target 0 at +60 s).
- Texture MB over HTTP for the whole run: expected plus 100 to 150 MB (40 to 60 avatars x 5 bakes x about 0.46 MB), against about 1.7 GB.
- Texture memory: `[GpuCache] avatarMB` and `sizeMB` at +60 s; `[VramTerms]` lowest budget; `parked outfit unloaded under VRAM pressure`
  (must stay 0 in the first 5 minutes at Sirens).
- "No face waiting for a texture" and "every full avatar has every bake": unchanged within the baseline's spread (the latter varied 23 to
  92 s between runs, so three runs cannot show a small change either way; say so in the PR).
- A picture of a crowded stand-in next to a promoted avatar, to judge decision 1 by eye.

## Risks

- **VRAM** (decision 2). The reason the policy hands the cache a real area instead of 0.
- **Main-thread work:** every applied bake is one `avatar.bake` queue item on the Visual lane; 40 to 60 avatars x 5 items arrive in a queue that
  already holds thousands. The lane order already serves the focus avatar first; the measurement above watches `mainQueue`.
- **Wrong look for mesh-body wearers** (decision 1). Option C is the safe default; A and B each fail visibly for some avatars.
- **Interplay with BUG-PERF-16** (parked outfits): a parked avatar keeps its worn data and now also its bakes; the unload under VRAM
  pressure must drop the stand-in bakes first.
- **The bare-avatar case seen in PEAK Lounge** (Bronte: attachments never built in a crowd) is a different bug and is not fixed here.

## Sub-tasks / Progress

- [x] Read the code and the viewer; spec written
- [x] Maintainer's OK on decisions 1 to 5
- [x] `StandInBakePolicy` and its tests (test first)
- [x] Stand-in alpha mode: two uniforms in `prim_common.gdshaderinc` (`standin_alpha_cutoff`, `standin_color`), 0 = off for every other face
- [x] Renderer wiring: request while shown, apply per part, pixel area, `GpuCache.GetOrUploadTextureAsync(standIn: true)`
- [x] ADR 0004 P3 amended; `AppVersion` v0.28.80-alpha; `/slng-verify`
- [x] Three cold runs (Sirens, 2026-11-01, `baseline/20261101-0845`); numbers below
- [ ] A screenshot of a crowded stand-in next to a promoted avatar (decision 1C judged by eye)

## As built (differences from the plan above)

- **Hair and eyelashes are not filled with jelly.** Their alpha is their shape (the hair is a shell cut out by its bake; filling the cut
  would bring back the uncut system-hair helmet), so on those two parts the bake shows through the part's own alpha-cut material, like on a
  full avatar. Only head, upper, lower and eyes use the opaque stand-in material with the jelly fill (`StandInBakePolicy.IsBodySurface`).
- **Requests only while on screen:** a reduced avatar asks for its bakes only while `Shown`; the cull tick asks again when it comes into range.
- **Promotion:** the stand-in's bake is already in `StashedPartMaterials`, so promotion shows it at once. A bake prepared small is sharpened by
  the cache's existing paths (`RestoreBake` for the three nearest full avatars, the area-doubling re-sharpen otherwise); an avatar that stays
  a little blurry after promotion until it comes closer is a known consequence of decision 2.
- **Not unit-tested:** the renderer wiring (it needs the engine). The pure policy is; the rest is judged by the cold runs and a screenshot.

## Measured (3 cold runs at Sirens, v0.28.80-alpha, 2026-11-01)

Compared with the baseline (`docs/perf/baseline-arrival-2026-10-31.md`, same place, same procedure).

| | kalt 1 | kalt 2 | kalt 3 | baseline |
|---|---|---|---|---|
| Reduced avatars without a bake at +60 s | **0 of 50** | **2 of 33** | **0 of 52** | 35 of 39, 8 of 9 (later A/B runs 16 of 23, 24 of 28) |
| Every full avatar has every bake | 25.2 s | 114.0 s | 19.3 s | 30.4 / 22.7 / 56.2 s |
| No face waiting for a texture | 123.7 s | 151.2 s | 125.2 s | 133.0 / 143.4 / 134.0 s |
| Texture MB over HTTP, whole run | 1792 | 1781 | 1740 | 1722 / 1695 / 1736 |
| Peak `avatarMB` / `sizeMB` | 579 / 1205 | 901 / 1519 | 606 / 1223 | 792 to 1008 / 1414 to 1627 (6 earlier cold and warm runs) |
| Peak `mainQueue` | 12609 | 33625 | 24083 | 19729 to 41384 |
| `parked outfit unloaded under VRAM pressure` | 0 | 0 | 0 | 0 |
| `[VramTerms]` lowest budget | 4742 | 4564 | 4556 | n/a |

- **Core criterion met in 2 of 3 runs.** Run 2 ends with 2 reduced avatars without a bake at both +30 s and +60 s (the cause is not found:
  the log has no per-avatar line; a failed transfer is logged at t=23.6 s for one resident). It is also the run with the slow "every full
  avatar has every bake" (114 s, against 19 to 25 s in the other two) and the highest queue; one outlier of three, not explained.
- **VRAM is not higher** than the earlier runs without stand-in bakes (peak `avatarMB` 579 to 901 against 792 to 1008). The shrink pass does
  its job; no outfit was unloaded. The extra HTTP volume is +20 to +70 MB over the baseline, below the 100 to 150 MB estimated.
- **"No face waiting for a texture" is unchanged** within the baseline's spread (123.7 to 151.2 s against 133.0 to 143.4 s). The target
  of ADR 0005 for this measure (67 s) is not addressed by this task; the line is the bound.
- **Not yet judged:** the look (holes, black patches, sharpness after promotion). Needs a screenshot.
