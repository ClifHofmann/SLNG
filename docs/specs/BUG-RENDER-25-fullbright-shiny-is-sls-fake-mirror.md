# [BUG-RENDER-25] Fullbright + Shiny is SL's fake mirror, and we zeroed the reflection away

- **Feature ID:** `BUG-RENDER-25`
- **Track:** `render`
- **Status:** `✅ Done`
- **Owner:** `claude` (graphics-engineer)
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

Reported live 2026-09-14 (Agni, *Millenium*): a framed wall mirror renders as a flat light-grey
panel. Firestorm shows the room in it — a window, furniture, floorboards, seen from the mirror's
own viewpoint.

Two wrong guesses preceded the measurement, which is why the click dump was built: the object is
neither a PBR material (`BUG-RENDER-23`'s first attempt) nor a flagged real-time mirror
(`BUG-RENDER-24`). The dump settled it in one line — the glass is part `341251525` of the linkset:

```
part 341251525: MESH 4943b2fb ... [Opaque] ... aabb=(0 x 0,77 x 2,29 m)
                | 45 faces tex=740da84d shiny=3 fullbright
```

**Fullbright + Shiny HIGH. No material, no glTF, no reflection-probe block anywhere in the
linkset.** That combination is SL's classic fake mirror, and the reference viewer gives it a
shader of its own.

## What the viewer does with it

`class3/deferred/fullbrightShinyF.glsl`, in full for the part that matters:

```glsl
float env_intensity = vertex_color.a;   // the SHINY level and nothing else (llface.cpp:1412-1427)
vec4  spec          = vec4(0,0,0,0);    // no highlight at all
sampleReflectionProbesLegacy(ambenv, glossenv, legacyenv, vec2(0), pos.xyz, norm.xyz,
                             spec.a, env_intensity, false, amblit);
color.rgb = srgb_to_linear(color.rgb);
applyLegacyEnv(color.rgb, legacyenv, spec, pos, norm, env_intensity);
color.a = 1.0;
```

So the unlit texture and the environment reflection are **mixed by the Shiny level**
(`color = mix(color, reflected_color * 0.5, envIntensity)`, applyLegacyEnv:912) — not chosen
between. Shiny HIGH is 0.75: three quarters of the surface is reflection.

SLNG's `fullbright` branch did the opposite:

```glsl
out_emission += out_albedo;
out_albedo = vec3(0.0);
```

That kills the reflection twice. Godot tints a metallic reflection by the ALBEDO, so albedo 0
reflects nothing at all; and what is left is a flat EMISSION of the mirror's own grey texture,
which is the entire panel the user was looking at. `BUG-RENDER-23` had by then computed the
correct `env_intensity` for this very face (0.75, from the Shiny level) — the fullbright branch
then discarded the surface it applied to.

## The fix

The unlit share stays emission, the reflective share stays a real metallic surface:

```glsl
out_emission += pre_env_albedo * (1.0 - env_intensity);
out_albedo    = vec3(0.5 * env_intensity * env_intensity);
```

`pre_env_albedo` is the surface colour captured before the env block rewrites it toward mid grey;
feeding the greyed value to the emission would wash out exactly the faces carrying the strongest
reflection. The albedo constant is the viewer's two steps folded into Godot's one: the viewer
scales the probe by `envIntensity` and then mixes at `envIntensity` with a further `* 0.5`, while
Godot reflects `probe * albedo` for a metal. Godot's own Fresnel stands in for applyLegacyEnv's
`min(fresnel + envIntensity, 1.0)`.

A fullbright face with Shiny NONE is unchanged — `env_intensity` is 0, so it is the old code
exactly. That includes every fullbright HUD face (see the `unshaded` note in the same block).

## Acceptance Criteria

- [x] The reported wall mirror shows the room, A/B against Firestorm from one camera position.
- [ ] Shiny LOW / MEDIUM / HIGH on a fullbright face differ in how much reflection replaces the
      texture, matching SL's `mix(color, reflected*0.5, shiny)`.
- [x] A fullbright face with Shiny NONE renders exactly as before (emission = albedo, albedo 0).
- [x] Fullbright HUD faces are untouched.

## Technical Specs & Affected Files

- `app/materials/prim/prim_common.gdshaderinc` — `pre_env_albedo` captured before the env block;
  the `fullbright` branch splits between EMISSION and a metallic ALBEDO instead of zeroing.
- `app/scripts/Boot.cs` — `AppVersion` `v0.22.130-alpha`.

## Why this took three IDs

Worth recording, because the pattern is the expensive one:

1. `BUG-RENDER-23` — assumed the surface was an Environment-Intensity material, from the image.
   Wrong object, but it uncovered two real defects (the specular-map gate, and legacy Shiny
   carrying an environment intensity at all) and shipped them.
2. `BUG-RENDER-24` — assumed it was a flagged real-time mirror, from the Firestorm reflection
   showing geometry behind the camera. Also wrong for this object; also real parity work, and the
   protocol gap it closed (LibreMetaverse never parses ExtraParams `0x90`) is worth having.
3. This one — measured. The click dump that made it a one-liner was built during step 2.

The measurement was cheap all along; each guess cost a build-and-relog round trip. Build the dump
first next time.

## Sub-tasks / Progress

- [x] Glass identified from the click dump rather than inferred from the render.
- [x] Ported against `fullbrightShinyF.glsl`, not reasoned from the PBR path.
- [x] Build (both), 711 tests, format, shader globals 28/28, selftest 38/38, `project.godot`
      untouched.
- [x] **Confirmed in-world 2026-09-14** (Agni): *„OK die Spiegelung funktioniert jetzt besser als in Firestorm“*.
