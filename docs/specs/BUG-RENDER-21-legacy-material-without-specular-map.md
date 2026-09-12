# [BUG-RENDER-21] A legacy material with no specular map loses its glossiness and Environment Intensity

- **Feature ID:** `BUG-RENDER-21`
- **Track:** `render`
- **Status:** `🧪 Review`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

Found 2026-09-12 while debugging `BUG-RENDER-20`/`FEAT-RENDER-20` (reflection probes), and
separate from it. In `ObjectRenderer.BuildFaceMaterialAsync` the **whole** legacy-material
specular block sat behind `if (lm.SpecularMap != Guid.Empty)`, and that block was the only place
that ever set `PrimShaderFamily.SpecularGlossiness` (SL's SpecExp) and
`PrimShaderFamily.SpecularEnvironment` (SL's Environment Intensity).

So a face whose legacy material raises Shininess and/or Environment Intensity but assigns **no
specular map** had both values dropped before they reached the shader, and then fell through
`slng_shade()`'s `if (has_specular_texture)` test into the plain `legacy_shininess` branch, which
ignores both uniforms — and which, for such content, is almost always `0`, i.e. fully matte.

This is not an exotic authoring case. The Build floater's Texture tab lets a creator raise
Shininess and Environment Intensity without ever touching the specular map slot, and Environment
Intensity is precisely the control that gives content its mirror-like look.

## What the viewer actually does — verified against `scratch/slviewer`

The claim to check was "the presence of a legacy MATERIAL, not of a specular MAP, selects the
material path". It holds, and every step of it is in the vendored source rather than inferred:

**1. The map is inside the `#ifdef`; the two scalars are not.**
`class3/deferred/materialF.glsl`, `getSpecular()` and `main()`:

```glsl
vec4 getSpecular()
{
#ifdef HAS_SPECULAR_MAP
    vec4 spec = texture(specularMap, vary_texcoord2.xy);
    spec.rgb *= specular_color.rgb;
#else
    vec4 spec = vec4(specular_color.rgb, 1.0);      // no map: the tint, alpha 1.0
#endif
    return spec;
}
...
vec4  spec       = getSpecular();
float env        = env_intensity * spec.a;          // no map -> env_intensity * 1.0
float glossiness = specular_color.a;                // never behind the #ifdef
```

**2. A material with no maps still compiles to a material shader and still lands in the material
pass.** `LLMaterial::getShaderMask()` (`llprimitive/llmaterial.cpp`) ORs in `SPEC_BIT (0x4)` only
`if (getSpecularID().notNull())` and `NORM_BIT (0x8)` only for a normal map — so a bare material
yields mask `0`, an ordinary index into `llvovolume.cpp`'s material-pass table whose entry `0` is
`LLRenderPass::PASS_MATERIAL`. Nothing anywhere rejects a mapless material.

**3. The defaults are not neutral.** `llprimitive/llmaterial.h:55-57` and `llmaterial.cpp:55`:
`DEFAULT_SPECULAR_LIGHT_EXPONENT = (U8)(0.2f * 255)` = 51, `DEFAULT_ENV_INTENSITY = 0`,
`DEFAULT_SPECULAR_LIGHT_COLOR = (255,255,255,255)`. A material therefore always carries a
**full-white** specular colour and a non-zero glossiness unless the creator lowers them.

**4. A material beats legacy Shiny outright — and the existing code comment had the reason
subtly wrong.** `llface.cpp:1412` does pack shininess into the vertex alpha whenever
`!mat || mat->getSpecularID().isNull()`, i.e. *including* for a materialed face with no map. But
`materialF.glsl`'s deferred branch never reads `vertex_color.a` for shininess — it writes
`frag_data[1] = vec4(spec.rgb, glossiness)` from `specular_color` — so that packed value is dead
for any face drawn through the material pass. The precedence rule is "a MATERIAL wins", not
"a MAP wins". SLNG's `else if (legacy_shininess > 0.0)` branch was therefore shading these faces
with a number the viewer would have ignored.

**5. Full Environment Intensity really does replace the surface colour.** `applyLegacyEnv`
(`class3/deferred/reflectionProbeF.glsl:904`) ends `color = mix(color.rgb, reflected_color*0.5,
envIntensity)` — at `env = 1.0` the diffuse term is gone entirely and only the reflection remains.
That is structurally what `METALLIC = 1.0` does in Godot, which is why folding EnvIntensity into
metalness (FEAT-RENDER-04's approximation) turns out to be the right *shape* and not just a
convenient one. It is also called **outside** the `if (spec.a > 0.0)` gate, so env applies even at
glossiness 0 — matching SLNG's version, where `out_metallic` is written inside the branch that the
material's mere existence now opens.

## The fix

- `app/materials/prim/prim_common.gdshaderinc` — new `uniform bool has_specular_material`; the
  branch tests it instead of `has_specular_texture`, and starts from `vec3 spec = specular_tint;`,
  multiplying the texture in only when there is one. Mathematically identical for a face that
  *does* have a map (a commuted multiply), so that path is unchanged by construction.
- `app/scripts/PrimShaderFamily.cs` — `HasSpecularMaterial`.
- `app/scripts/ObjectRenderer.cs` — tint / glossiness / environment now set whenever `lm` exists;
  only the texture and its UV placement stay behind the `SpecularMap != Guid.Empty` gate.
- Three stale comments corrected from "a specular MAP takes precedence" to "a legacy MATERIAL
  takes precedence", with the `materialF`/`llface` reasoning above.

## Verification — rendered, and looked at

`BUG-RENDER-20`'s round 4 is the standing warning here: three numbers moving in the expected
order is *not* evidence a fix looks right. So this was verified by rendering PNGs and reading
them, with the numbers only as a cross-check.

**Method.** `scratch/probes/probe_specmaterial.gd` (throwaway, not committed), run as
`godot --path app -s ../scratch/probes/probe_specmaterial.gd` — **not** `--headless`, which forces
the dummy driver and never populates a viewport texture. Six spheres of identical albedo
(0.35 grey) in a courtyard of strongly coloured walls, all drawn with the real
`prim_opaque.gdshader`, under `Boot.SetupEnvironment`'s own configuration
(`res://materials/sky.gdshader` as the sky material, `AmbientSource.Sky`, `ToneMapper.Linear`).
The identical script was run twice, once against the fixed `prim_common.gdshaderinc` and once
against `git show HEAD:`'s, so "before" is the real old shader and not a simulation of it.

| # | case | before | after |
|---|---|---|---|
| 0 | no material, Shiny NONE | matte | **bit-identical** |
| 1 | no material, Shiny HIGH | small tight highlight | **bit-identical** |
| 2 | material, no maps, SL defaults (51 / 0) | matte | broad soft sheen |
| 3 | material, no maps, 255 / 0 | matte | tight bright highlight |
| 4 | material, no maps, 255 / 255 | matte | dark, mirror-like, reflects the walls |
| 5 | material, no maps, 51 / 255 | matte | dark, blurred reflection |

**The bug, visually:** in the "before" frame spheres 2-5 are indistinguishable from each other
*and* from the matte control, across the entire SpecExp 51→255 and Env 0→255 range. Every one of
those settings was reaching the client and being thrown away.

**Regression bound, measured:** with no `ReflectionProbe` in the scene (the state `main` ships
today) spheres 0 and 1 render **mean |Δ| = 0.00 per channel** — bit-identical, not "close". A face
with no legacy material cannot reach the changed branch, and the render confirms it rather than
asserting it.

**One artefact chased and cleared.** An early pass used a `ProceduralSkyMaterial` stand-in, and
sphere 4 came out with a stark, dead-straight horizontal seam across its equator — exactly the
shape `BUG-RENDER-20` round 3 shipped and round 4 reverted. It is not a shader defect: a fully
metallic ball mirrors its environment, and that environment's sky/ground boundary is a hard line.
Re-rendered against SLNG's actual `sky.gdshader`, the seam is gone and the reflection is a smooth
sky gradient. Worth recording because the same picture would have been read as a regression.

## Acceptance Criteria

- [x] A legacy material with no specular map delivers its SpecExp and Environment Intensity to
      the shader
- [x] The material path is selected by the presence of a material, matching `getSpecular()`'s
      `#else` branch, with `specular_tint` standing in for the missing map
- [x] A face with a specular map renders exactly as before (identical arithmetic; the reordered
      multiply is commutative)
- [x] A face with no legacy material renders bit-identically (measured, 0.00 per channel)
- [x] Verified by rendered before/after PNGs that were actually examined, not by sampled numbers
      alone
- [ ] **Live A/B against Firestorm on real materialed content** — see below

## Known limitations, stated plainly

1. **This is a broad visual change.** Every face carrying a legacy material without a specular
   map moves from the `legacy_shininess` branch to the material branch, and therefore picks up
   the material's defaults — SpecExp 51 (glossiness 0.2 → roughness ≈ 0.59) and a full-white
   specular colour. The **normal-map-only** case, very common in SL, is included. In the probe the
   default-material sphere's mean luminance rose ~37%. The *direction* is viewer-correct (the
   viewer adds a full-strength white Blinn-Phong lobe for exactly this face); the *magnitude*
   rests on FEAT-RENDER-04's choice to map a white specular to Godot `SPECULAR = 1.0` (F0 0.08),
   which this task inherited rather than re-derived.
2. **No live-grid comparison was possible from this session.** No reachable grid, so the one
   check that would settle limitation 1 — a walk-up A/B against Firestorm on real materialed
   content — has not been done. `tools/testassets/probe_lighting.lsl` **cannot** do it: it
   explicitly clears its material (`PRIM_SPECULAR` with `NULL_KEY`, glossiness 0, environment 0),
   so it is unaffected by this bug by construction. Verifying it needs a probe object that
   *carries* a material with no map.
3. **`env` is still scaled by the specular RGB luminance, not by `spec.a`.** The viewer uses
   `env = env_intensity * spec.a`. For the no-map case the two agree exactly (white tint →
   luminance 1.0, and the viewer's `#else` sets `spec.a = 1.0`); for a *mapped* face they differ.
   That is FEAT-RENDER-04's pre-existing approximation, deliberately left untouched here so this
   change cannot alter any face that already rendered correctly.

## Technical Specs & Affected Files

- `app/materials/prim/prim_common.gdshaderinc`
- `app/scripts/PrimShaderFamily.cs`
- `app/scripts/ObjectRenderer.cs`
- `app/scripts/Boot.cs` — `AppVersion`
- Reference: `scratch/slviewer/indra/newview/app_settings/shaders/class3/deferred/materialF.glsl`,
  `.../reflectionProbeF.glsl:904`, `indra/llprimitive/llmaterial.{h,cpp}`,
  `indra/newview/llvovolume.cpp`, `indra/newview/llface.cpp:1412`

## Note on overlap with FEAT-RENDER-20

`FEAT-RENDER-20` is editing the same `slng_shade()` and the same file on its own branch. The two
changes do not touch the same lines — that work removed `slng_env_reflection` above this function,
this one changes the branch condition inside it — but they both land in
`prim_common.gdshaderinc`, so whichever merges second should re-read the merged `slng_shade()`
rather than trusting a clean auto-merge. `AppVersion` here is `v0.22.75-alpha`, deliberately above
`FEAT-RENDER-20`'s in-flight `v0.22.74-alpha`, so the sequence stays monotonic in either order.
