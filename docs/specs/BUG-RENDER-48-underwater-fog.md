# [BUG-RENDER-48] Things below the water surface look as if there were no water

- **Feature ID:** `BUG-RENDER-48`
- **Track:** `render`
- **Status:** `🧪 Review` — `v0.27.60-alpha`; measured in a standalone probe, **not yet seen in-world** (see "Open")
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)
- **Branch:** `fix/BUG-RENDER-48-underwater-fog`
- **Depends on:** `BUG-RENDER-05` (water draws before every default-priority transparent object)

## Overview & Goal

User report, 2026-10-15, Sirens (Agni), a beach with a sailboat and seaweed standing in shallow water:
*"ich hab noch ein problem mit dem wasser an sich, da sieht man die objekte als wäre da kein wasser"* — objects
below the surface are crisp, saturated and untinted, as if there were no water.

Goal: from a camera ABOVE the surface, things below it get what the reference viewer gives them — the water's tint
and fade (fog), its fresnel veil and its refraction — and a tree canopy crossing the waterline is still **not** cut
(`BUG-RENDER-03` / `-05` stay fixed). A camera below the surface is out of scope (see "Not done").

## Root cause

Two separate things, one per kind of face. Both reproduced in a probe (below), neither needed the grid.

### 1. Opaque / scissored / hashed faces: the fog was parsed and never used

`water.gdshader` composites `mix(fb, reflection, df2.x)` where `fb` is the screen texture behind the water pixel
(`water.gdshader`, formerly line ~165). It had no fog at all. The setting is read —
`WaterSettings.FogColor / FogDensity` — and `EnvironmentDriver.ApplyWater` set `slng_water_fog_density` on the
material, but nothing in the shader ever read it (`docs/eep-parity-probe-protocol.md` W7 already said so for the
underwater camera). So an opaque pole 4 m down came out of the water only dimmed by the surface's own fresnel.

### 2. Alpha-BLENDED faces: painted over the water, with nothing of the water applied

`BUG-RENDER-05` gave the water `RenderPriority = -1` so every default-priority transparent object draws after it.
A blended face below the surface is therefore laid over the finished water pixel: no fresnel veil, no refraction,
no fog. Measured with fog off: a blended pole reads **exactly the same below the surface as above it** (0.851 /
0.851 / 0.851 / 0.851 green, sRGB) while an opaque pole beside it is already 0.61–0.71. That is the report, to the
digit. Leaf-textured seaweed is the typical blended content in SL; which kind the user's seaweed is could not be
settled without a login (see "Open") — both are fixed, so it does not matter for the fix, only for the A/B.

## What the real viewer does (scratch/slviewer, shallow clone)

| Piece | Source |
|---|---|
| Water haze pass runs just before `POOL_ALPHA_PRE_WATER`, i.e. before the water copies the screen | `pipeline.cpp:4175-4220` (`doWaterHaze`), `:9032-9114` |
| It draws the water mesh with `waterHazeF.glsl`: view position from the **opaque** depth, `fogged = getWaterFogView(pos)`, `fogged.a = pow(fogged.a, 1.7)`, blend `(ONE, SRC_ALPHA)` → `dst*a + fog.rgb` | `class3/deferred/waterHazeF.glsl:44-70`, `pipeline.cpp:9059` |
| Fog maths: `F = 0.98`, `L = pow(min(t1/t2*t3, 1), 1/1.7)`, `D = pow(0.98, l*kd)`, returns `(srgb_to_linear(kc)*L, D)`; nothing above the plane | `class1/environment/waterFogF.glsl:40-74` |
| Uniforms: plane in eye space, `waterFogKS = 1/max(light.z, 0.3)` (`WATER_FOG_LIGHT_CLAMP = 0.3`), density = plain density with the camera above water | `llsettingsvo.cpp:1073-1136`, `:916`; `llsettingswater.cpp:378` |
| Water then copies the (now fogged) screen and writes `mix(fb, radiance*df2.y, df2.x) + punctual`, opaque | `class3/environment/waterF.glsl:258-280`, `:324-327` |
| Refracted sample rejected when it lands in front of the water: `if (pos.z < refPos.z - 0.05) distort2 = distort` | `waterF.glsl:268-272` |
| Alpha faces split around the water by `waterSign`; below-plane ones fog their own colour with `applySkyAndWaterFog` → `color*D + rgb` (**no** `^1.7` on D here) | `lldrawpoolalpha.cpp:92-121`, `waterFogF.glsl:90-96`, `:115-144` |

The viewer is inconsistent between its two fog paths (`D^1.7` for the opaque frame, `D` for alpha faces). SLNG
follows each path as written rather than "fixing" it.

## Fix

### (a) Water haze, in `water.gdshader`

The sample behind the water pixel is fogged before the fresnel mix, from the pixel's own depth:

- `hint_depth_texture` is read at the (rippled) sample position and turned into a view-space point
  (`INV_PROJECTION_MATRIX`, reverse-Z).
- `eye_h` / `point_h` are heights above **this water fragment's own world height** (a `varying`), not a global
  plane, so a neighbouring region with a different water height fogs against its own surface (the viewer has one
  plane and gets that wrong).
- `fb = fb * pow(D, 1.7) + rgb` — `waterHazeF.glsl`'s result, via `slng_water_fog_height()` in the new
  `app/materials/slng_water_fog.gdshaderinc`.
- The viewer's refraction guard (`waterF.glsl:268-272`) is ported with it: a ripple sample that lands on
  something nearer than the water falls back to the un-rippled pixel. Without it the water smears a shifted copy
  of any object standing in it across itself (a candidate for the "striped ghost copies", see "Open").

### (b) Blended faces, in `prim_common.gdshaderinc`

The nine blended world variants (`prim_blend`, `_doublesided`, `_depth`, `_prepass`, `_avatar` and the four
`_vspec` twins) `#define SLNG_WATER_FOG_BLEND`. In `slng_shade`, a fragment **below the plane with the eye
above it** then gets, instead of the atmospheric haze (the viewer picks one or the other by side of the plane):

`out = (colour * D + fogRGB) * T + R`

- `D`, `fogRGB`: the same fog, `colour * D + rgb` (the viewer's alpha path);
- `T = 1 - df2.x`: the water surface's own fresnel transmission — in the viewer the face is *inside* the frame the
  water then weighs by `1 - df2.x`; here it is drawn after the water, so it applies that weight to itself. Flat
  water (all three wave normals up) stands in for the per-fragment ripple normals;
- `R = df2.x * df2.y * sky(mirrored ray)`: the sheen of reflected sky, from `slng_sky_dome_color()` — a **mirror of
  `sky.gdshader`'s `sky_col`** (skyV.glsl:126-152) without clouds, sun disc or stars. The viewer takes this from its
  reflection probe; the water here takes it from Godot's sky radiance, which a prim shader cannot read.

Opaque, scissored and hashed variants do **not** define it: they are fogged through the frame the water refracts,
and doing both would fog them twice. `WaterFogTests.OnlyTheBlendedWorldVariantsFogThemselves` pins that split from
the shader text, because nothing in the compiler would catch a new variant on the wrong side of it.

Nothing about draw order changed: water stays at `RenderPriority -1` and blended faces stay after it, so the
`BUG-RENDER-03/-05` "water eats foliage" cut cannot come back by this route. (The naive alternative — blended
faces below the plane drawn before the water — makes the water paint opaque over them, because Godot copies
`hint_screen_texture` once before the transparent pass. Not done.)

### Plumbing

| Global (declared in `slng_water_fog.gdshaderinc` and `project.godot`) | Written by |
|---|---|
| `slng_water_fog_color` (vec3, **linear**), `slng_water_fog_density`, `slng_water_fog_ks`, `slng_water_fresnel` | `EnvironmentDriver.ApplyWater` (set before its `waterMaterial == null` return: the prims need them without the water material). `ks` = `WaterFog.KsFromLightHeight(CalculatedLightDirection.Z)` — sun-or-moon, as `getClampedLightNorm()` |
| `slng_water_plane_view` (vec4: world-up in view space, eye height above the plane) | `AvatarController.PublishViewSpaceWaterPlane`, on `RenderingServer.FramePreDraw` (this node places the camera mid-`_Process`, so a write from `_Process` would describe last frame's camera) |
| primary-region water height | `TerrainRenderer.PrimaryWaterHeight` (static, NaN until the region's terrain record exists → the camera then publishes "no water") |

`slng_water_fog_density` moved from a water-material uniform to a global with the same name (the local one was
never read).

### C# mirror

`src/SLNG.Core/WaterFog.cs` holds the viewer's maths — `Compute`, `ApplyHaze`, `ApplyToSurface`,
`FlatSurfaceTransmission`, `FlatSurfaceReflectionWeight`, `ApplyToBlendedSurface`, `KsFromLightHeight` — with no
`using Godot;`. `tests/SLNG.Core.Tests/WaterFogTests.cs` (34 tests) holds it against (i) hand-worked numbers
(straight down through 4 m of default water from 10 m up, `ks = 1`: `L = 0.319205`, `D = 0.850763`), (ii) a
literal vector-form transcription of `waterFogF.glsl` — a different expression of the same formula, so agreement is
evidence — and (iii) the shader text itself (the constants `0.98 / 0.1 / 1.7 / 3 d²`, the `pow(fog.a, 1.7)` in the
water shader). `slng_sky_dome_color` has no C# mirror (it is a copy of the sky's own colour block; the REFL probe
below is its check).

## Measurements (probe `tools/waterfog-probe`, SLNG's own `sky.gdshader`, default water, sun 45°)

Green channel, sRGB, of a pole's front face: 1 m above the water / 0.6 m / 2 m / 4 m below it. `opaque` = `prim_opaque`,
`blend` = `prim_blend` at alpha 1 — the two should look alike.

| camera | | above | −0.6 m | −2 m | −4 m |
|---|---|---|---|---|---|
| 3 m up (~30° down) | **before**, opaque | 0.851 | 0.655 | 0.678 | 0.714 |
| | **before**, blend | 0.851 | **0.851** | **0.851** | **0.851** |
| | after, fog 2, opaque | 0.851 | 0.608 | 0.561 | 0.533 |
| | after, fog 2, blend | 0.851 | 0.612 | 0.600 | 0.600 |
| 1.6 m up (~13°, a beach) | before, opaque | 0.851 | 0.643 | 0.655 | 0.690 |
| | before, blend | 0.851 | 0.851 | 0.851 | 0.851 |
| | after, fog 2, opaque | 0.851 | 0.580 | 0.518 | 0.490 |
| | after, fog 2, blend | 0.851 | 0.565 | 0.549 | 0.557 |
| | after, fog 8, opaque | 0.851 | 0.455 | 0.310 | 0.224 |
| | after, fog 8, blend | 0.851 | 0.467 | 0.365 | 0.310 |

Read: *before*, the blended pole is the same at every depth — the report. *After*, it falls off with depth and
tracks the opaque one. The remaining gap between the two grows with depth, from two causes that are both the
viewer's or ours on purpose: the viewer fogs alpha faces with `D` and the opaque frame with `D^1.7`, and the
blended path's reflection sheen is about 3/4 of the real one (below).

With the fog off (`-Density 0`, fresnel only) a blended pole at −0.6 m reads 0.635 against the opaque pole's 0.655;
before the change it read 0.851. So the fresnel transmission alone removes the "no water" look; the fog adds depth.

Reflection term, isolated (`-Mode refl`: black slabs under the water, left opaque = Godot's real reflection, right
blend = `slng_water_surface_reflection`; sRGB):

| ray | real (blue) | mine (blue) |
|---|---|---|
| near (dz +10) | 0.125 | 0.102 |
| mid (+2) | 0.329 | 0.243 |
| far (−20) | 0.592 | 0.412 |

About 0.5–0.8 of the real sheen in linear light: the real sky also carries clouds, and Godot's BRDF adds its own
terms. Not a tuned constant — a port of the sky's own colour block — but an approximation, noted as such. (The first
attempt, built from the surface atmospherics instead of the sky's block, gave ~0.1 of it: the two are different
formulas.) Before the change a blended face carried none of it.

## Deliberate deviations from the viewer

1. **Sky behind the water is not fogged.** The viewer fogs a pixel with no geometry behind it as if the scene were at
   the far plane (the open sea out to the horizon goes to the fog colour) and then lets its post-water atmospheric
   pass (`pipeline.cpp doAtmospherics`, `hazeF.glsl`) pull distant water back toward the horizon haze. SLNG has no
   such pass over the water: the horizon water has always taken its colour from the sky pixel behind it. Fogging
   that dark opened a navy band between sea and sky in the probe, so `scene_depth > 0.0` gates the fog. The report
   was about objects, not the horizon.
2. **Flat-water fresnel and reflection for blended faces** (see above), instead of the per-fragment ripple normals.
3. **Blended faces fog against the primary region's plane** (one global, as the viewer's one `waterPlane`); the water
   shader itself uses each fragment's own plane height.
4. No post-water atmospherics on blended below-water fragments (the viewer's `applySkyAndWaterFog` picks fog *or*
   atmospherics by side of the plane — SLNG does the same).

## Not done (separate work)

- **Camera below the surface** (`underwater_fog_mod`, the different path-length term): `WaterFog.Compute` and
  `slng_water_fog_height` return "no fog" when the eye is not above the plane, so an underwater camera sees exactly
  what it saw before.
- **Particles below the surface** draw after the water at `RenderPriority 1` and are not fogged.
- **The planar mirror view** (`prim_mirror`) reuses the main camera's plane.
- **Per-ripple fresnel for blended faces**: would need the water's normal map in the prim shader.

## Acceptance criteria

- [x] `dotnet build SLNG.sln` and `app/SLNG.App.csproj`: 0 warnings, 0 errors.
- [x] `dotnet test`: Core 1804, Net 1240, Assets 274 — all pass (34 of Core are `WaterFogTests`).
- [x] `dotnet format SLNG.sln --verify-no-changes` clean; `python tools/check_shader_globals.py`: 36 registered, 0 stale.
- [x] `--selftest`: 140/140 (loads every prim variant and `water.gdshader` through the engine; the `prim_blend_*` variants still match their base's uniform list).
- [x] Probe: a blended pole below the surface is no longer identical to the one above it, and tracks the opaque pole
      (table above). The probe renders with the real GPU path (Forward+, MSAA 2×, `hint_depth_texture` +
      `hint_screen_texture` in one transparent shader) — no shader error.
- [ ] **In-world, same spot (Sirens, the sailboat beach), against Firestorm:** seaweed in shallow water tinted and
      veiled like Firestorm; a tree canopy crossing the waterline NOT cut. Needs a person logged in.

## Open

- **Which seaweed is it?** The report cannot be tied to a face kind from the logs on disk. To settle it: run
  `tools/run-client.ps1 -Diag`, stand at the spot and read the `[FaceAlpha] <uuid> … -> Blend|Scissor|Opaque`
  verdict for the seaweed's texture (or `-TraceObject <uuid>`). Both kinds are covered by this fix.
- **"A straight diagonal edge along the plants and striped ghost copies at the lower left"** (from the screenshot) are
  not explained by the root causes above and were **not reproduced** in the probe, which has no ripple normal map and
  a fine water mesh. Two hypotheses, to be checked in-world after this fix: the ghost copies are the water's
  refraction smearing a nearer object (the guard in (a) removes that); the diagonal edge is the coarse 8×8-per-256 m
  water mesh piece-wise-linearising the wave UVs (`water.gdshader` header: "the viewer's coarse water mesh
  piecewise-linearises it") — a triangle seam, independent of the plants. If either remains after this fix, it is
  its own ticket.
- Cost: +1–2 depth taps and ~40 ALU in the water fragment; blended variants pay a branch and, below the plane
  only, the fog + sky-colour maths. Not measured with the perf overlay.

## How to re-measure

```powershell
tools/waterfog-probe/run.ps1 -Density 2 -Eye 1.6 -Mode both   # the PROBE lines above
tools/waterfog-probe/run.ps1 -Density 0 -Mode refl            # the reflection term in isolation
```

It copies the viewer's *current* shaders in first. Windowed — headless has no GPU.
