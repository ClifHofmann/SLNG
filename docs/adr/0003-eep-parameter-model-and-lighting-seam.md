# ADR 0003 — The EEP parameter model, and where our lighting seam actually is

- Status: accepted
- Date: 2026-08-21
- Supersedes nothing. Constrains `FEAT-ENV-01` Phase E and `FEAT-RENDER-01`.

## Context

`FEAT-ENV-01` implemented Extended Environment (EEP) far enough to drive Godot's
sun, ambient, sky dome, fog and water from a region's day cycle. A long live
comparison session against Firestorm 7.2.4 on two OSGrid regions (Howletts, a
one-frame sunset; The Dangazi Forest, a seven-frame 24-hour cycle) found and
fixed a series of real bugs — and then hit a wall that no further tuning moved.

That wall is worth an ADR rather than another commit message, because it is not a
bug. It is a structural mismatch between how the real viewer lights a scene and
what our current renderer hands us to light it with, and it silently invalidates
part of what we already built.

This ADR records three things: the **complete EEP parameter set** with its real
ranges and its real consumers, **what we do and do not use**, and the **decision**
about where the remaining work belongs.

All line references were read from source this session, not recalled.

## Firestorm adds nothing to the parameter set

Checked directly: `llsettingssky.cpp` in `FirestormViewer/phoenix-firestorm` and in
`secondlife/viewer` define the **same 54 setting-key constants**, with no key
present in one and absent from the other. Firestorm's EEP additions are presets
(`app_settings/windlight/skies/[TOR] *.xml`) and UI panels
(`panel_fs_settings_water.xml`), not protocol or semantics.

The same holds where it matters most for us: Firestorm's
`llviewertexturelist.cpp::doPrefetchImages` is byte-identical to Linden's,
including `setAddressMode(LLTexUnit::TAM_WRAP)` on the default water normal. There
is no OpenSim-specific branch and no bundled local fallback texture anywhere in
either tree.

**Consequence:** "how does Firestorm do it" and "how does the LL viewer do it" are
the same question for EEP. We can verify against either. What differs between SL
and OpenSim is only *which assets a grid serves* — and OpenSim ships the Linden
"viewer-side" defaults itself, in `bin/assets/TexturesAssetSet/`
(the EEP water normal `822ded49-…` is registered there as `viewer_water`).

## The sky parameters

54 key constants, of which 6 (`width`, `exp_term`, `exp_scale`, `linear_term`,
`constant_term`, `anisotropy`) are sub-keys inside the density-profile layer maps
rather than top-level settings.

Ranges are the viewer's own validators (`llsettingssky.cpp:726-818`, and
`legacyHazeValidationList` at `:157-184` for the nested haze block).

### Atmospherics — the haze model

These live under a `legacy_haze` submap when the document came from legacy
Windlight, and at the top level otherwise. **The viewer's getters check the submap
first** (`llsettingssky.cpp:1383-1411`); a parser that reads only one location
renders a region with default haze and reports no error. All 21 sky frames in our
real OSGrid capture nest them, so this is load-bearing, not defensive.

| Key | Range | Influence |
|---|---|---|
| `ambient` | 0…3 per channel | Base ambient level. **Not** what reaches a surface — see the lighting seam below. |
| `blue_density` | 0…3 | Rayleigh-ish term. With `haze_density` forms `combined_haze`, which splits into `blue_weight`/`haze_weight` and drives the transmittance line integral. |
| `blue_horizon` | 0…3 | Colour the blue term contributes to the additive sky. |
| `haze_density` | 0…5 | Mie-ish term. Also enters `light_atten` at 0.25 weight — the viewer notes equal weighting "seemed too strong". |
| `haze_horizon` | 0…5 | Colour weight of the haze term in the additive sky. |
| `density_multiplier` | 0.0001…2 | Scales the optical path. `density_dist = rel_pos_len * density_multiplier`. |
| `distance_multiplier` | 0.0001…1000 | Extra factor in the transmittance exponent only. |
| `max_y` | 0…10000 | Altitude at which the sky-dome ray is terminated; also multiplies `light_atten`. |
| `gamma` | 0…20 | Tonemapping input (`SKY_HDR_SCALE` is derived from it). |
| `glow` | x 0.2…40, z −10…10 | Sun-halo shape. x tightens/dims, z is the (negative) falloff exponent. |

### Celestial bodies

| Key | Range | Influence |
|---|---|---|
| `sunlight_color` | 0…3 per channel | The light colour for **both** sun and moon — `getMoonlightColor()` returns `getSunlightColor()` verbatim (`:1683-1686`). |
| `sun_rotation` | unit quaternion | Sun direction: `LLVector3::x_axis * sunq`. |
| `moon_rotation` | unit quaternion | Moon direction — **independent**, not the anti-sun. |
| `sun_scale`, `moon_scale` | 0.25…20 | Apparent disc size. |
| `sun_arc_radians` | 0…0.1 | Angular radius used by the shadow/soft-light path. |
| `moon_brightness` | 0…1 | Brightness of the moon **disc**. Reaches only `moonF.glsl` — it does not dim scene lighting. |
| `star_brightness` | **0…500** | Star opacity. The viewer divides by 500 before any shader sees it: `star_alpha = getStarBrightness() / 500` (`lldrawpoolwlsky.cpp:230`). |
| `sun_id`, `moon_id` | UUID | Disc textures. |
| `bloom_id`, `rainbow_id`, `halo_id` | UUID | Bloom/rainbow/halo lookup textures. |

### Clouds

| Key | Range | Influence |
|---|---|---|
| `cloud_shadow` | 0…1 | **Cloud cover.** `cloudDensity = 2 * (cloud_shadow - 0.25)` (`cloudsF.glsl:179`). Also lifts ambient and dims the sky's sunlight term. |
| `cloud_pos_density1` | max (1, 1, **3**) | xy = UV offset of the large layer; **z is the ×10 coefficient on cloud opacity, not the cover amount**. |
| `cloud_pos_density2` | max (1, 1, 1) | xy = offset of the small layer; z weights the second noise octave. |
| `cloud_scale` | 0.001…3 | Texture scale. `< 0.001` discards the cloud fragment entirely. |
| `cloud_variance` | 0…1 | Domain-warp amount; 0 means no shape disturbance at all. |
| `cloud_scroll_rate` | −50…50 | Drift. Accumulated as `delta += delta_t * rate / 100` (`llenvironment.cpp:1680`) and added to `cloud_pos_density1.xy` **with x negated** (`llsettingsvo.cpp:775-776`, SL-13084). Legacy Windlight stores this **offset by +10** and pairs it with `enable_cloud_scroll` (`llsettingssky.cpp:1024-1036`). |
| `cloud_color` | max (1,1,1) | Cloud tint, split into a sun-facing and an ambient term. |
| `cloud_id` | UUID | Cloud noise texture. |

### Advanced atmospherics (post-Windlight)

`rayleigh_config`, `mie_config`, `absorption_config` (arrays of density-profile
layers), `planet_radius` / `sky_bottom_radius` / `sky_top_radius` (1000…32768),
`dome_offset` (0…1), `dome_radius` (1000…2000), `moisture_level` (0…1),
`droplet_radius` (5…1000), `ice_level` (0…1), `reflection_probe_ambiance` (0…10).

These feed the modern per-fragment atmospheric integration and the probe system.
`moisture_level`/`droplet_radius`/`ice_level` drive rainbow and halo rendering.

## The water parameters

25 key constants, but only **12 distinct settings** — EEP accepts each under both
its modern snake_case name and its legacy camelCase name (`water_fog_color` /
`waterFogColor`, `normal_scale` / `normScale`, and so on), so a parser must try
both. Ranges from `llsettingswater.cpp:316-356`.

| Key | Range | Influence |
|---|---|---|
| `water_fog_color` | 0…1 | Underwater fog / deep-water colour. |
| `water_fog_density` | 0.001…100 | Underwater fog exponent. |
| `underwater_fog_mod` | 0…20 | Multiplier applied while the camera is submerged. |
| `fresnel_offset` | 0…1 | Head-on reflectivity. |
| `fresnel_scale` | 0…1 | How reflectivity grows toward grazing angles. |
| `blur_multiplier` | **−0.5…0.5** | Reflection blur. Note it is **signed** — treating it as an unsigned roughness is wrong at the negative end. |
| `normal_scale` | 0…10 per axis | Normal-map tiling. Its default varies with day-cycle position in the viewer (offset by `position * 0.5 - 0.25`). |
| `scale_above` / `scale_below` | 0…3 | Refraction scale above / below the surface. |
| `wave1_direction`, `wave2_direction` | −20…20 | The two wave layer directions. |
| `normal_map` | UUID | Wave normal map. Default `822ded49-…`, WRAP addressing. |
| `transparent_texture` | UUID | Opaque/transparent water surface texture. |

## Where the viewer actually consumes all this

Two paths, and the distinction is the whole point of this ADR.

**`LLSettingsVOSky::applyToUniforms` (`llsettingsvo.cpp:725-747`)** publishes the
raw settings as shader uniforms: `ambient`, `blue_density`, `blue_horizon`,
`haze_density`, `haze_horizon`, `density_multiplier`, `distance_multiplier`,
`cloud_pos_density2`, `cloud_scale`, `cloud_shadow`, `cloud_variance`, `glow`,
`max_y`, `moon_brightness`, `moisture_level`, `droplet_radius`, `ice_level`,
`reflection_probe_ambiance`.

**`applySpecial` (`:749+`)** adds `lightnorm`, `cloud_pos_density1` with the scroll
delta folded in, `sunlight_color`, `moonlight_color`, `cloud_color`,
`sun_up_factor`, `sun_moon_glow_factor`, `gamma`, `sky_sunlight_scale`,
`sky_ambient_scale`, `classic_mode`.

Note what `AMBIENT` is set from: `getAmbientColor()` — **the raw current-frame
setting**, day or night. `calculateLightSettings` even records it plainly as
`mTotalAmbient = ambient`.

Everything downstream happens **per fragment**, in
`calcAtmosphericVars` / `calcAtmosphericVarsLinear`
(`atmosphericsFuncs.glsl:52` and `:147`).

## The lighting seam — the finding that motivates this ADR

`LLSettingsSky::calculateLightSettings` (`llsettingssky.cpp:1706`) computes
`mSunDiffuse`, `mSunAmbient`, `mMoonDiffuse`, `mMoonAmbient`, `mHazeColor`. It is
the obvious thing to port: it is CPU-side, it is self-contained, and it produces
exactly the four values a conventional engine wants.

**It is dead code.** Searched across the viewer: `getSunDiffuse()`,
`getMoonDiffuse()`, `getLightDiffuse()` and `getMoonAmbient()` have **no caller
anywhere in `indra/newview`**. (`getSunDiffuse` appears once in `llvosky.cpp`, the
pre-EEP sky object.) The modern renderer lights scenes exclusively through the
shader path above.

We ported `calculateLightSettings` faithfully — verified by hand-recomputing two
live samples to three decimals — and then built `EnvironmentDriver`'s
`DirectionalLight3D` and Godot ambient on its output. Which means **our scene
lighting is built on a function the viewer no longer uses for lighting.**

Measured consequence, same code and constants, two regions:

| Sample | Body | Raw `sunlight_color` | Optical depth | Derived diffuse |
|---|---|---|---|---|
| Howletts, sun at +5.4° | sun | 2.37 | 0.349 × 10.6 | (0.63, 0.20, 0.04) — correct-looking sunset |
| Dangazi, midnight | moon | 2.36 | 0.101 × 1.03 | (2.06, 1.98, 1.86) — 2× a daylight sun |

Dangazi's night keyframe carries a bright `sunlight_color` with the sun at −88.2°
and the moon at +73.0°, through a nearly clear atmosphere. Since the moon is up,
the viewer's own `getLightDirection()` selects it and its own formula also lands
near 2.2 — yet Firestorm renders that moment with dark, evenly lit ground. The
resolution is that the formula's output is never used; night in the real viewer is
effectively ambient-dominant, which is exactly what its screenshot shows.

Tuning a global constant until Dangazi looks right therefore breaks Howletts. That
is the signature of a structural gap, not a calibration error.

The gap is specific: **SL computes lighting per fragment, from the view ray's own
path through the atmosphere. Godot hands us one `DirectionalLight3D` and one
global ambient colour.** No choice of two scalars reproduces a function of view
direction.

## What we currently model

`SkySettings` carries **27** properties, `WaterSettings` **12**. The parser reads
**22 of the ~48 top-level sky keys** and all 12 water settings (both spellings).

Faithfully ported and in use: the haze model, `glow`, `cloud_*` (including the
scroll accumulation and the `cloud_shadow`-derived cover), `star_brightness` with
its /500 conversion, `sun_rotation`/`moon_rotation`, `sun_scale`/`moon_scale`,
`sunlight_color`, the sun/moon/cloud/water textures, and the water set.

Parsed but unused: `gamma` (no tonemapping hook yet), `moon_brightness` (only fed
the dead `MoonDiffuse` path).

Not modelled at all: `rayleigh_config`, `mie_config`, `absorption_config`,
`planet_radius`, `sky_bottom_radius`, `sky_top_radius`, `dome_offset`,
`dome_radius`, `moisture_level`, `droplet_radius`, `ice_level`,
`reflection_probe_ambiance`, `sun_arc_radians`, `bloom_id`, `rainbow_id`,
`halo_id`, `enable_cloud_scroll`, water's `transparent_texture`.

Known divergence, not yet fixed: legacy Windlight's `cloud_scroll_rate` needs the
−10 offset and the `enable_cloud_scroll` per-axis gate. Our parser reads the value
raw for both protocols, so a legacy region's clouds would race. The legacy path
has never been exercised against a real grid.

## Decision

1. **Treat `calculateLightSettings` as a reference, not as our lighting model.**
   `SkyLighting` stays — it is a correct port and its `HazeColor` output is still
   used — but `SunDiffuse`/`MoonDiffuse`/`MoonAmbient` are documented as viewer
   dead code and must not be treated as authoritative for scene lighting.
   `MoonAmbient` in particular is a trap: applying it produced an unusably dark
   night, twice.

2. **Scene lighting parity is Phase E work and blocked on `FEAT-RENDER-01`.**
   Reproducing `calcAtmosphericVarsLinear` needs the per-fragment shader seam ADR
   0002 describes. Until it exists, the `DirectionalLight3D` + global-ambient
   mapping is an approximation, and any constant in it is **our** calibration.

3. **Calibration constants are named and justified in place.** `MoonLightScale`
   in `EnvironmentDriver` is the current example: it carries the full measurement
   in its doc comment precisely because no viewer source line backs it.

4. **Verify against consumption, not just definition.** A value existing in
   `llsettingssky.cpp` does not mean the viewer uses it. Before porting a derived
   quantity, check it has a caller in `indra/newview`. Two bugs this session came
   from skipping that check.

5. **Do not model the advanced atmospherics set yet.** `rayleigh_config` and
   friends only pay off once the shader seam can integrate them; parsing them
   earlier adds surface area with no visible effect.

6. **Fix the legacy `cloud_scroll_rate` offset with unit tests**, not live
   testing — the legacy path cannot be exercised on our test grid, but its LLSD
   shape is known and hand-writable.

## Consequences

- The remaining day/night lighting mismatch is expected and bounded. It is not a
  bug to be chased with further constant tuning.
- `docs/specs/FEAT-ENV-01-windlight-eep.md` Phase E gains a concrete definition:
  port `calcAtmosphericVars`/`calcAtmosphericVarsLinear` onto the shader family,
  and retire the CPU light mapping at that point.
- Anyone comparing against Firestorm can use the LL viewer source
  interchangeably for EEP, and should check OpenSim's `bin/assets` before
  concluding a Linden default asset is unavailable.
