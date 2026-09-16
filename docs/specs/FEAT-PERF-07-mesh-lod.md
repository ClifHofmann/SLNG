# [FEAT-PERF-07] Mesh LOD — stop loading every uploaded mesh at `high_lod`

- **Feature ID:** `FEAT-PERF-07`
- **Track:** `assets` / `render`
- **Status:** `✅ Done (Phase 1)`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

SLNG has **no mesh LOD at all**. `AssetService.Decode` decodes every uploaded LLMesh asset with
`DetailLevel.Highest` (`AssetService.cs:401`) and reads its skin weights out of the `high_lod`
block (`:412`), whatever the object's size or distance. An SL mesh asset ships four baked LOD
blocks precisely so a viewer does not have to do that.

### The measurement

Measured offline against the **3643 mesh assets the client itself cached** during the
2026-09-16 Millenium session (`user://cache/assets/*.mesh`), by parsing each asset's LLSD header,
inflating each `*_lod` block and summing `len(TriangleList)/6`:

| LOD block | Triangles, all 3643 meshes | Share of `high_lod` |
|---|---:|---:|
| `high_lod` — what SLNG loads | 28 163 877 | 100 % |
| `medium_lod` | 6 906 947 | 24.5 % |
| `low_lod` | 815 577 | 2.9 % |
| `lowest_lod` | 38 242 | 0.1 % |

`[Perf]` from that same session reports `tris=26151k` — i.e. the scene really is drawing
essentially the whole cached mesh set at maximum detail.

### What it costs, twice

1. **GPU, steady state.** 26.1 M triangles through the main pass plus the shadow splits plus the
   reflection-probe faces holds an RTX 4070 at ~60 fps with **vsync off**. It is not the CPU: the
   sum of all `[PhaseCost]` entries is ≈ 95 ms per second of wall clock, so instrumented
   main-thread work is ~10 % of the frame.

2. **Main thread, during login.** `BuildArrayMesh` runs on the main thread
   (`ObjectRenderer.cs:3875`) and `[WorkCost]` measures it at **`avgMs=7.70 maxMs=10.0` per
   distinct mesh**. At ~3600 meshes that is ~28 s of pure main-thread work, which is exactly what
   the log shows: `queue=4875 queuePeak=5025`, 22–42 fps, 3–8 hitches/s, worst frames 140–150 ms,
   for minutes after login. A LOD that is a quarter of the vertices is roughly a quarter of that
   build cost, and it is paid on a scene that is *already* mostly cached.

### The rule to copy

Verified against the vendored viewer source rather than invented
(`scratch/slviewer/indra/newview/llvovolume.cpp:1507-1660`, `indra/llmath/llvolumemgr.cpp:33-42`):

```
radius   = mLODScaleBias.scaledVec(scale).length()      // (0.5,0.5,0.5) for a MESH volume
                                                        //   llvolume.cpp:2065 — the cylinder /
                                                        //   circle overrides below it are gated
                                                        //   on NOT being LL_SCULPT_TYPE_MESH
distance = |objectPos - cameraPos|
distance *= sDistanceFactor                             // = 1 - lodFactor*0.1 (llappviewer.cpp:564)
rampDist  = lodFactor * 2
if (distance < rampDist)                                // "boost LOD when you're REALLY close"
    distance = (distance/rampDist)^2 * rampDist
distance *= PI/3
lodFactor *= DEFAULT_FOV / cameraFOV                    // unless IgnoreFOVZoomForLODs
tan_angle = lodFactor * radius / distance
detail    = first i in 0..2 with tan_angle <= {0.03, 0.06, 0.24}[i], else 3
```

`RenderVolumeLODFactor` defaults to **1.0** upstream (`app_settings/settings.xml:10120`);
Firestorm ships it higher, and every viewer exposes it as "Object Detail". Detail 3..0 maps onto
`high_lod` / `medium_lod` / `low_lod` / `lowest_lod`, which is LibreMetaverse's
`DetailLevel.Highest / High / Medium / Low` (`MeshFoundry.cs:844-848`).

Two rules the viewer applies that are worth copying exactly:
- **HUD attachments are always detail 3** (`llvovolume.cpp:1622-1626`).
- **Rigged mesh LODs off the AVATAR**, not off its own bounds — the wearer's camera distance and
  the avatar's animated extents (`llvovolume.cpp:1526-1560`). That is Phase 2.

## Scope

**Phase 1 (this change): world mesh objects.** They are the bulk of the 26 M triangles — the 3643
cached assets above are overwhelmingly scenery. Rigged/attached mesh keeps loading at `Highest`
and is untouched, because the avatar path carries hard-won correctness (skin weights, joint
overrides, `lock_scale_if_joint_position`) that deserves its own verified pass.

**Phase 2 (follow-up): rigged mesh and attachments**, using the viewer's avatar-relative rule.

## Acceptance Criteria

- [x] `AssetService.GetMeshAsync` takes a `MeshDetailLevel` and caches per `(meshId, lod)` — two
      different levels of one asset must never share a cache entry.
- [x] The skin-weight re-decode reads the **same** LOD block that was decoded, not always
      `high_lod`.
- [x] A requested LOD block that is missing, empty, or all-`NoGeometry` falls back **upward**
      toward `high_lod` rather than rendering the object as nothing.
- [x] `ObjectRenderer` picks the level from the viewer formula above at first load and
      **re-evaluates it on the existing cull sweep** as the camera moves, the way BUG-RENDER-19
      already does for procedural prims.
- [x] The GpuCache mesh key includes the LOD (same trap `KeyForShape` documents: the cache is
      trusted over the `MeshData` just handed in, so a shared key silently serves the wrong LOD).
- [x] Per-face material mapping survives a LOD switch (a lower LOD may drop `NoGeometry`
      submeshes; `MeshSubmesh.FaceIndex` must stay the SL face number).
- [x] `RenderConfig.VolumeLodFactor` exists, defaults to the viewer's 1.0, and is exposed as an
      "Objektdetails" slider in the graphics settings so the change is A/B-testable in-world.
- [x] Unit tests written and passing — `VolumeLodTests` (21) + `MeshLodTests` (14), 777 total green.
- [ ] In-world: `[Perf] tris=` drops substantially on the same region, and the post-login
      `queue=` backlog is visibly shorter.

## Technical Specs & Affected Files

- `src/SLNG.Core/MeshDetailLevel.cs` — its doc comment said this enum was *not* for uploaded
  LLMesh assets. It is now, including the off-by-one in the block names.
- `src/SLNG.Assets/AssetService.cs` — `GetMeshAsync(id, lod)`, `FetchAndDecodeMeshAsync(id, lod)`,
  `Decode(id, bytes, lod)` + upward fallback + LOD-correct weights block.
- `app/scripts/ObjectRenderer.cs` — `VisualState.LoadedMeshDetailLevel`, `KeyForMesh(id, lod)`,
  `PickMeshDetailLevel`, cull-sweep re-evaluation, `LoadAndApplyMeshAsync(state, id, lod)`.
- `src/SLNG.Core/VolumeLod.cs` (new) — the viewer's LOD arithmetic, engine-agnostic so it can be
  unit-tested. A viewer-parity formula that cannot be tested is one nobody can safely touch again.
- `app/scripts/RenderConfig.cs` — `VolumeLodFactor`.
- `app/scripts/UI/GraphicsSettings.cs`, `QualityPreferencesPage.cs`, `app/i18n/*.json` — the slider.
- `tests/SLNG.Assets.Tests/MeshLodTests.cs` (new) — LOD keying, fallback, face-number survival,
  the block-name off-by-one.
- `tests/SLNG.Core.Tests/VolumeLodTests.cs` (new) — the viewer threshold table, hand-worked
  distances, monotonicity, the dead band.

## Sub-tasks / Progress

- [x] Phase 1a — `SLNG.Assets`: LOD-aware decode + cache + fallback, with tests.
- [x] Phase 1b — `ObjectRenderer`: pick, key, re-evaluate.
- [x] Phase 1c — the `VolumeLodFactor` setting + slider.
- [x] Phase 1d — in-world verification on Millenium: `tris=`, `queue=`, fps (confirmed 2026-09-16).
- [ ] Phase 2 — rigged mesh / attachments off the avatar's distance and extents.

## What shipped, and what it does not cover

`v0.22.168-alpha`. Both builds + 777 tests + `dotnet format` + `check_shader_globals` +
`--selftest` 39/39 green. **Not yet verified in-world.**

Two deviations from the reference viewer, both deliberate:

- **A 10 % dead band** (`VolumeLod.Hysteresis`). The viewer switches the instant `calcLOD`
  disagrees, because it re-uses an `LLVolume` it already holds. A switch here re-keys the shared
  mesh and re-applies every face's material, so an object parked on a threshold while the camera
  bobs with the walk animation would pay that a few times a second forever.
- **No FOV term.** The viewer scales the factor by `DEFAULT_FIELD_OF_VIEW / getDefaultFOV()`
  (unless `IgnoreFOVZoomForLODs`). SLNG's camera FOV is a user preference *and* changes with
  Alt-zoom, and wiring the live camera FOV in would re-level the whole scene every time the user
  zooms. Left out until there is a reason to want it; it is one multiply if so.

**VRAM note:** the GpuCache can now hold up to four ArrayMeshes per asset instead of one. The
lower three together are ~28 % of the highest, so the worst case is ~1.28x for an asset seen at
every level, against a geometry saving of up to 97 %. LRU eviction applies as before.

## In-world verification (open)

1. Millenium (or any object-dense SL region), same spot as the 2026-09-16 session:
   `[Perf] tris=` should be far below `26151k`, and `queue=` should drain visibly sooner.
2. Walk up to a distant mesh object and confirm it **sharpens** rather than staying coarse — the
   cull sweep re-evaluating upward is the half a static screenshot cannot show (the same gap
   BUG-RENDER-19 left open for prims).
3. Move the **Objektdetails** slider and confirm the scene re-levels within about a second.
4. Compare against Firestorm's own Object Detail slider before judging "too blocky" — SL's
   default of 1.0 is lower than Firestorm ships.
