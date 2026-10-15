# [FEAT-PERF-14] Small-object culling by size (engine visibility ranges)

- **Feature ID:** `FEAT-PERF-14`
- **Track:** `render`
- **Status:** `🧪 Review` (built v0.27.48-alpha; headless and engine-probe verified; in-world A/B at Sirens Beach is open, recipe below)
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md), plan in [MVP6-1-performance-program.md](file:///E:/Git/SLNG/docs/specs/MVP6-1-performance-program.md), [ADR 0004](file:///E:/Git/SLNG/docs/adr/0004-performance-architecture.md) pillar P2
- **Depends on:** FEAT-PERF-12 (baseline). **Feeds:** FEAT-PERF-24 (the same range is set on a RID instance), FEAT-PERF-20 (merged cells).

## Overview & Goal

A crowded region submits 20-25k draw calls a frame; the frame is CPU-bound in the renderer. A prim that would cover
only a few pixels is not worth a draw. Godot can refuse it in C++ (`GeometryInstance3D.VisibilityRangeEnd`), so the
main and the shadow pass both submit fewer draws and **none of our C# runs per frame**: the range is written when a
visual is built or rescaled, and again only when a setting, the window or the field of view changes.

## The rule (`SLNG.Core.SmallObjectCull`)

Hide an object once the screen radius of its bounding sphere is under `pixelRadius` pixels, in the viewer's own pixel
measure, scaled the way the viewer scales every size-derived distance by Object Detail:

```
end = radius / tan(pixelRadius / pixelsPerRadian) * LodScale(lodFactor)
pixelsPerRadian = viewport height (px) / vertical FOV (rad)            // LLFace::calcPixelArea, sCurPixelAngle
LodScale(f)     = 0.9 * f / (1 - 0.1 * f)                              // calcLOD; 1.0 -> 1, 2.0 -> 2.25, 3.0 -> 3.86
```

Never inside 15 m, never for radius >= 1.5 m, and `0` ("no range") where `end >= 1.15 x draw distance` (the sweep hides
the object there anyway, and the engine then skips the check). The engine's end margin (10 % of the end, at least 1 m)
is hysteresis with fade off, so an object on the edge does not flicker as the camera bobs.

### What the reference viewer has, and what this takes from it (`scratch/slviewer`)

The viewer has **no per-object "too small to draw" cull**. The anchors are:

| Source | What it says | Used for |
|---|---|---|
| `LLFace::calcPixelArea`, `llface.cpp:2382-2398` | screen radius = `atan(halfExtent / dist) * sCurPixelAngle`, `sCurPixelAngle = windowHeight / fov` | the unit of the threshold; `ObjectRenderer.ComputeTextureLod` already uses it for textures |
| `LLViewerOctreeCull::checkProjectionArea`, `llvieweroctree.cpp:1446`, with `SceneLoadFront/RearPixelThreshold` 100 / 400 (`llvocache.cpp:467-473`, `settings.xml`) | the one place the viewer skips an object for projecting too small: it does not LOAD it | confirms that "pixels" is the right currency; its threshold is for octree groups, so it is far too aggressive to apply per prim |
| `LLVOVolume::calcLOD`, `llvovolume.cpp:1507` (ported as `VolumeLod`) | `RenderVolumeLODFactor` is in the numerator and in `distance *= 1 - 0.1 * factor` | `LodScale`, so the Object Detail slider moves this cull the way it moves mesh LOD |

`calcLOD` does not scale by the field of view either (fixed `PI/3`). Here the field of view is part of
`pixelsPerRadian`, because a narrower FOV makes the same prim bigger on screen and a static engine range must follow.

### Distances the rule gives (1080p, 60 degrees; `none` = no range)

| bounding radius | Low (4 px, LOD 1.0, draw 64) | Medium (3 px, 1.25, 128) | High (2 px, 2.0, 176) | Ultra (1 px, 3.0, 256) | default (3 px, 1.0, 96) |
|---|---|---|---|---|---|
| 0.05 m | 15 m | 22 m | 58 m | 199 m | 17 m |
| 0.10 m | 26 m | 44 m | 116 m | none | 34 m |
| 0.15 m | 39 m | 66 m | 174 m | none | 52 m |
| 0.20 m | 52 m | 88 m | none | none | 69 m |
| 0.30 m | none | 133 m | none | none | 103 m |
| 0.45 m and up | none | none | none | none | none |

(radius = half the diagonal of the bounds: a 20 cm cube is 0.17 m, a 1 m cube 0.87 m.)

## Engine facts (checked, Godot 4.7-stable)

- The check is per instance while Godot culls (`renderer_scene_cull.cpp`, `VIS_RANGE_CHECK`), from the **camera origin**
  to the instance's AABB centre. The **directional-shadow cascades sit behind the same check**, so one range removes an
  object from both passes. Omni/spot shadow casters are *not* covered (we have few of them).
- With fade **off** (the default) the margin is hysteresis: a hidden instance must come within `end - margin` to show,
  a shown one stays until `end + margin`. No dither pass.
- `tools/visrange-probe` (windowed, 4.7, RTX 4070) confirms it on a club-like mix, one material per object so Forward+
  does not merge draws (4000 prims of 0.2 m + 1500 of 0.5 m + 500 of 2 m, 5-105 m ahead of the camera, 2-split sun):

| config | drawsScene | drawsShadow | total |
|---|---|---|---|
| no ranges | 6001 | 5395 | 11396 |
| the rule, Low (LOD 1.0, 3 px, hide 110 m) | 2774 | 2938 | 5712 |
| the rule, High (LOD 2.0, hide 202 m) | 4770 | 4725 | 9495 |
| the rule, Ultra (LOD 3.0, hide 294 m) | 6001 | 5395 | 11396 |
| every range forced to 40 m (engine check) | 1972 | 2190 | 4162 |

  This is the mechanism, not a prediction for any region: how much a real scene drops depends on how many of its prims
  are small and how far away they are.

## What is, and is not, culled

- **Culled:** a prim with its own `MeshInstance3D` whose bounding radius is small enough (and its split sorted-surface
  children, which take the parent's range when they are made or when it changes).
- **Never (`IsRangeExempt`):** selected or edited prims (`_instanceSuppressed`), the mirror the hero probe is spent on and
  every flagged / legacy / material mirror, a prim carrying a light or a particle emitter (the light and the emitter are
  child nodes the range does not touch, and their prim is one cheap draw), a prim a control avatar draws.
- **Not in this renderer, so unchanged:** avatars, attachments, HUDs, terrain, water, sky.
- **MultiMesh groups (FEAT-PERF-06): no range, on purpose.** A group is *one* draw however many members it has, with one
  AABB spread over the region, and Godot has no per-instance range on a MultiMesh. A group-level range would hide the
  far members together with the near ones, and grouping the small prims out would turn one draw into many. A range
  set on a prim stays on its node while it is grouped (the node has no mesh then) and is live again the moment it
  leaves. `[RangeCull] grouped=N` says how many prims are drawn this way.
- **BUG-PERF-11 sweep:** unchanged and not fought. It still owns `Visible` (draw distance) and the load/release
  distances (min of avatar and camera); the engine range is a separate mechanism measured from the camera only.
- **FEAT-PERF-07 LOD swaps:** a swap assigns a new mesh, which re-reads the bounds and refreshes the range.
- **Selection outline:** a selected prim is exempt, so its outline child never shows over a culled parent.

## Shadows

The range already removes a small object from the sun's cascades together with the main pass. In addition the sweep
stops a small *caster* sooner than the plain `ShadowCasterDistance` (56 m): `ShadowEndDistance` uses 4x the main
threshold (a shadow is soft-filtered and carries less detail than the object), per object, from data the sweep already
holds, so no new per-frame cost. Prims under 0.5 m radius already do not cast at all (`SmallObjectShadows` off), so this
only moves mid-size prims (about 0.5-0.65 m) and, on Ultra, small casters. `[RangeCull] shadowCut=N` counts it.

## Setting, presets, switches

- **Graphics > General > "Hide tiny objects"** slider, 0-8 px (0 = off), saved as `graphics/small_object_cull_pixels`.
  Presets: Low 4, Medium 3, High 2, Ultra 1; default 3. It is not part of preset *detection* (an existing preset stays
  recognised). Object Detail scales it, as for mesh LOD.
- `--small-cull=N` (and `tools/run-client.ps1 -SmallCull N`) overrides it for a run; `0` is the off baseline for the A/B.
- Takes effect within a quarter second: a changed setting, window size, FOV or draw distance bumps an epoch and each
  object re-sets its range on its next sweep visit (a managed read plus, only if the number changed, two writes).
- **Diagnostics:** one line per 5 s next to `[Perf]`:
  `[RangeCull] cullPx lod ppr objects ranged culled exempt shadowCut grouped radius=<7 buckets>`
  (objects = shown prims with their own node; culled = beyond their range from the camera right now; radius buckets, in
  metres of bounding radius: <0.05 <0.1 <0.2 <0.35 <0.5 <1 >=1). `[LiveMaterial]` shows `rangeEnd/shadowEnd/rangeExempt`.

## Known limits

- **A surface built of many tiny unlinked prims** (a mosaic of 5 cm tiles) loses its far tiles one by one, because each
  tile is judged alone. The 15 m floor and the 3 px default keep this to prims that really are specks; the viewer's own
  load rule judges octree groups for the same reason. A linkset-aware rule would be the fix if it is ever reported.
- **FOV and window size are applied with up to a quarter second of lag** (the epoch), and in 8 % steps.
- **The scene's gain depends on how many of its prims are small.** Two real regions' object caches on the dev machine
  (4,344 and 5,010 prims) have 16.6 % and 14.6 % of prims under 0.35 m radius, 23 % and 21 % under 0.5 m, and 62-63 % of
  1 m radius or more. At the default (3 px, uniform ground density to 96 m) that is roughly 5-9 % of prim draws; at
  Low (4 px) 12 %. A club with a lot of small decor will be above that. The next lever for the big and mid-size prims is
  FEAT-PERF-20/24, not a harder threshold.

## Acceptance Criteria

- [x] `SmallObjectCull` rule with unit tests (`tests/SLNG.Core.Tests/SmallObjectCullTests.cs`, 13 tests)
- [x] Range set at build and rescale, never per frame; refreshed on setting/viewport/FOV change; exempt classes listed above
- [x] Composes with the BUG-PERF-11 sweep, LOD swaps, split sorted-surface children, selection and mirrors; groups decided and documented
- [x] Setting in the Graphics page and presets, `--small-cull`, `-SmallCull`, `[RangeCull]` line
- [x] Selftest "small-object visibility ranges" (rule applied, exempt classes, rescale, settings reach the node)
- [x] Engine behaviour verified on 4.7 for both passes (`tools/visrange-probe`)
- [ ] In-world A/B at Sirens Beach (same camera): `-SmallCull 0` vs default vs `-SmallCull 6`, compare `draws`, `drawsScene`, `drawsShadow`, `renderCpuMs`, fps, read `[RangeCull]`
- [ ] In-world visual check: nothing a person would notice vanishing at normal viewing distances (see below)

## In-world test (needs a login, not run by the implementer)

Same spot and camera, three runs, 60 s each after the scene has loaded:

```powershell
tools/run-client.ps1 -Diag -SmallCull 0      # baseline, cull off
tools/run-client.ps1 -Diag                   # default (3 px, or the Graphics setting)
tools/run-client.ps1 -Diag -SmallCull 6      # aggressive, to see where it becomes visible
```

Read from `slng-perf.log`: the `[Perf]` columns `draws drawsScene drawsShadow renderCpuMs fps`, and `[RangeCull]`
(`culled` should be a large share of `ranged` at a crowded spot; `grouped` shows what the cull cannot reach).
Look at: small furniture decor, jewellery-sized props, glitter/sparkle prims, plants' small parts from 20-60 m; walk
toward and away from them and zoom (View > Zoom In) - a prim should not pop inside the distances in the table above.

## Technical Specs & Affected Files

- `src/SLNG.Core/SmallObjectCull.cs`, `tests/SLNG.Core.Tests/SmallObjectCullTests.cs`
- `app/scripts/ObjectRenderer.cs` (state, `RefreshVisibilityRange`, epoch, sweep, tally, hooks), `ObjectRenderer.VisibilityRange.SelfTest.cs`, `SelfTest.cs`
- `app/scripts/RenderConfig.cs`, `Diagnostics.cs`, `UI/GraphicsSettings.cs`, `UI/GraphicsPreferencesPage.cs`, `app/i18n/*.json`
- `tools/run-client.ps1`, `tools/visrange-probe/`

## Sub-tasks / Progress

- [x] Rule + tests
- [x] Renderer integration, exemptions, groups decision
- [x] Setting, presets, CLI, diagnostics, handbook
- [x] Selftest, engine probe
- [ ] In-world measurement and visual acceptance
- [ ] Follow-up if the in-world histogram shows most draws are mid-size: FEAT-PERF-20 (merged cells), not a harder threshold
