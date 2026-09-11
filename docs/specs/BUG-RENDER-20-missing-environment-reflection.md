# [BUG-RENDER-20] Shiny faces get no environment reflection

- **Feature ID:** `BUG-RENDER-20`
- **Track:** `render`
- **Status:** `✅ Done (code, v2) — not yet re-confirmed live in-world against Firestorm`
- **Owner:** `claude` (graphics-engineer)
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

- [x] The probe sphere at shiny low/medium/high shows an environment reflection, strongest near
      the silhouette, as `applyGlossEnv`'s Fresnel term predicts — verified via probe script
      (isolated Godot scene, not the live grid; see "Honesty note" below)
- [x] Reflection strength tracks the shininess LEVEL — verified monotonically increasing
      low<medium<high in the same probe
- [x] The greys and primaries (probe steps 0-5) do not shift — they are `PRIM_SHINY_NONE` and
      must stay exactly where `FEAT-RENDER-19` calibrated them — verified bit-identical pre/post-fix
- [x] `EnvironmentDriver`'s ambient-source comment corrected to match what was measured

## Technical Specs & Affected Files

- `app/materials/prim/prim_common.gdshaderinc`
- `app/scripts/EnvironmentDriver.cs` — `ApplyAmbient`, the `AmbientLightSource` choice
- Reference: `scratch/slviewer/.../class3/deferred/reflectionProbeF.glsl:893`

## Method

`tools/testassets/probe_lighting.lsl` steps 6-8 (dark grey at shiny low / medium / high), with
step 2 as the matte reference for the same albedo. Shoot from the **same camera position** in
both viewers — a highlight and a reflection both depend on the eye, unlike diffuse shading.

## What was actually found (2026-09-11, empirical)

No interactive desktop-automation tool is available in this environment for a native Godot
window (the sandboxed browser tooling only drives web pages), so live-grid A/B screenshotting
was not possible this session. Verification instead used this project's own established pattern
for exactly this situation — `scratch/probes/*.gd`, a `SceneTree`-rooted script that builds a
small isolated scene and reads pixels back from `get_viewport().get_texture().get_image()` —
run as `godot --path app -s <script>.gd`. **Not `--headless`**: headless forces Godot's `dummy`
rendering driver regardless of `--rendering-driver`, which never populates a real viewport
texture (`texture_2d_get` returns null, confirmed directly). Without `--headless`, Godot opens
real Vulkan (`Vulkan 1.4.351 - Forward+`, an actual GPU device) and renders for real.

**The untested variable named above — closed, with a negative result.** A probe built a
`WorldEnvironment` with a `ProceduralSkyMaterial` sky and no placed `ReflectionProbe`/
`VoxelGI`/`SDFGI` (matching the live scene exactly, confirmed absent by grep at the start of
this session), then rendered a sphere with `prim_opaque.gdshader` (the real shader, real
`ShaderMaterial`, real `legacy_shininess`/`metallic_factor` uniforms) against the identical
sphere with `StandardMaterial3D` at matching METALLIC/ROUGHNESS/SPECULAR for shiny low/medium/
high. Sampled centre (view-parallel normal) and near-silhouette (grazing normal) pixels: **the
two matched almost exactly** at every level, e.g. shiny HIGH centre `(0.1373, 0.1333, 0.1608)`
for both. A diagnostic mirror override (`metallic_factor=1, roughness_factor=0` — Godot's own
exposed uniforms, no shader edit needed) showed an obvious, strong reflection, proving the
pipeline can receive environment radiance at all. Also tested and ruled out: re-driving the
sky's `global uniform`s every single frame (exactly mirroring `EnvironmentDriver.Update`, called
from `Boot._Process`) does not stop Godot's sky-radiance filter from converging — static and
per-frame-churning skies gave the same reflection numbers; and Boot's real post-FX stack (SSAO
radius 1.0/intensity 2.0, SSIL, Glow) made no measurable difference either.

**Conclusion: the rendering pipeline was never broken.** A custom spatial `ShaderMaterial`
receives Godot's automatic sky-radiance specular IBL exactly like `StandardMaterial3D` does, and
mechanically already produced a real, non-zero, shininess-tracking, silhouette-strongest
reflection at FEAT-RENDER-19's existing `out_specular = 0.5` (4% dielectric F0) — this is
consistent with the report ("weak but real" is what a 4% F0 should look like, not literally
zero) and explains why every earlier line of investigation (ambient source/energy/sky
contribution, `render_mode`) came up clean: none of them were the actual gap.

**The real gap:** Godot's built-in metallic-roughness model has exactly ONE strength knob
(SPECULAR/METALLIC, feeding F0) shared between the DIRECT-light specular response and the
automatic sky-IBL reflection. Raising it enough to make the reflection clearly, robustly visible
would also brighten the sun highlight FEAT-RENDER-19 already calibrated and confirmed correct
against Firestorm. The reference viewer does not have this problem — `softenLightF.glsl:243`'s
Blinn-Phong sun lobe and `applyGlossEnv` (`reflectionProbeF.glsl:893`) read the same
`spec`/`glossiness` but apply completely DIFFERENT overall scale constants, so the viewer can
(and does) tune the two independently. Godot's shared built-in cannot.

## The fix

Ported `applyGlossEnv` as its own, independent, additive EMISSION term —
`slng_env_reflection()` in `app/materials/prim/prim_common.gdshaderinc` — instead of trying to
express it through METALLIC/SPECULAR/ROUGHNESS:

- Same Fresnel term as the viewer: `clamp(1.0 + dot(normalize(view_position), normal), 0.3, 1.0)`,
  squared, then scaled by the shininess LEVEL itself (`spec.a` in the viewer) — a factor the old
  flat `out_specular = 0.5` never carried at all (only roughness/tightness varied by level
  before). This is also why the fix shows a materially cleaner low<medium<high progression than
  Godot's built-in IBL alone gave.
- Same final chain: `* 0.5 * fresnel`, `* (vec3(1.0) - base_color)` ("fake energy
  conservation"), `* 0.5`.
- `glossenv` (a real reflection-probe radiance sample in the viewer — a full deferred-renderer
  feature SLNG does not have and was NOT built here) is approximated from `slng_blue_horizon`
  and `slng_ambient` — the SAME Windlight globals `sky.gdshader` itself paints the sky from,
  already in scope via the shared `slng_atmospherics.gdshaderinc` seam, so no new global uniform
  was needed (`check_shader_globals.py` stays clean). This produces a Fresnel-weighted,
  shininess-scaled SKY TINT — it will NOT show actual nearby courtyard geometry the way a real
  placed reflection probe would. Building that is FEAT-ENV work, tracked separately, not this
  bug.
- Called only from the existing `legacy_shininess > 0.0` branch (probe steps 6-8), and skipped
  when `fullbright` (the viewer never runs its specular/env branch for a fullbright face
  either). Steps 0-5 (`PRIM_SHINY_NONE`) get `env_reflection = vec3(0.0)` and are untouched —
  verified bit-identical pre/post-fix in the probe (`(0.0627, 0.0627, 0.1059)` centre,
  `(0.0275, 0.0549, 0.1725)` edge, both times).
- `slng_shade()` gained a `vec3 normal` parameter (Godot's `NORMAL` built-in, already
  view-space in every prim variant's `fragment()`). Since the signature is shared, all 17
  variants' call sites needed the one-line update even though only the 8 non-avatar/non-HUD/
  non-depth "world prim" variants (`prim_opaque[_doublesided]`, `prim_blend[_doublesided]`,
  `prim_scissor[_doublesided/_edge]`, `prim_hash`) can ever reach the branch that uses it.
- A first attempt transformed the reflection vector to world space with `INV_VIEW_MATRIX`
  inside the new helper function, to blend horizon-vs-ambient colour by up/down direction, and
  hit `Unknown identifier in expression: 'INV_VIEW_MATRIX'`. This is the SAME restriction
  `slng_apply_atmospherics`'s own comment already documents for `VIEW_MATRIX` — a
  `fragment()`-only built-in, unreachable from a function `fragment()` calls (which is why
  `AvatarController` publishes `slng_sun_direction_view` as a global instead of transforming
  in-shader). The FIRST cut of this fix worked around it by staying in view space and using a
  flat horizon tint — see "Live re-test and v2 fix" below for why that had to change, and how.
- `EnvironmentDriver.ApplyAmbient`'s comment corrected per the acceptance criteria: it claimed
  Godot ignores `AmbientLightColor` under `AmbientSource.Sky` unconditionally; measured (see
  table above) that this only holds at the DEFAULT `sky_contribution` of 1 — which is what this
  driver actually leaves it at, since it never sets the property — and confirmed neither the
  source choice nor `sky_contribution` affects the specular/IBL reflection amount at all, only
  the flat diffuse ambient term.

**Verification:** `dotnet build` (both `SLNG.sln` and `app/SLNG.App.csproj`, separately),
`dotnet test` (690, 0 failures), `dotnet format` clean, `check_shader_globals.py` clean (no new
globals), `godot --headless --path app -- --selftest` 38/38 (every prim shader compiles, every
avatar/HUD twin's uniform count still matches its base variant). Re-ran the reflection probe
post-fix: shiny low/medium/high all clearly brighter than the pre-fix run and now monotonically
increasing with level (probe centre R channel `0.1176 → 0.1412 → 0.1529`); the matte control
unchanged. `AppVersion` bumped to `v0.22.67-alpha`.

**Honesty note:** this was NOT re-verified against the live grid / a reference-viewer
screenshot in this session (no tool available to drive the native Godot window interactively).
The fix is grounded in: (a) a source-faithful port of the viewer's own formula, (b) empirical,
reproducible probe measurements of every mechanism it touches, and (c) a full green build/test/
selftest — but the acceptance criteria's "matches Firestorm's ~0.19 magnitude at the silhouette"
is a qualitative target this session could not pixel-compare against a running Firestorm. Worth
a live A/B on the next in-world session.

## Live re-test and v2 fix (same session, immediately after)

The user WAS live-testing (screenshots, side-by-side against Firestorm) and reported, unprompted,
that the fix above changed nothing visible: *"ich seh aber noch keine reflexion"*. Confirmed
running the actual fixed build (`[Boot] v0.22.67-alpha` in the client's own log) — not a stale
assembly, the usual first suspect for "a fix has no effect" in this project.

Comparing the two screenshots side by side (same camera position, both viewers, sphere framed
close to head-on): Firestorm's reflection is not a rim effect at all in this framing — it reads
as visibly BRIGHTER across the whole upper half of the sphere, where the surface faces up toward
the open sky, fading toward the lower half. The v1 fix's `glossenv` was a single flat colour, so
its only source of variation was the Fresnel term (viewing angle vs. normal) — nearly zero away
from the silhouette, which is most of a sphere framed close to head-on. A real reflection varies
with which way a point's surface FACES (what part of the environment it points at), which Fresnel
alone — a function of the VIEW ray only — structurally cannot produce.

**v2 fix (`v0.22.68-alpha`):** blend two already-available Windlight globals by how much the
normal faces up, instead of one flat colour: `slng_blue_horizon` (at or below the horizon) toward
`slng_blue_density` (SL's own zenith-leaning scattering term — reusing it as a zenith stand-in
mirrors what `EnvironmentDriver.ApplySkyDome` already does for the sky dome itself), weighted by
`clamp(world_normal_up, 0.0, 1.0)`. No distinct "ground" shader global exists, so a downward
normal reflects the same horizon tint rather than fading to black — a limitation (nothing below
the horizon is modelled), not a claim of accuracy down there.

Getting "which way is up" needs `INV_VIEW_MATRIX`, unreachable from `slng_env_reflection` for the
reason already documented above. Unlike `slng_sun_direction_view`, this could NOT become a new
per-frame global uniform either: the sun moves once a frame, but the camera's basis is different
for every fragment of every frame, so there is no single value to publish. Instead each of the 17
`prim_*.gdshader` variants' `fragment()` computes `(INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).y`
itself (Godot world space: +Y is up) and passes the single float into `slng_shade()`, mechanically
threaded the same way `NORMAL` itself already was — one more parameter on an already-shared
signature, not a new pattern.

Verified: `godot --headless --path app -- --selftest` 38/38 green post-change (shaders still
compile, uniform counts unchanged — `slng_blue_density` was already declared, reused for the sun/
ambient path), `project.godot` untouched. No C# changed, so `dotnet build`/`test` (690) are
unaffected by construction; re-ran both anyway to confirm. `AppVersion` bumped again, to
`v0.22.68-alpha`.

**Still not re-confirmed live against this v2** — the change compiles and passes every automated
check, but has not yet been screenshotted against Firestorm the way v1 was (and v1's own probe
numbers, while real, are exactly what turned out not to be the whole story — a flat 0.15 centre
value can look identical to "no reflection" from some camera angles and obviously present from
others, which a probe sampling fixed points cannot by itself catch). This needs the same live
walk-up/A-B the user was already mid-way through when they reported v1 as not working.
