# [BUG-RENDER-23] A shiny prim's mirror: lost to a material with no specular map, and never wired at all

- **Feature ID:** `BUG-RENDER-23`
- **Track:** `render`
- **Status:** `✅ Done`
- **Owner:** `claude` (graphics-engineer)
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

Reported live 2026-09-14 on Agni, Firestorm and SLNG side by side on the same sphere:
*„die Reflexionen sind wieder blurry geworden — das ging vor 2 oder 3 Tagen schon mal“*, and
after the first attempt at a fix, *„die Kugel ist aber immer noch matt statt mirror“*.

The object is the lighting probe (`tools/testassets/probe_lighting.lsl`) at its shiny-HIGH step:
a grey-20 sphere, **Shiny HIGH**, no specular map, and — because the script sets
`PRIM_ALPHA_MODE_NONE` — a legacy material whose own Glossiness and Environment Intensity are
both explicitly **0**. Firestorm renders it as a chrome ball. SLNG rendered it first as a soft
smear, then (once the invented highlight was gated off) as a flat matte sphere.

## What actually decides this, in the reference viewer

`llvovolume.cpp:5536-5557`, building the draw batch:

```cpp
float spec = alpha[shiny & TEM_SHINY_MASK];     // legacy Shiny: 0 / 0.25 / 0.5 / 0.75
LLVector4 specColor(spec, spec, spec, spec);
draw_info->mSpecColor    = specColor;           // .a  -> glossiness
draw_info->mEnvIntensity = spec;                //     -> environment intensity
...
else if (mat)
{
    if (!mat->getSpecularID().isNull())         // <-- ONLY with a specular MAP
    {
        specColor.mV[3]          = mat->getSpecularLightExponent() * (1.f/255.f);
        draw_info->mSpecColor    = specColor;
        draw_info->mEnvIntensity = mat->getEnvironmentIntensity() * (1.f/255.f);
    }
}
```

Two things follow, and both were wrong here.

**1. The gate is the specular MAP, not the material.** `BUG-RENDER-21` moved it to "has a legacy
material", reasoning from `getSpecular()`'s `#else` branch and from `getShaderMask()`'s SPEC_BIT
— but those describe how `specular_color` is *used*, not where it is *filled*. A material with
no specular map contributes neither glossiness nor Environment Intensity; both keep coming from
the prim's Shiny level, exactly as for a face with no material at all. That is why the probe
sphere, whose material says 0/0, is a mirror in Firestorm: the viewer never reads those zeroes.
It is also why this regressed on 2026-09-12 and not before — `d97fc1c` merged that gate about
five minutes after the build the user last called sharp.

**2. Legacy Shiny has always carried an environment intensity, and SLNG never routed it.** The
same line hands the Shiny level to `mEnvIntensity`. Shiny HIGH is a 0.75-strength mirror in SL —
not a highlight, a *reflection of the surroundings*. SLNG gave a shiny face a 4% dielectric
highlight and nothing else. This is the missing half behind `BUG-RENDER-20`'s entire four-round
history of hand-tuning a sky-tint approximation, and behind `FEAT-RENDER-20` adding a real
`ReflectionProbe` that plain shiny prims still had no path into.

**3. The environment sample is taken at the sharpest mip, always** — `legacyenv =
sampleProbes(pos, normalize(refnormpersp), 0.0)` (`reflectionProbeF.glsl:862-865`), while the
`glossenv` sample seven lines above it uses `(1.0-glossiness)*reflection_lods`. The two
sharpnesses are deliberately independent, and the intensity weighs only the mix
(`applyLegacyEnv:912`). `FEAT-RENDER-20` interpolated the *roughness* by the intensity instead,
which was harmless while only a full-strength mirror ever reached it and is not harmless now
that every shiny prim does: shiny LOW would have been a smear.

## Acceptance Criteria

- [x] The probe sphere at shiny HIGH reflects sky, clouds and horizon with visible detail, not a
      gradient — A/B against Firestorm from one camera position.
- [x] Shiny LOW / MEDIUM / HIGH differ in reflection STRENGTH, not in sharpness.
- [x] A material with a specular map still uses its own SpecExp and Environment Intensity.
- [x] A face with Shiny NONE and no map stays matte: no highlight, no reflection.

## Technical Specs & Affected Files

- `app/materials/prim/prim_common.gdshaderinc`
  - branch gate back to `has_specular_texture`; `has_specular_material` uniform removed with the
    premise that introduced it
  - no-map branch now sets `env_intensity = legacy_shininess` — the missing mirror
  - `out_specular` gated on `glossiness > 0.0` in the map branch (`materialF.glsl:367`)
  - the env block forces `out_roughness = 0.0` flat, never `mix(…, env_intensity)`
- `app/scripts/PrimShaderFamily.cs`, `app/scripts/ObjectRenderer.cs` — `HasSpecularMaterial`
  plumbing removed; the material's scalars are still sent (the shader decides), and the stale
  BUG-RENDER-21 rationale in both files is corrected rather than deleted.
- `app/scripts/ObjectRenderer.cs` — `DebugLegacyMaterials` is now `Diagnostics.Enabled`, so
  `--diag` prints each distinct material's `gloss=`/`env=` without a source edit and a rebuild.
- `app/scripts/Boot.cs` — `AppVersion` `v0.22.127-alpha`.

### Deliberately not changed

The no-map highlight strength stays at Godot's 4% dielectric `out_specular = 0.5`, as
`FEAT-RENDER-19` calibrated it against the probe sphere. The viewer's own `spec.rgb` here is
`vec3(legacy_shininess)`, i.e. it varies with the Shiny level — a smaller, separate question than
the missing reflection, and one that deserves its own A/B rather than a quiet change made on the
way past.

Godot picks the reflection's mip from the same `ROUGHNESS` that shapes the sun highlight, so
forcing roughness to 0 for an env-intensity face also tightens its highlight beyond the viewer's.
The viewer needs no compromise — it has one lod per term. Fixing that properly means a custom
`light()` in every prim variant.

## Sub-tasks / Progress

- [x] Root-caused in `llvovolume.cpp`, the place the values are filled — not in the shader that
      reads them, which is where the previous reading stopped.
- [x] Shader + plumbing fix, stale rationale corrected, `--diag` diagnostic, version bump.
- [x] Build (both), 705 tests, format, shader globals 28/28, selftest 38/38, `project.godot`
      untouched.
- [x] **Confirmed in-world 2026-09-14** (Agni, `v0.22.127-alpha`): *„Super geht nun“*.
