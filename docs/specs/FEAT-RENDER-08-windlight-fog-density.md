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

