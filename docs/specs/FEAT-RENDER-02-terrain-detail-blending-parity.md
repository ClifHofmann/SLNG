# [FEAT-RENDER-02] Terrain Detail-Texture Blending Parity

- **Feature ID:** `FEAT-RENDER-02`
- **Track:** `render`
- **Status:** `✅ Done`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)
- **Handover:** [HANDOVER.md](file:///E:/Git/SLNG/docs/HANDOVER.md)

## Overview & Goal

Terrain detail-texture blending did not match the Second Life viewer. Found 2026-08-21 in an A/B
against Firestorm 7.2.4 on OSGrid's Howletts with the neutral EEP parity preset applied: sky, water
and lighting matched closely, but the ground texture pattern visibly differed.

The old `app/materials/terrain.gdshader` blended detail0/1/2 by elevation using three hardcoded
±2 m `smoothstep` bands, and put detail3 on steep faces via a slope test. The viewer does neither.

## What the viewer actually does

Verified by reading the vendored source at `scratch/slviewer`, not from general SL knowledge.

`LLVLComposition::generateHeights` (`indra/newview/llvlcomposition.cpp:461`) turns elevation into a
single continuous **composition value** in `[0, 3]` that indexes all four detail textures:

```c
const F32 slope_squared   = 1.5f * 1.5f;   // 2.25
const F32 xyScale         = 4.9215f;
const F32 noise_magnitude = 2.f;

vec[0] = (origin_global.x + location.x) / xyScale;
vec[1] = (origin_global.y + location.y) / xyScale;

twiddle  = noise2(vec * 0.2222222222f) * 6.5f;   // low frequency, large divisions
twiddle += turbulence2(vec, 2) * slope_squared;  // high frequency
twiddle *= noise_magnitude;

scaled_noisy_height = (height + twiddle - start_height) * ASSET_COUNT / height_range;
scaled_noisy_height = clamp(scaled_noisy_height, 0.f, 3.f);
```

`terrainF.glsl` then blends with three taps of an alpha ramp:

```glsl
mix( mix(detail3, detail2, ramp(v - 2.0)), mix(detail1, detail0, ramp(v)), ramp(v - 1.0) )
```

So the deltas against our implementation were:

1. **All four detail textures are selected by elevation.** There is no slope or cliff texture
   anywhere in the viewer. Our `cliff_weight` was an invention that put detail3 on steep faces and
   left detail2 covering the top elevation band, where the viewer uses detail3.
2. **The selector is a continuous index over four textures**, blending between neighbours — not
   three `smoothstep` bands with a fixed 2 m width.
3. **Perlin noise (`twiddle`) modulates elevation before the band lookup**, at three octaves. This
   is the dominant visible difference: without it our transitions were geometric contour bands,
   where the viewer's are organically mottled. The low-frequency octave alone swings the effective
   height by roughly ±9 m at a ~22 m scale, which is large next to typical `height_range` values.
4. **The noise is sampled in global coordinates**, so the pattern is continuous across region
   borders.
5. **Detail texture UVs repeat every `RenderTerrainScale` metres** (settings.xml default 12.0),
   phased by the region origin so they too are globally continuous. We had a hardcoded 5 m repeat,
   making every detail texture 2.4× too small.

### Four traps in the viewer source

Each of these would have produced a plausible-looking result that is not the viewer's.

- **`indra/llmath/llperlin.cpp` is dead code.** It looks like the terrain noise and has the right
  function names, but nothing in the viewer tree references it. The live implementation is
  `indra/newview/noise.cpp` / `noise.h`, reached through `#include "noise.h"` in
  `llvlcomposition.cpp`. Only the live one seeds `srand(42)` ("we want repeatable noise (e.g. for
  stable terrain texturing), so seed with known value"); `llperlin.cpp`'s table would depend on
  whatever ambient RNG state the process happened to be in.
- **`scratch/noise2D.glsl` is the wrong noise variant.** It is Ashima/stegu *simplex* noise. The
  viewer uses *classic gradient* Perlin on a 256-entry lattice with the cubic ease `3t²-2t³`.
  Identical constants through a different variant give a different pattern.
- **The noise is purely horizontal.** `generateHeights` fills `vec[2] = height / zScale`, but both
  `noise2()` and `turbulence2()` read only the first two components. Elevation never enters the
  noise, and `zScale` is dead.
- **The gradient and permutation tables depend on the C runtime.** `init()` calls `srand(42)` and
  then draws from `rand()`, so the table — and therefore every region's terrain pattern — differs
  between an MSVC-built and a glibc-built viewer. Windows is our parity target, so
  `SlPerlinNoise.MsvcRand` reproduces the Microsoft CRT generator, pinned by a known-answer test.

### The alpha ramp

`IMG_ALPHA_GRAD_2D` is **not** a grid asset. The viewer preloads it from its own skin folder
(`llviewertexturelist.cpp:187`, `alpha_gradient_2d.j2c`), and it is absent from OpenSim's shipped
`TexturesAssetSet`, so there is nothing to fetch from OSGrid.

Decoding the shipped file shows a 256×256 single-channel image that is a family of smooth,
near-monotone descending curves whose transition centre and width drift slowly along V, with V
driven by a separate ~155 m Perlin field (`LLSurfacePatch::eval`). Averaged over V, a
`smoothstep(0.0118, 1.0627, u)` fits that family to within **3.7/255** — against **32.6/255** for
the linear ramp it is otherwise natural to assume. We hold the ramp at that mean curve.

The 1-D `alpha_gradient.tga` (which is plain, and parses without a J2K decoder) settles the
polarity independently: it runs 255 → 0, which is what makes **detail0 the low texture and detail3
the high one**.

### Licensing of the ramp asset — an earlier claim here was wrong

This spec previously justified the fitted curve as avoiding redistribution of a Linden art asset.
That reasoning does not hold and was never checked before it was acted on.

`doc/LICENSE-logos.txt` in the viewer tree licenses the Second Life viewer artwork under
**Creative Commons Attribution-Share Alike 3.0**. Redistribution and adaptation are explicitly
permitted, subject to: shipping the Notice, clearly identifying any changes, and placing the
attribution where copyright notices normally go (a text file, the About window, or a credits page).
Linden trademarks are carved out, but `alpha_gradient_2d.j2c` is a blend lookup table, not a logo.

So embedding the real gradient is available, at the cost of that attribution obligation on the
shipped client. It is a product decision rather than a legal blocker, and it belongs to the repo
owner — not something to route around silently, which is what happened here.

**Not modelled:** the ramp's V-axis drift. See the failed attempt below before trying again.

## Acceptance Criteria

- [x] All four detail textures are selected by elevation; no slope/cliff term remains.
- [x] The composition value is the viewer's continuous `[0, 3]` index, blended between neighbours.
- [x] `twiddle` is ported at all three octaves with the viewer's constants.
- [x] Noise is sampled in global coordinates and is continuous across region borders.
- [x] Detail UVs repeat every 12 m, phased by the region origin.
- [x] Unit tests written and passing (29 new; 220 total).
- [x] Blending happens in sRGB, matching the viewer's legacy path.
- [x] The viewer's real blend ramp is shipped and sampled on both axes, with the CC BY-SA notice
      reaching the user (Preferences → Licences).
- [x] Composition is evaluated per vertex on the 1 m grid, as the viewer does.
- [x] Detail samplers are anisotropic.
- [x] **Verified against ground truth**: heightmap 1024/1024 cells identical to the sim's own
      `terrain.raw` export; composition 221/224 cells identical to an independent Python port of
      the viewer's algorithm run over that export.
- [~] **Visual A/B**: run five times against Firestorm 7.2.4 on Howletts, including with
      known-answer probe textures. Each round found and fixed something real. The last observed
      difference (Firestorm showing more of the low band around the island's edge) could not be
      reproduced in any measurement and may be a misreading of the screenshots — the five
      known-answer points in `tools/testassets/README.md` are there to settle it.

**Left in this state deliberately.** Everything measurable agrees with the viewer; what remains is
one visual impression that no measurement supports. Chasing it further without the point test would
mean tuning against a screenshot, which this task has already shown to be how wrong answers get
made.

## First A/B result (2026-08-21, v0.7.38-alpha) — still wrong, and what that ruled out

Firestorm 7.2.4 vs SLNG on Howletts, same spot, same EEP preset. The large-scale pattern now
matches — the flat tan apron around the island, the position of the brown region in the north-west
— but **SLNG puts noticeably more brown on the island**, where Firestorm stays green.

Howletts uses the OpenSim default terrain set, decoded from our own asset cache:

| slot | asset | mean RGB | reads as |
|---|---|---|---|
| detail0 | `b8d3965a…` dirt | (164, 136, 117) | brown |
| detail1 | `abb783e6…` grass | (66, 89, 49) | **green — the only green slot** |
| detail2 | `179cdabd…` mountain | (160, 148, 134) | pale brown |
| detail3 | `beb169c7…` rock | (134, 137, 136) | grey |

Green comes from exactly one slot, so *any* drift in the composition value — up or down — shows up
as "more brown". That makes this symptom sensitive but not diagnostic on its own.

**Ruled out by measurement, do not re-derive these:**

- **The noise port is exact.** An independent replication of `noise.h init()` + `noise.cpp noise2()`
  written straight from the C source agrees with `SlPerlinNoise` on the permutation table
  (`p[0..11] = 204, 117, 7, 120, 49, 222, 28, 96, 237, 17, 30, 184`), on `g2[0]`, `g2[1]`, `g2[255]`,
  and on `noise2()` at sample points, to 7 decimals. `turbulence2` and `twiddle` likewise.
- **The ramp approximation is not the cause.** Sampling the real `alpha_gradient_2d.j2c` at the V
  values the viewer actually draws (`clamp(noise2·0.75 + 0.5, 0, 1)`, measured: mean 0.500, sd 0.159,
  rows 59–195) and comparing the mean selected slot against our fitted smoothstep gives a maximum
  error of **0.088 slot units** across the whole composition range. Far too small for this.
  Individual rows are also not much sharper than the mean (widths ~0.9–1.16; row 128's 0.64 is an
  outlier, not the typical case).
- **The detail UV scale is right.** Firestorm's `RenderTerrainScale` is 12.0 in its install default
  *and* in every one of the user's graphics presets — checked on disk, not assumed.
- **The corner interpolation is right.** `bilinear()` in the viewer transposes its axes relative to
  the corner names (hence its "Not sure if this is the right math..." comment), and both our shader
  and `SlTerrainComposition.BilinearCorners` reproduce that transposition. Verified algebraically
  twice.
- **The mesh feeds the shader what it thinks.** Terrain vertices are `(x, height, -z)` with the
  region node at Y = 0, so `VERTEX.y` is SL elevation and `(VERTEX.x, -VERTEX.z)` is (east, north)
  in metres.

### Cause found: we were blending in the wrong colour space

The viewer's legacy terrain path blends the four detail textures **while they are still
sRGB-encoded**. `terrainF.glsl` mixes raw texels and writes them straight to the albedo G-buffer;
nothing in that path calls `srgb_to_linear`, `diffuseF.glsl` does not either, and `TCS_SRGB` is
declared in `llrender.h` but appears **nowhere else in the tree**, so the detail textures are not
bound as sRGB. Linearization happens later, once, in the deferred lighting pass. The *PBR* terrain
path is the one that linearizes before mixing (`pbrterrainUtilF.glsl:158`) — and that is not the
path Firestorm takes on OpenSim without PBR terrain enabled.

Our shader declared the detail samplers `source_color`, so Godot linearized them at sample time and
we blended in linear space — the PBR behaviour, against a viewer running the legacy one.

That is not a pedantic difference. Linear blending weights the brighter sample more, and SL's
default terrain set has exactly one green slot against three brown/grey ones. Computed against the
real Howletts textures:

| composition | viewer (sRGB blend) | ours (linear blend) | ΔR | ΔG |
|---|---|---|---|---|
| 0.4 | (134, 122, 96) | (143, 124, 102) | **+9** | +2 |
| 0.6 | (106, 108, 77) | (119, 111, 85) | **+13** | +3 | 
| 1.4 | (95, 107, 75) | (107, 111, 87) | **+12** | +4 |
| 1.6 | (121, 124, 99) | (131, 128, 109) | **+10** | +4 |

At composition 0.6 and 1.6 the blend flips from green-dominant (G ≥ R) to red-dominant. The error is
**exactly zero at the pure slots** (0, 1, 2, 3) and maximal in the transitions — so pure grass and
pure mountain match between the viewers, and everything between them reads browner in ours. Since
the composition ramp is a full-unit crossfade, most of the island is a transition.

Fixed in v0.7.39-alpha: the detail samplers dropped `source_color`, and `sl_terrain_blend` applies
the viewer's `srgb_to_linear` to the blended result instead.

This affects terrain and only terrain. For a single-textured surface, converting at sample time and
converting after are the same thing; the spaces only diverge when several textures are *blended*.

**Still unverified:** the start/range values arriving from the wire. `LogCompositionDiagnostics`
covers that if the colour-space fix does not fully close the gap.

## Second A/B result (2026-08-21, v0.7.39-alpha) — "passt fast"

The colour-space fix closed the brown/green problem. Green is dominant in both viewers, the brown
band along the northern edge matches in position and extent, and the coastline reads the same.

A small residual remains: SLNG's green is slightly lighter and more olive, and the whole frame is
marginally lower-contrast. That reads as overall tone rather than composition — i.e. atmospherics
(FEAT-ENV-01, whose sky/fog mapping is a documented approximation and whose real port is Phase E,
blocked on FEAT-RENDER-01's shader seam) rather than anything in this task. Confirming that is the
next step, and it should not be chased by tuning terrain constants.

The diagnostic did not print on this run and that was a defect in it, not a finding:
`LogCompositionDiagnostics` used `Logger.Info`, but `Diagnostics.cs:39` leaves `Logger.CurrentLevel`
at `Warning` unless diagnostics are switched on, so the lines were suppressed in exactly the kind
of run they exist for (0 of 2049 log lines carried an `[INFO]` prefix). Fixed in v0.7.40-alpha by
printing through `GD.Print`, the way `[ENV]` and `[Boot]` already do.

`LogCompositionDiagnostics` was added to settle it: it recomputes the composition on the CPU
through the same `SlTerrainComposition` maths and logs the per-slot **area split**, which is
directly comparable against a screenshot, plus the raw start/range corners and the height
distribution. It is guarded so it does not fire before the RegionHandshake arrives — terrain
patches arrive first, and the early rebuilds run with all-zero start/range.

Also changed in v0.7.39-alpha, and to be accounted for when reading the next A/B: **terrain detail
textures are now mipmapped.** They had not been, only because that was the pre-existing behaviour;
the shader asks for `filter_linear_mipmap` and Godot silently degrades it to linear without a mip
chain. At 128×128 repeating every 12 m, looking down at a region minifies them several times over.

## Failed attempt: parametric V drift (v0.7.41-alpha, reverted in v0.7.42-alpha)

Prompted by the observation that Firestorm's texture boundaries are crisper than ours.

**The measurement was sound and is worth keeping.** Contrast is *spread*, not mean. At a fixed
composition value the viewer gives neighbouring places different blends, because each samples a
different row of the 2D ramp; we gave every place the same blend. Sampling the real gradient at the
V values the viewer actually draws:

| composition | viewer p10..p90 spread of selected slot | ours |
|---|---|---|
| 0.4 | 0.496 | 0.000 |
| 0.6 | 0.422 | 0.000 |
| 1.4 | 0.504 | 0.000 |
| 1.6 | 0.429 | 0.000 |

That spread is exactly what "crisper boundaries" means, and it is **invisible to any measurement of
the mean** — which is how it survived the earlier "ramp ruled out, max 0.088 slot units" check in
this spec. That check measured the wrong statistic.

**The fix attempted was wrong.** The V field itself was reproduced exactly (same `noise2`, same
~155 m scale from `LLSurfacePatch::eval`), but the V → ramp-shape mapping was approximated by
fitting each row to a smoothstep and storing 16 (centre, width) knots. Two defects:

- **The parameterization is unstable.** Different (centre, width) pairs describe nearly the same
  curve, so the fitted parameters jump between adjacent rows even where the curves do not — width
  swings from 0.74 to 1.32 between two neighbouring sample rows. Interpolating between such pairs
  yields ramps *wider than any real row*, i.e. blends more washed out than simply holding the mean.
- An off-by-one on top: knots measured at V = i/16 were placed at v = i/15, stretching the table.

Measured recovery was only ~2/3 of the real spread, and the live A/B judged it **worse than the
fixed mean ramp**. Reverted.

**If this is picked up again:** do not re-fit a parametric shape. Sample the real
`alpha_gradient_2d.j2c` — which the licence permits (above). The V field, the noise scale and the
three-tap lookup are all already correct in the code; only the ramp lookup itself would change.

## Resolution: ship the viewer's own ramp (v0.7.43-alpha)

The licence was the only thing standing in the way, and it did not actually stand in the way.
`alpha_gradient_2d.j2c` now ships as `app/textures/sl_alpha_gradient_2d.png` and the shader samples
it directly on both axes, so the ramp is the viewer's, not an approximation of it.

- **Conversion is lossless.** Re-encoded JPEG 2000 → PNG so Godot loads it natively; verified
  byte-for-byte against the decoded original, and again after Godot's import (9 probes across the
  table, all exact, format L8, no mipmaps).
- **The import settings are load-bearing.** `detect_3d/compress_to=0` above all: Godot's default
  re-imports a texture as VRAM-compressed the first time it is used in a 3D material, which would
  corrupt the table *after* it had already been verified correct. Godot strips comments from
  `.import` files, so this is recorded in the shader include and asserted by
  `TerrainRampAssetTests`.
- **The CPU reference keeps the fitted mean curve**, now explicitly documented as a diagnostic
  stand-in rather than what renders. It is only used for per-slot *area* splits, where it is within
  0.088 slot units — and by construction it has no contrast, so it must not be used to reason about
  boundary crispness.

### Licence compliance

The Second Life viewer artwork is CC BY-SA 3.0 (`doc/LICENSE-logos.txt`). Requirements and how each
is met:

| Requirement | How |
|---|---|
| Ship the notice with the program | `app/THIRD-PARTY-NOTICES.md`, inside the Godot project so exports include it; added to `export_presets.cfg`'s include filter |
| Reach the user | Client shows it under **Preferences → Licences** (`LicensesPreferencesPage`), which reads `res://THIRD-PARTY-NOTICES.md` and warns loudly if a build dropped it |
| Identify changes | The notice states the JPEG 2000 → PNG re-encode explicitly, and that values are unchanged |
| Reproduce the notice | Verbatim from `doc/LICENSE-logos.txt` |
| Trademarks excluded | Stated; neither shipped file is a Linden mark |

Found while doing this: **`app/textures/clouds2.tga` was already in the repository**, byte-identical
to the viewer's `indra/newview/app_settings/windlight/clouds2.tga`, with no attribution anywhere. It
is now covered by the same notice.

## Third A/B (v0.7.43-alpha): close-up ground is blurry — anisotropic filtering

At ground level the composition now reads right, but our ground is visibly soft where Firestorm
keeps fine grass detail into the distance. That is texture filtering, not blending.

**Ruled out by measurement first:**

- **Not the texture resolution.** The J2K codestream headers in our own asset cache give
  `Xsiz=128 Ysiz=128` for all four Howletts detail textures. Firestorm has the same 128×128
  assets; ours are not a degraded fetch.
- **Not a degraded upload.** `GpuCache` only downsamples when `screenPixelArea > 0`, and
  `TerrainRenderer` passes the default 0, so terrain uploads at full resolution.
- **Not the UV scale.** Confirmed again at 12 m, and the viewer's `texgen_object` applies
  `texture_matrix0`, which the terrain path leaves as identity.
- **Not 3D render scaling or TAA.** Neither is configured in `project.godot`.

**Cause:** the shader declared `filter_linear_mipmap`, which is *not* anisotropic in Godot.
`rendering/textures/default_filters/anisotropic_filtering_level` only applies to samplers that ask
for anisotropy. The ground is seen at a grazing angle over most of the screen, so isotropic mip
selection — which picks its level from the largest derivative — blurs across the view direction as
well as along it.

The comparison viewer does have it on: `RenderAnisotropic` is **1** in this user's Firestorm
`settings.xml` and in their "my Standard" graphics preset. Note the Linden *default* is 0, so this
is a setting the user enabled rather than stock behaviour — worth knowing when comparing against a
differently-configured viewer.

Fixed in v0.7.44-alpha: the four detail samplers use `filter_linear_mipmap_anisotropic`, and the
project's anisotropy level goes 4 → 16, matching the viewer, which binds the GPU's maximum when
`RenderAnisotropic` is on.

**Likely wider than terrain:** `ObjectRenderer` / `AvatarRenderer` surfaces will have the same
isotropic filtering, so prims and avatars at grazing angles are probably soft in the same way. Not
changed here — it belongs with FEAT-RENDER-01's shader family rather than in a terrain task.

## Fourth A/B (v0.7.44-alpha): a brown patch the viewer does not have

Overall match is now close; the report was a localised patch of brown on otherwise unbroken grass.

**The diagnostic finally printed**, and it reframes everything above. Howletts:

```
start=[10,10,10,10]  range=[60,60,60,60]  water=20.0
height min=-0.1 mean=5.7 max=25.0
area split  detail0=91.4%  detail1=8.5%  detail2=0.1%  detail3=0.0%
```

The 91% detail0 is seabed — most of the region sits far below the 20 m waterline and is never seen.
What matters is the visible land at h ≈ 20–25 m, where

    value = (h - 10 + twiddle) * 4 / 60  ≈  0.7 … 1.6

**The whole island lives inside the detail0↔detail1↔detail2 transition.** Pure grass is a single
point on that scale (value = 1.0) with brown on *both* sides, and `twiddle` swings ±9 m against a
`(h - start_height)` of only 10–15 m. That is why every approximation in this task showed up as
"too much brown", and why this region is an unusually harsh parity test rather than a typical one.

**Fix applied — composition moves to the vertex stage.** The viewer evaluates the composition on a
1 m CPU grid (`LLVLComposition(mLandp, grids_per_region_edge, region_width_meters /
grids_per_region_edge)` → scale 1.0 m for a 256 m region), reads it per terrain vertex in
`LLSurfacePatch::eval`, and lets the rasterizer interpolate. Our terrain mesh is the same 1 m grid,
one vertex per heightmap cell with no LOD stride, so evaluating in `vertex()` reproduces that
sampling exactly. Per fragment we were instead carrying the ~2.5 m and ~4.9 m octaves at full
strength, where 1 m sampling plus linear interpolation largely averages them away. It is also
substantially cheaper.

**Honest measurement of the size of that effect:** sampling `twiddle` continuously versus
interpolating it from the 1 m grid differs by mean 0.117 m, p90 0.238 m, max 0.556 m of effective
height — which at `range = 60` is only **0.04 slot units**, at most 0.11 for a tight range. So this
is a correctness fix that removes a real source of spurious local excursions, but it is *not* large
enough on its own to account for an obviously visible patch. Said plainly rather than claimed.

## Probe-texture A/B (v0.7.45-alpha): two findings the grass hid

Four flat-hue band textures (`tools/testassets/gen_terrain_probe.py`) replaced the region's grass.
Removing texture content, filtering and colour from the picture immediately surfaced two things
that five rounds of grass-on-grass comparison had not.

### 1. A region without terrain settings was painted in the HIGHEST detail band

Above the island, where Firestorm shows water, SLNG showed a magenta chequerboard — band 3, which
on Howletts (`value` spans only ~0.7–1.6) cannot legitimately appear anywhere.

Cause was a guard added earlier in this task. A region whose RegionHandshake has not arrived
reports `height_range = 0`; the guard turned that into `0.001`, so
`(height - start_height) * 4 / 0.001` overflowed to a huge number and **clamped to 3.0** — the top
detail texture, across the whole region. With a normal terrain set that is the rock texture:
quiet, plausible, and invisible. It took a bright magenta probe to expose it.

The viewer draws no terrain at all in this state (`generateHeights` returns early on
`!mParamsReady`), so there is no correct value — but it has to be the *lowest* band, not the
highest. Fixed in v0.7.46-alpha in both the shader and the CPU reference, and
`LogCompositionDiagnostics` now announces any region being drawn without settings instead of
returning quietly.

This is a good argument for the probe-texture approach generally: the failure was not subtle, it
was *camouflaged*, and only an unnatural palette made it visible.

### 2. The first area-split report was measuring the wrong thing

`detail0 = 91.4%` was almost entirely seabed. Most of a coastal region lies far below the
waterline, and averaging it in swamps the split for the land actually on screen. The diagnostic now
reports the split **above the waterline** separately, plus percentiles of the composition value
there, so it can be compared against a screenshot rather than merely admired.

### Still open after this round

Firestorm shows noticeably more of band 1 (red, the low band) around the island's periphery, where
we show an olive red/green mix — i.e. our composition value runs higher at low elevations. Cause
not yet identified; the above-water percentiles added in v0.7.46-alpha are there to pin it down
against the next probe shot rather than by eye.

## Second probe round (v0.7.46-alpha): numbers, and a hole in my own verification

The diagnostic's above-water split, which is the first genuinely comparable measurement in this
task:

```
area split ABOVE WATER (204 samples)  detail0=27.1%  detail1=71.6%  detail2=1.3%  detail3=0.0%
composition value above water  min=0.10  p10=0.44  median=0.75  p90=1.13  max=1.34
```

Self-consistent: above-water land sits at h ≈ 21.5, so `value = (21.5 - 10) / 15 ≈ 0.77`, which is
the measured median. And 27% of the above-water area *is* weighted to band 0. So the red is not
missing — Firestorm concentrates it into a band while we spread it as olive mixing over a wider
area. Same mean, different distribution: the "spread, not mean" trap this task has now hit twice.

A 32×32 composition **map** was added (north at top, digit = dominant band, letter = blended cell)
because summary statistics demonstrably cannot distinguish those two cases, and laying a map over
the screenshot can.

### `dotnet build SLNG.sln` never compiled the client

`SLNG.App` is deliberately not in the solution — AGENTS.md scopes it to the engine-agnostic
libraries plus tests. Every "build clean" reported during this task therefore covered `src/` and
`tests/` only, and **not one line of `app/scripts/TerrainRenderer.cs` or the shaders was ever
compile-checked by it**. That went unnoticed because Godot builds the C# project on launch, so the
user's own test runs were doing the checking.

It surfaced when a genuine `CS1501` in the new map code passed `dotnet build SLNG.sln` with zero
errors. Verification for anything under `app/` must include:

```
dotnet build app/SLNG.App.csproj
```

## Ground-truth verification against the sim's own terrain export (2026-08-22)

The region's `terrain.raw` estate export closed the last open input. Format: 256×256 cells, 13
bytes each, `height = red * green / 128` (OpenSim `LLRAW.cs` builds its lookup as
`i * (j / 128.0)`), byte 2 carrying the water height — 20, matching what we report. **Row 0 is
north**, established by scoring all orientations against our own map rather than assuming.

With that, every input and every stage is now verified rather than argued:

| Link in the chain | Result |
|---|---|
| Our heightmap vs the sim's export | **1024/1024 sampled cells identical** |
| `start_height` / `height_range` off the wire | 10 / 60, all four corners |
| `noise2` / `turbulence2` vs a C-source replication | exact to 7 decimals |
| Client's composition map vs an independent Python port over ground truth | **221/224 cells** |

The three differing cells are rounding at the "dominant vs mixed" reporting threshold, not
composition differences.

**Conclusion: the client demonstrably computes the viewer's published algorithm on the sim's actual
heightmap.** If Firestorm still renders differently, the cause is no longer anywhere in this chain
— it is either in how the screenshots were being read (which has misled this task more than once)
or in Firestorm departing from Linden's `llvlcomposition.cpp`.

### The point test that decides it

Rather than compare screenshots again, five known-answer points were derived from ground truth and
recorded in `tools/testassets/README.md`. Each has an unambiguous expected band (81–99% weight),
spread across the island. Standing at them in both viewers settles the question locally and
exactly.

Worth noting from that table: all five points lie between 20.9 m and 22.8 m — within two metres of
each other — and it is the Perlin term alone (−9.57 to +4.96) that separates red from green. With
`start_height` 10 against a `height_range` of 60, elevation contributes almost nothing on this
region. That is why Howletts punished every approximation in this task so hard, and why it is an
unusually harsh parity test rather than a representative one.

## Technical Specs & Affected Files

- `src/SLNG.Core/SlPerlinNoise.cs` — **new.** MSVC `rand()`, the `srand(42)` table build, `noise2`,
  `turbulence2`. Engine-agnostic.
- `src/SLNG.Core/SlTerrainComposition.cs` — **new.** `Twiddle`, `Value`, `Ramp`, `Weights`,
  `BilinearCorners`, and the `RenderTerrainScale` constant. The CPU reference implementation.
- `app/materials/sl_terrain_composition.gdshaderinc` — **new.** The per-fragment port. Kept as a
  separate include specifically so **FEAT-RENDER-01 Phase 4** can pull terrain onto the shared
  shader family by including it, rather than rewriting this math a second time.
- `app/materials/terrain.gdshader` — rewritten `fragment()`; the slope term is gone.
- `app/scripts/TerrainRenderer.cs` — builds the 256×1 RGBAF noise LUT from `SlPerlinNoise` (one
  source of truth for the tables) and feeds the per-region noise origins and detail UV phase.
- `tests/SLNG.Core.Tests/SlTerrainCompositionTests.cs` — **new.**

### Deviations from the viewer, on purpose

- ~~**Per-fragment, not per-vertex.**~~ **Withdrawn in v0.7.45-alpha** — this was a deviation
  taken on the assumption that smoother is better, and it is not parity. See the fourth A/B below.
- **Noise origins are reduced modulo the 256 lattice on the CPU.** SL global coordinates reach the
  hundreds of thousands, where float32 quantizes position to a few centimetres. The viewer absorbs
  that because it samples once per metre; per fragment it would show as stair-stepping. `noise2` is
  256-periodic, so the reduction is exact — and the viewer applies the identical trick itself in
  `LLSurfacePatch::eval`.
- **Detail UVs are phased by each region's own origin.** The viewer uses `gAgent.getRegion()`'s
  origin for every region it draws, so the phase is only correct in the region you are standing in.
  Ours matches the viewer exactly there and stays continuous elsewhere instead of stepping.

## Sub-tasks / Progress

- [x] Establish what the viewer does from source, including which noise implementation is live.
- [x] Port the noise and composition to engine-agnostic C# with tests.
- [x] Port to GLSL against a shared table LUT.
- [x] Rewire `TerrainRenderer` uniforms.
- [x] `dotnet build`, `dotnet test`, `dotnet format`, headless Godot boot.
- [ ] Live A/B against Firestorm on Howletts.
- [ ] Fold into FEAT-RENDER-01 Phase 4 by including `sl_terrain_composition.gdshaderinc` from the
      shared shader family.

---

## How it closed (v0.9.8-alpha, confirmed in-world)

Two texture-coordinate flips, found five measurement rounds apart, both the same
convention: **SL's texture V is bottom-origin and Godot's is top-origin.**

1. **`sl_detail_uv`** sampled every detail texture upside down. The s/t plane equations
   were already the viewer's exactly (`lldrawpoolterrain.cpp:242-248`), offset included --
   what was missing is the `v = 1 - v` that `ObjectRenderer.BuildArrayMesh` applies to
   every world mesh. Terrain never went through that path: it carries no mesh UVs at all,
   its coordinate is generated in the shader from position, so it silently kept SL's
   convention.

2. **`sl_terrain_ramp`** sampled the blend ramp's V the same way. `rand_val` reaches the
   viewer as a plain texture coordinate (`llsurfacepatch.cpp:240`), so it is subject to
   the same convention. This one bites harder than it looks: the ramp's rows are not
   interchangeable -- row 128 crosses over in 62 texels, row 160 takes 137 -- so `v -> 1-v`
   does not change which rows are used, it changes WHERE. `rand_val` is a ~155 m field, so
   the terrain came out crisp where the viewer is soft and soft where it is crisp, at
   exactly that scale.

### Why five rounds of measurement could not see either

Every number this task produced is computed from position: composition values,
per-slot area splits, the heightmap comparison, the independent port that agreed to
221/224 cells. **A texture-space flip is invisible to all of it.** The maths was right the
whole time, which is why each round ended with "the numbers check out" and an unexplained
visual impression left over.

Both were found by deliberate asymmetry instead:

- the detail flip by the probe texture's corner marker, which exists for exactly this
  ("If the terrain UVs are flipped or transposed relative to the viewer, this lands
  somewhere else and says so immediately", `gen_terrain_probe.py:126`);
- the ramp flip by `terrain_bands.raw` -- a flat terrace at composition 0.50, where height
  contributes a constant and nothing but the blend decides the pixel -- plus six
  known-answer poles that turned "the crop looks different" into six readings.

### What the poles ruled out on the way

Three of the six sat on the red side of the crossfade and three on the green. All three
green ones were right in Firestorm and all three red ones wrong, which killed the
sign-flip hypothesis outright: a negated twiddle predicted the green three would be wrong
too. A y-mirror and an x/y transpose were eliminated the same way, each failing at least
one point. That left a difference in the blend rather than in the composition, and the
only field in this shader at the observed ~150 m scale is the ramp's V.

### Left open, deliberately

`terrainF.glsl` samples the ramp's ALPHA channel (`texture(alpha_ramp, ...).a`); we sample
`.r`. Our shipped PNG is greyscale with no alpha, so `.r` is the only data present and the
result now matches in-world -- but whether the hand extraction from `alpha_gradient_2d.j2c`
took that channel was never verified. Worth settling if the ramp is ever touched again.
