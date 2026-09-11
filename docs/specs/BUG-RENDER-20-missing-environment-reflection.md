# [BUG-RENDER-20] Shiny faces get no environment reflection

- **Feature ID:** `BUG-RENDER-20`
- **Track:** `render`
- **Status:** `⏸️ Pending`
- **Owner:** *(unassigned)*
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

Reported live 2026-09-11 with the probe sphere at shiny low/medium/high: *"slng rechts keine
reflexionen"*. Firestorm's dark grey sphere clearly mirrors the courtyard — smeared bright
streaks of the surroundings across its upper half. SLNG shows a sun highlight (correct, and new
in `FEAT-RENDER-19`) but no reflection of the environment at all.

Split out of `FEAT-RENDER-19`, which delivered the highlight half.

## What the viewer does

`softenLightF.glsl:243` puts **two** things behind the same gate:

```glsl
if (spec.a > 0.0) {
    ... the Blinn-Phong sun lobe ...
    applyGlossEnv(color, glossenv, spec, pos.xyz, gb.normal);   // the radiance map
}
```

`applyGlossEnv` (`class3/deferred/reflectionProbeF.glsl:893`):

```glsl
glossenv *= 0.5;                                   // "fudge darker"
float fresnel = clamp(1.0 + dot(normalize(pos), norm), 0.3, 1.0);
fresnel *= fresnel;
fresnel *= spec.a;                                 // the shininess level
glossenv *= spec.rgb * fresnel;
glossenv *= vec3(1.0) - color;                     // fake energy conservation
color.rgb += glossenv * 0.5;
```

So the reflection is Fresnel-weighted at `0.25 * fresnel² * shiny`, clamped to a 0.3 floor
face-on — i.e. weak head-on and strongest at the silhouette, up to ~0.19 of the environment
radiance at shiny HIGH.

FEAT-RENDER-19 ported only the sun lobe and deliberately took the conservative 4% dielectric F0
for its strength.

## What was already measured (Godot 4.7, empirical)

Ruling things out cost several rounds; these results should not be re-derived.

| ambient source | sky_contribution | ambient_light_energy | reflection (centre) |
|---|---|---|---|
| SKY | 0.0 | 0.00 | 0.0000 |
| SKY | 0.0 | 0.25 | 0.1647 |
| SKY | 0.0 | 1.00 | 0.2196 |
| COLOR | 0.0 | 0.25 | 0.1647 |
| COLOR | 0.0 | 1.00 | 0.2196 |

1. **Godot scales the specular IBL by `ambient_light_energy`, and switches it off entirely at 0.**
   Not documented anywhere obvious. SLNG's driver runs an ambient energy around 0.25, which by
   this table should still leave ~75% — so this is *not* on its own the explanation.
2. **`AmbientSource.Sky` with `sky_contribution = 0` honours a driver-set ambient colour.**
   `EnvironmentDriver`'s comment ("Godot ignores AmbientLightColor entirely while the source is
   Sky") holds only at the DEFAULT contribution of 1. That comment should be corrected whichever
   way this bug resolves.
3. `reflected_light_source` (BG vs SKY) made no difference in any configuration.
4. The prim shader variants' `render_mode` lines are clean — `specular_schlick_ggx` everywhere,
   nothing disabling ambient.

## The remaining difference

The measurements above used a `StandardMaterial3D`. SLNG draws prims through its own
`ShaderMaterial` (`prim_common.gdshaderinc`). That is the untested variable and the place to
start: does a custom spatial shader writing `ROUGHNESS` / `METALLIC` / `SPECULAR` still receive
Godot's radiance-map contribution, and does `blend_mix` on an otherwise-opaque prim change that?

## Acceptance Criteria

- [ ] The probe sphere at shiny low/medium/high shows an environment reflection, strongest near
      the silhouette, as `applyGlossEnv`'s Fresnel term predicts
- [ ] Reflection strength tracks the shininess LEVEL
- [ ] The greys and primaries (probe steps 0-5) do not shift — they are `PRIM_SHINY_NONE` and
      must stay exactly where `FEAT-RENDER-19` calibrated them
- [ ] `EnvironmentDriver`'s ambient-source comment corrected to match what was measured

## Technical Specs & Affected Files

- `app/materials/prim/prim_common.gdshaderinc`
- `app/scripts/EnvironmentDriver.cs` — `ApplyAmbient`, the `AmbientLightSource` choice
- Reference: `scratch/slviewer/.../class3/deferred/reflectionProbeF.glsl:893`

## Method

`tools/testassets/probe_lighting.lsl` steps 6-8 (dark grey at shiny low / medium / high), with
step 2 as the matte reference for the same albedo. Shoot from the **same camera position** in
both viewers — a highlight and a reflection both depend on the eye, unlike diffuse shading.
