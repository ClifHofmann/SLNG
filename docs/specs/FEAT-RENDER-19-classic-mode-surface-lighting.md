# [FEAT-RENDER-19] Port SL's `classic_mode` surface lighting

- **Feature ID:** `FEAT-RENDER-19`
- **Track:** `render`
- **Status:** `✅ Done`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

Reported live 2026-09-11 as *"die Schattenseiten sind zu blau, die Sonnenseiten zu rot"*, then
*"Firestorm hat mehr Farbe und Kontrast"*.

SLNG was running the `classic_mode < 1` branch of `calcAtmosphericVarsLinear` while
`Boot.SetupEnvironment` had deliberately been set up for the other one (ADR 0003 established
classic mode for the TONEMAPPER but never carried it into surface lighting). The two halves
contradicted each other.

The colour-space block in `atmosphericsFuncs.glsl:147` is gated:

```glsl
if (classic_mode < 1) {
    amblit = srgb_to_linear(amblit);
    amblit = vec3(dot(amblit, vec3(0.2126, 0.7152, 0.0722)));
    sunlit = srgb_to_linear(sunlit);
}
```

Legacy and EEP skies without `reflection_probe_ambiance` take the branch that applies **none** of
it. Applying a transfer function where the viewer applies none does not merely dim: it
exaggerates every colour RATIO by the same 2.4 power. A mildly blue ambient (R/B 0.77) became
strongly blue (0.30); a warm sun (R/B 0.58) became orange (0.27). That is precisely the reported
symptom.

## The classic-mode equation

`softenLightF.glsl:159` and `:226-233`, plus `:280`:

```glsl
sunlit *= 1.35;
da = pow(da, 1.2);
color = srgb_to_linear(amblit*0.9 + linear_to_srgb(min(da, scol)) * sunlit * 0.7);
color *= baseColor;
final_scale = 1.1;
```

Godot sums its lights in LINEAR space and the viewer sums in sRGB, converting once. Those cannot
be made identical by rescaling, so what is matched instead are the two **endpoints** a viewer
actually looks at — fully shadowed (`ClassicShadowRadiance`) and fully sunlit (`ApplySun`).

## Three findings that were measured, not reasoned

1. **Godot runs `srgb_to_linear` on `Light3D.LightColor` AND `Environment.AmbientLightColor`.**
   Measured on 4.7, not assumed: a 0.5 grey light on white albedo at NdotL 1 renders **0.498**,
   where an unconverted colour would render 0.735. `SplitLinear` pre-compensates with
   `LinearToSrgb` so the engine's conversion cancels, and the magnitude stays in energy (which
   Godot multiplies in *after* the transfer function).

2. **`RenderSkySunlightScale` / `RenderSkyAmbientScale` are 1.0, not Linden's 1.5.** Firestorm
   overrides both in its own `settings.xml` with the comment *"fudge factor for matching with
   pre-PBR viewer"*. Verified against
   `FirestormViewer/phoenix-firestorm@master:indra/newview/app_settings/settings.xml`, which also
   confirms `RenderSkyAutoAdjustLegacy = 0`. Shipping Linden's 1.5 raised every albedo without
   touching the sun/ambient ratio, which reads as washed out — **contrast is the ratio, not the
   brightness.**

3. **`prim_common` gave every face without a specular map Godot's default `SPECULAR = 0.5`,**
   where the viewer gates its entire specular branch on `spec.a > 0.0` — zero for
   `PRIM_SHINY_NONE`. Now 0, and legacy shininess is read for the first time.

## Legacy shininess

`TextureEntryFace.Shiny` → `SHININESS_TO_ALPHA` (`llface.cpp:1420`: `{0, 0.25, 0.5, 0.75}`) →
the shared Blinn-Phong→GGX conversion. A specular MAP takes precedence, matching the viewer,
which packs shininess into the vertex alpha only *"if we don't have a specular map"*.

**Trap, fixed in the follow-up commit:** LibreMetaverse's `Shininess` enum holds the value still
packed in the protocol byte's top two bits — `None 0, Low 0x40, Medium 0x80, High 0xC0`
(`TextureEntry.cs:83`) — while the viewer reads `mBump >> 6`. A plain cast fed 64/128/192 into a
0-3 lookup, every level missed, and the feature shipped **inert**: no highlight anywhere. The
failure mode is a silent zero that is indistinguishable from SL genuinely saying matte, which is
why it is pinned by a test rather than left to review.

## Method: the probe sphere

`tools/testassets/probe_lighting.lsl`. `EnvironmentDriver`'s own comment already said *"tuning
this driver against screenshots demonstrably does not work"*, and this task proved it twice: the
first A/B was read backwards, and Linden's 1.5 scales were shipped on a guess.

- **greys** locate a LEVEL error — wrong at *every* albedo means a scale, not a curve
- **pure primaries** locate a per-CHANNEL error, and anything NEUTRAL being added wrongly (4% of
  white reflection is invisible on grey and visibly washes out a saturated colour; a blue sphere
  went violet because what it reflected was the warm courtyard)
- a **blue** error hides in greys almost entirely at 7% of luminance

## Acceptance Criteria

- [x] Shadow sides neutral, not blue; sun sides warm, not orange
- [x] Brightness and saturation match Firestorm across white / grey-50 / grey-20 / R / G / B
- [x] `ambRGB` in `[ENVDBG]` matches the hand-derived classic-mode value (measured
      `(0.142, 0.201, 0.247)` against a prediction of `(0.1415, 0.2011, 0.2465)`)
- [x] Legacy shininess produces a highlight that TIGHTENS from low to high
- [x] Shininess protocol conversion pinned by a test
- [x] Build, 688 tests, format, shader globals, selftest 38/38

## Technical Specs & Affected Files

- `app/scripts/EnvironmentDriver.cs` — `ClassicAmbientSrgb`, `ClassicShadowRadiance`, `ApplySun`,
  `SplitLinear`, `LinearToSrgb`, the `SkySunlightScale` / `SkyAmbientScale` constants
- `app/materials/prim/prim_common.gdshaderinc` — `out_specular` default, `legacy_shininess`,
  `slng_glossiness_to_roughness`
- `app/scripts/ObjectRenderer.cs`, `app/scripts/PrimShaderFamily.cs`
- `src/SLNG.Core/FaceTexture.cs` — `Shiny`, `ShinyGlossiness`
- `src/SLNG.Net/GridSession.cs` — the `>> 6`
- `tests/SLNG.Net.Tests/LegacyMaterialConversionTests.cs`,
  `tests/SLNG.Core.Tests/FaceSurfaceMergeTests.cs`
- `tools/testassets/probe_lighting.lsl`

## Deliberately NOT done

**The `pow(NdotL, 1.2)` midtone curve.** Endpoints match; the midtones stay up to 2.8× dark at
the terminator (viewer 0.279 against our 0.100 at NdotL 0.1). Injecting the curve needs a
`light()` function, which **replaces Godot's entire BRDF** for prims, avatars and terrain at
once. Measured before deciding:

| | reproducible by hand? |
|---|---|
| Lambert diffuse | ✅ 0.00% over the whole sweep |
| Burley vs Lambert at roughness 1 | ✅ below 1/255 |
| Burley diffuse | ❌ 18.8% off, worst **at the terminator** |
| Schlick-GGX specular | ❌ 1.5× to 7× off |

Godot's real `scene_forward_lights_inc.glsl` (4.7-stable) shows why: `specular_amount` is applied
a second time although it is already in `f0`, plus `energy_compensation`, an `A` bias on
`cNdotH`, engine-private `D_GGX`/`V_GGX` and `half` precision throughout. Transcribing that pins
us to 4.7 internals with **no test that would catch drift on a Godot upgrade** — and the price of
getting it wrong is the specular work in this very task.

If it is ever wanted: a variant split (curve-bearing shader for faces with no specular, built-in
lighting for shiny ones). Costs a doubled shader family and slightly different shading between
matte and shiny neighbours.

## Follow-ups split out

- `BUG-RENDER-19` — spheres render visibly faceted
- `BUG-RENDER-20` — environment reflection missing on shiny faces
- `FEAT-ENV-03` — sky-type switch; every sky is currently treated as legacy
