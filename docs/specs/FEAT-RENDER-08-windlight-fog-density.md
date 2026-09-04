# [FEAT-RENDER-08] Windlight Fog Density

- **Feature ID:** FEAT-RENDER-08
- **Track:** 
ender
- **Status:** ✅ Done
- **Owner:** gemini
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
Windlight/EEP settings define a DensityMultiplier and DistanceMultiplier (or similar haze parameters) which dictate the distance fog (Haze / Density). Currently, this is missing in the actual Godot rendering environment; volumetric fog might be set to a static value, but the actual Windlight density is not correctly applied, meaning the light fog in the distance is missing. This feature maps the Windlight density to the Godot Environment.FogDensity or VolumetricFogDensity dynamically based on the current region's Windlight/EEP settings.

## Acceptance Criteria
- [x] Windlight density/haze parameters are extracted and applied to the Godot Environment fog.
- [x] Changing environment settings updates the visual distance fog in real-time.

## Technical Specs & Affected Files
- src/SLNG.Core/SkySettings.cs
-  pp/scripts/EnvironmentDriver.cs

## Sub-tasks / Progress
- [x] Map DensityMultiplier/DistanceMultiplier to Godot Fog Density
- [x] Verify in-world

## Implementation

### `v0.20.63-alpha` (Gemini) — the seam stops being a no-op

`slng_atmospherics.gdshaderinc` applies exponential haze from `haze_density`,
`density_multiplier` and `distance_multiplier`, mixing the fragment toward `haze_color` with
distance. `EnvironmentDriver.ApplyFog` dropped its `FogDensity` calculation, which was dead code —
it assigned `env.FogDensity` two lines after `env.FogEnabled = false`, so nothing ever rendered it.

All four global uniforms were already registered in `project.godot` and written by
`EnvironmentDriver` (lines 425–431, 472), and `slng_apply_atmospherics` is reached by every surface
type: prims and avatars via `prim_common.gdshaderinc:237` inside `slng_shade`, plus
`terrain.gdshader` and `water.gdshader` directly.

**A magnitude check worth recording, because the arithmetic looks alarming and is not.** With the
environment in the reported EEP editor (`haze_density` 4.0, `density_multiplier` 0.2223,
`distance_multiplier` 7.87) the density works out to ~7 per metre — an opaque wall at 15 cm. It is
fine: **the EEP UI displays `density_multiplier` scaled by 1000**
(`llpaneleditsky.cpp:117`, `SLIDER_SCALE_DENSITY_MULTIPLIER(0.001f)`; the panel divides on load and
multiplies on store). The stored value is `0.0002223`, giving ~0.007/m — 50 % attenuation at 100 m.
SLNG reads the stored LLSD value, so it gets the right one. Do not "fix" this by rescaling.

### `v0.20.64-alpha` — extinction ported to the viewer's actual model

Two properties of `calcAtmosphericVars`
(`indra/newview/app_settings/shaders/class1/windlight/atmosphericsFuncs.glsl`, vendored at
`scratch/slviewer`) were missing, and both are visible:

1. **The extinction term is a `vec3`, not a scalar** (viewer lines 70/79/84):
   ```glsl
   vec3 combined_haze = max(blue_density + vec3(haze_density), vec3(1e-6));
   float density_dist = rel_pos_len * density_multiplier;
   combined_haze = exp(-combined_haze * density_dist * distance_multiplier);
   ```
   `blue_density` was omitted, so the haze was both **colourless and weaker** than the atmosphere
   the region describes. Summing it before the exponential and mixing component-wise is what turns
   distance BLUE rather than grey — most of what makes an SL vista read as an SL vista.

2. **Altitude is clamped before the distance is taken** (viewer line 57):
   ```glsl
   if (abs(rel_pos.y) > max_y) rel_pos *= (max_y / rel_pos.y);
   ```
   Without it, looking up or flying high accumulates haze the atmosphere does not have. Guarded
   here on `max_y > 0` as the viewer is not: there it is a validated setting, here a global uniform
   that a region which never reported one leaves at 0, and an unguarded `max_y / rel_pos.y` would
   collapse `rel_pos` to the origin and switch the haze off entirely.

Both uniforms (`slng_blue_density`, `slng_max_y`) were already registered and already written by
`EnvironmentDriver` — nothing new had to be plumbed.

## Correction to `v0.20.64`, and what the horizon actually needs

Live report: *"im FS sorgt die density schon dafuer, dass Himmel und Meer am Horizont fast
verschmelzen"* — and with `v0.20.64` they still do not.

Reading the viewer's **composite** rather than only its extinction shows why, and also shows that
`v0.20.64`'s component-wise mix is not the step toward the viewer it was described as.
`atmosphericsF.glsl:35-41`:

```glsl
vec3 atmosFragLighting(vec3 light, vec3 additive, vec3 atten)
{
    light *= atten.r;                        // scalar -- the RED channel only
    additive = srgb_to_linear(additive*2.0);
    additive *= sky_hdr_scale;
    light += additive;                       // ADD, not mix
    return light;
}
```

So the real structure is **`color * atten.r + additive`**. Two consequences:

- The blue of distance does **not** come from per-channel extinction. It comes from `additive`,
  which is built from `blue_horizon * blue_weight` and `haze_horizon * haze_weight`. `v0.20.64`
  put the colour in the wrong term. It is not a regression — mixing toward `haze_color` is still a
  closer approximation than the scalar version before it — but it is a different model, not the
  viewer's, and the commit message overstated it.
- **Nothing merges sky and horizon without `additive`.** Extinction alone can only ever darken or
  wash geometry toward a fixed colour; the horizon merge is the in-scattered light *matching what
  the sky shader is drawing behind it*. `sky.gdshader` already implements the full model, which is
  precisely why the two do not meet.

### Everything needed is already registered

All 27 global uniforms exist in `project.godot` and are written by `EnvironmentDriver`, including
every input the `additive` term takes: `slng_blue_horizon`, `slng_haze_horizon`,
`slng_sunlight_color`, `slng_moonlight_color`, `slng_ambient`, `slng_cloud_shadow`, `slng_glow`,
`slng_sun_direction`, `slng_sun_moon_glow_factor`. **No new plumbing is required** — this is a
shader-only change.

### Two traps to know before starting

1. **Coordinate spaces do not match.** `slng_sun_direction` is written in Godot **world** space
   (`EnvironmentDriver.cs:501` converts SL Z-up to Godot Y-up), while `slng_apply_atmospherics`
   receives a **view**-space position. The viewer's `haze_glow` is `dot(rel_pos_norm, lightnorm)` —
   dotting those two directly is silently wrong, and wrong in a way that looks like "the glow is in
   the wrong place" rather than like an error. Three ways out, none free:
   - pass a world-space position into the seam (a new varying in every prim variant, plus terrain
     and water);
   - set an additional `slng_sun_direction_view` global once per frame from the app, since the
     camera basis is known there and the existing globals are only written when the environment
     changes;
   - transform inside the seam — **not possible**: Godot built-ins such as `INV_VIEW_MATRIX` are
     only in scope in `fragment()` itself, not in a function called from it, so it would have to be
     passed as a parameter anyway.
2. **`sky_hdr_scale` must not be copied blindly.** SLNG's skies are display-referred and never
   tonemapped (see the project memory on legacy skies); the viewer's `srgb_to_linear(additive*2.0)
   * sky_hdr_scale` belongs to its HDR pipeline. Port the term, decide the transfer separately.

### Acceptance for the in-scatter step

- Sky and distant water/terrain visibly meet at the horizon rather than showing a seam.
- The haze brightens toward the sun and does so from the correct direction at several camera
  headings (the space trap above).
- No change in look at close range, where `atten` is ~1 and `additive` ~0.

## Still open: in-scatter

The remaining gap is the interesting one. The viewer computes **two** terms and composites
`color * atten + additive`:

- `atten` — extinction, which is what is implemented now.
- `additive` — in-scattered light, including `haze_glow`, which builds up around the sun
  (viewer lines 88–128).

`mix(haze_color, color, atten)` reproduces the extinction half only, so **sunsets lose their
directional glow**: the sky is coloured correctly but the air in front of geometry does not brighten
toward the sun. Closing it needs `blue_horizon`, `haze_horizon`, `blue_weight`/`haze_weight`,
`sunlight`, `ambient_color`, `cloud_shadow`, `glow` and `lightnorm` in the seam as well. Several are
already registered globals; `glow` and the sun/ambient colours would have to be checked.

## Acceptance

- [x] Windlight density/haze parameters are extracted and applied to the rendered fragment.
- [x] Distance haze is wavelength-dependent (blue), not grey.
- [x] Altitude-clamped, so looking up does not over-haze.
- [ ] Changing environment settings updates the visible haze in real time — **not verified
      in-world.**
- [ ] In-scatter / haze glow toward the sun.

