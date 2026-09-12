# [BUG-RENDER-20] Shiny faces get no environment reflection

- **Feature ID:** `BUG-RENDER-20`
- **Track:** `render`
- **Status:** `✅ Superseded by FEAT-RENDER-20` — v4's hand-rolled `slng_env_reflection` approximation
  described below was removed once a real `ReflectionProbe` (FEAT-RENDER-20) was confirmed
  working; none of the code in this spec's history exists in the tree any more. Kept as the
  round-by-round record of why the approximation approach was abandoned in favour of a real probe.
- **Owner:** `claude` (graphics-engineer + orchestrating session)
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

## Round 3: an actual rendered picture, not more arithmetic (same session)

Handed back with an explicit instruction not to trust another round of hand-derived numbers:
render an actual PNG of the probe sphere and look at it, the way a person would. This changed
the outcome, and changed it twice.

**Method.** Extended `scratch/probes/*.gd`'s established pattern (`godot --path app -s
<script>.gd`, not `--headless`, for the reason already established above — headless never
populates a real viewport texture) with `Image.save_png()` on `get_viewport().get_texture()
.get_image()`, so the frame could be read back as an actual image via the Read tool instead of
sampled at single pixels. Built a `WorldEnvironment` with `TonemapMode.Linear` (matching
`Boot.SetupEnvironment` exactly — SLNG does not run ACES, see the comment there), a flat
sky-blue background/ambient standing in for a clear-noon sky, a `DirectionalLight3D` sun, and
the real `prim_opaque.gdshader` `ShaderMaterial` on a sphere at the probe's dark-grey albedo
(0.2, 0.2, 0.2) and each shiny level in turn. The project's own `[shader_globals]` defaults
(`app/project.godot`) were re-set explicitly in the probe script rather than relied on
implicitly, to be certain of exactly which numbers were driving the render — they are the stock
default SL sky (`BlueDensity (0.2447, 0.4487, 0.7599)`, `BlueHorizon (0.4954, 0.4954, 0.6399)`),
not a capture of the live "Millenium" region the original report came from. **Honesty note:**
this session had no way to reach that region's actual EEP values (no client-output log or
captured `.llsd` for it was found on disk, and it was not logged into live) — the numbers below
are for the stock default sky, which is the closest available stand-in, not a confirmed match
to what the user was actually looking at. The shape of the finding (LDR haze colour vs HDR
radiance sample) does not depend on which specific sky is loaded, only the fix's exact tuning
constant might.

**What the v2 code's own screenshot showed.** Rendered unmodified: the sun highlight from
FEAT-RENDER-19 was clearly visible (a small bright disc), and, held up next to a matte control
of the same albedo, the shiny-HIGH sphere's upper rim was *very slightly* less black than the
matte one — a difference only findable by comparing the two images side by side, not something
that reads as "a reflection" on its own. Isolating the two contributions (re-rendering with
`slng_env_reflection`'s call site commented out) showed the delta between shiny and matte was
**entirely** attributable to the custom term — this probe's flat ambient colour (not a real sky
radiance map) gives Godot's own built-in specular IBL nothing directional to reflect, so it
contributed exactly zero here, isolating the new term perfectly.

**Why it was that dim, numerically.** `applyGlossEnv`'s `glossenv` argument in the reference
viewer is an actual captured HDR reflection-probe radiance sample, explicitly clamped only to
`[0, 10]` (`sampleReflectionProbes`, `reflectionProbeF.glsl:890`) — i.e. routinely brighter than
1.0. SLNG's stand-in, `slng_blue_horizon`/`slng_blue_density`, is an LDR Windlight haze colour:
even at its brightest channel it lands under 0.55 once linearized. Applying the viewer's literal
`glossenv *= 0.5; ... *= fresnel; ... *= 0.5;` chain — two attenuations LL tuned against an input
that can be an order of magnitude brighter — to an input structurally ~20x dimmer reproduces
almost exactly the near-invisible result the screenshot showed. This is the hypothesis the
orchestrating session raised, and the probe confirmed it directly rather than by further hand
arithmetic.

**The first fix attempt made it WORSE, and the picture is what caught that.** Dropping both
`*0.5` fudges and boosting the raw colour (tried at `*6.0`) does fix the magnitude — but rendered
and looked at, it produces a bright RING running around the sphere's ENTIRE silhouette, top to
bottom, an unmistakable "glass bubble" look, nothing like Firestorm's reference. The reason is
geometric, not numeric: Fresnel (`clamp(1 + dot(view, normal), 0.3, 1.0)`) depends only on the
angle between the view ray and the surface normal, which is exactly as large at the BOTTOM of a
sphere's limb as at the top — it cannot tell "faces the sky" from "faces the ground." Raising a
Fresnel-only term's overall brightness makes the ring more visible, not less of a ring. A second
attempt raised Fresnel's floor from 0.3 to 0.6 (to soften the top/centre falloff) — rendered,
this just made the WHOLE sphere a uniform lighter grey-blue with a brighter rim on top, still not
a patch, and now also washing out the matte body the reflection is supposed to sit on top of.

**Fix v3 (`v0.22.69-alpha`).** The picture made the missing piece obvious in a way the numbers
alone had not: the reflection's INTENSITY, not only its hue, needs to fall off with `upness` —
`slng_env_reflection` now computes `float patch = upness;` and multiplies it in alongside
`fresnel`: `glossenv *= 8.0 * fresnel * patch;` (both `applyGlossEnv`-literal `*0.5`s dropped;
`8.0` is an empirical boost tuned against the saved screenshots, not derived, documented as such
in the code). This is what actually produces "bright patch on the upper hemisphere, fading
toward nothing below the equator" instead of "ring around the silhouette": Fresnel alone cannot
distinguish top from bottom, so the world-direction-based `upness` term has to carry that part of
the shape, on TOP of hue, not only for it. Tried a steeper `pow(upness, 1.5)` gate first — this
pulled the visible patch in to a thin crescent right at the top rim, not the broad upper-half
patch Firestorm's screenshot shows; a plain linear `upness` spread it correctly, because a
sphere's own foreshortening near the equator already compresses most of the visible falloff into
a fairly narrow screen band without an extra exponent's help. Verified across all three shininess
levels with the identical saved-PNG method: LOW, MEDIUM and HIGH show the same upper-hemisphere
patch shape, cleanly monotonic in both the rendered images and the sampled numbers (probe
upper-mid R channel `0.1098 → 0.1569 → 0.1922`); the matte control and `PRIM_SHINY_NONE` stayed
bit-identical throughout, since `slng_env_reflection` is simply never called when
`legacy_shininess == 0.0`.

**Verification:** `dotnet build` (both `SLNG.sln` and `app/SLNG.App.csproj`, separately),
`dotnet test` (690, 0 failures), `dotnet format` clean, `check_shader_globals.py` clean (28
registered/28 typed/0 stale — no new global uniforms, the fix only changes constants inside
`slng_env_reflection`), `godot --headless --path app -- --selftest` 38/38 (every shader still
compiles, every variant's uniform count still matches its avatar/HUD twin), `git diff
app/project.godot` empty after the selftest run (nothing to revert this time). `AppVersion`
bumped to `v0.22.69-alpha`.

**Honesty note (round 3):** this is the first round of this bug backed by an actual look at a
rendered image rather than single-pixel samples or hand arithmetic, and it changed the outcome
materially (caught the ring artefact that pure numbers would not have surfaced). It is still not
a live-grid A/B against a running Firestorm — the probe's sky is the stock default SL sky, not a
capture of the "Millenium" region the report came from (no `.llsd` capture or reachable
client-output log for that region was found on disk this session), and the `8.0` boost constant
is tuned to look right against this session's own rendered screenshots, not against a
side-by-side Firestorm frame. The next session with live grid access should do that walk-up
comparison before considering this bug fully closed.

## Round 4: round 3's OWN screenshot contradicted its own writeup

User relaunched (log confirmed `[Boot] v0.22.69-alpha`, not a stale build) and reported: *"weiterhin
keine reflexionen"* — round 3 unchanged too.

**What round 3 actually got wrong.** Round 3's writeup above (paragraph "Fix v3") describes its own
result as "bright patch on the upper hemisphere, fading toward nothing below the equator" and calls
the sampled numbers "cleanly monotonic" as if that confirmed the intended shape. The orchestrating
session re-ran round 3's own saved probe script this round and actually looked at the PNG (not just
the three numbers round 3 printed) — and the picture shows something round 3's own writeup
undersells: a stark, dead-straight, perfectly flat line at exactly the world-space equator, with a
uniformly-washed blue-grey upper hemisphere above it and a completely flat black lower hemisphere
below, no gradient at all at the seam. Re-rendering the MATTE control confirmed this line was
entirely the new `upness`-gated intensity term's own doing (the matte control has no such line at
any brightness). This does not read as "an environment reflection" to a human looking at it — it
reads as "the top half of the ball is a different, flatter material," a visibly synthetic artefact,
regardless of how monotonic the three sampled shininess levels were. **The lesson: three numbers
increasing in the expected order is necessary but not sufficient evidence a fix looks right — round
3 had the tool (saved PNGs) to catch this and did not look closely enough at what it saved.**

**Fix (`v0.22.70-alpha`).** Reverted the `upness`-gated intensity entirely. `slng_env_reflection` now
uses `upness` for COLOUR ONLY (`sky_color = mix(slng_blue_horizon, slng_blue_density, upness)`,
unchanged from v2/v3) while INTENSITY goes back to Fresnel alone — `glossenv *= 3.5 * fresnel;`, no
`patch` multiplier at all. This is structurally round 1's approach (a ring around the whole
silhouette, not a patch confined to one hemisphere), re-tuned:

- Re-rendered against the SAME stock-default-sky probe used in round 3: a soft, continuous,
  believable rim glowing around the ENTIRE silhouette, brightest near grazing angles, hue shifting
  subtly from horizon-tint at the sides/bottom to a cooler zenith-tint very close to the true top —
  no seam anywhere, at any of the three shininess levels. This is a RING, not Firestorm's specific
  asymmetric patch (Firestorm's shape comes from an actual reflection-probe capture of real nearby
  geometry — a courtyard, presumably brighter on one side — which SLNG has no equivalent of and is
  explicitly out of scope, see "the remaining difference" above) — but it is what a Fresnel-driven,
  no-real-probe approximation can honestly produce, and it is exactly what this bug's OWN acceptance
  criteria asks for: "shows an environment reflection, strongest near the silhouette, as
  `applyGlossEnv`'s Fresnel term predicts."
- Re-tuned the magnitude against TWO rendered palettes, not one, specifically because the whole
  reason rounds 1-3 undershot was tuning against a brighter sky than a real region necessarily
  ships: the same stock bright SL sky AND a second, deliberately dim/desaturated dusk-overcast probe
  (`slng_blue_horizon`/`slng_blue_density` around 0.15-0.20, `ambient_light_energy` unchanged at
  0.25) meant to approximate the mood the live reports were actually shot against (a muted pink-grey
  sky, visible in the user's own screenshots) — still without a real capture of "Millenium" itself,
  which remains unavailable on disk. A `sky_color` floor of `max(sky_color, vec3(0.25))` plus a
  `3.5` boost (both read off the saved renders, not derived) keep the rim visibly distinct from a
  matte control in BOTH renders, confirmed by eye, without the bright-sky render looking overblown.
- `PRIM_SHINY_NONE` (probe steps 0-5) is untouched in every render across both palettes — the
  function is structurally never called when `legacy_shininess == 0.0`, unchanged since round 1.

**Verification:** `godot --headless --path app -- --selftest` 38/38 (uniform counts unchanged —
no new global uniform, the fix only edits constants and one line of logic inside
`slng_env_reflection`), `git diff app/project.godot` empty. `dotnet build` (both `SLNG.sln` and
`app/SLNG.App.csproj`) and `dotnet test` (690, 0 failures) reconfirmed, unaffected by construction
since no C# changed. `AppVersion` bumped to `v0.22.70-alpha`.

**Honesty note (round 4):** every round of this bug so far has been verified by rendering an
isolated probe scene and looking at the result — which is what caught rounds 2's near-invisibility
and round 3's hemisphere-seam artefact, both misses that pure numeric sampling did not surface on
its own. That same method has NOT yet been cross-checked against the one thing it cannot stand in
for: an actual screenshot of the live grid, in the real "Millenium" region, next to a running
Firestorm. Round 4's dusk-palette probe is a deliberately constructed approximation of that mood,
not a capture of it. This remains the one verification step every round of this bug has been
missing, and should be the very next thing done before considering this closed.
