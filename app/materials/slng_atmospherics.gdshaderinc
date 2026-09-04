// Atmospherics seam for SLNG's world surfaces (FEAT-RENDER-01, ADR 0002, ADR 0003).
//
// Shared by prims, avatars, terrain, and water.
// Windlight / EEP atmospheric scattering is applied here per-fragment in Phase 5.
//
// Ported from the reference viewer's calcAtmosphericVars() + atmosFragLighting(),
// indra/newview/app_settings/shaders/class1/windlight/{atmosphericsFuncs,atmosphericsF}.glsl
// (vendored at scratch/slviewer). Line numbers below refer to those files.
//
// THE STRUCTURE IS THE POINT. The viewer computes TWO terms and composites
//
//     color * atten.r + additive          (atmosphericsF.glsl:37-40)
//
// -- an extinction that darkens, and IN-SCATTERED LIGHT that is added back. Mixing the fragment
// toward a single haze colour, which is what this file did before, reproduces the first half only,
// and the second half is the one that matters for a horizon: `additive` is built from the same
// blue_horizon / haze_horizon / sunlight / ambient inputs that sky.gdshader draws the sky itself
// from, which is what makes distant water and the sky meet instead of showing a seam. Live A/B
// against Firestorm, 2026-09-04: SLNG's lighthouse was washed out at middle distance while the
// horizon stayed a hard line -- too much extinction, no in-scatter -- where Firestorm keeps the
// island crisp and dissolves the far distance into haze.
//
// Note `atten.r` -- the viewer attenuates with the RED channel alone, and the blue of distance
// comes from `additive`, not from per-channel extinction. Red is also the least-attenuated channel
// (blue_density is largest in blue), so this washes middle distance out far less than a per-channel
// mix does.
//
// STILL MISSING: the directional part of haze_glow (atmosphericsFuncs.glsl:88-106), which brightens
// the haze toward the sun and is what gives an SL sunset its glow. It needs the angle between the
// view ray and the sun, and those two are in different coordinate spaces here -- slng_sun_direction
// is Godot WORLD space (EnvironmentDriver.cs:501) while view_position is VIEW space, and Godot
// built-ins like VIEW_MATRIX are only in scope inside fragment() itself, not in a function it
// calls. Fixing that needs either a per-frame view-space sun global or a signature change through
// every shader; see the FEAT-RENDER-08 spec. Until then haze_glow sits at the viewer's own
// non-directional floor (the "minimum anti-solar illumination" constant), so the haze is the right
// COLOUR everywhere but does not yet brighten toward the sun.

global uniform vec4 slng_blue_density;
global uniform vec4 slng_blue_horizon;
global uniform float slng_haze_density;
global uniform float slng_haze_horizon;
global uniform float slng_density_multiplier;
global uniform float slng_distance_multiplier;
global uniform float slng_max_y;
global uniform vec4 slng_sunlight_color;
global uniform vec4 slng_ambient;
global uniform float slng_cloud_shadow;
global uniform float slng_sun_moon_glow_factor;

vec3 slng_apply_atmospherics(vec3 color, vec3 view_position) {
    vec3 rel_pos = view_position;

    // (TERRAIN) limit altitude -- viewer atmosphericsFuncs.glsl:57. Guarded on max_y > 0 as the
    // viewer is not: there it is a validated setting, here a global uniform that a region which
    // never reported one would leave at 0, and an unguarded max_y / rel_pos.y would collapse
    // rel_pos to the origin and switch the haze off entirely.
    if (slng_max_y > 0.0 && abs(rel_pos.y) > slng_max_y) {
        rel_pos *= (slng_max_y / rel_pos.y);
    }

    float rel_pos_len = length(rel_pos);

    // --- extinction (viewer lines 70, 79, 84) ---------------------------------------------------
    vec3 combined_haze = max(slng_blue_density.rgb + vec3(slng_haze_density), vec3(1e-6));
    vec3 blue_weight   = slng_blue_density.rgb / combined_haze;
    vec3 haze_weight   = vec3(slng_haze_density) / combined_haze;

    float density_dist = rel_pos_len * slng_density_multiplier;
    vec3 atten = exp(-combined_haze * density_dist * slng_distance_multiplier);

    // --- in-scatter (viewer lines 104-128) ------------------------------------------------------
    // haze_glow's directional term is not available here (see the header). The viewer adds a
    // constant 0.25 "minimum anti-solar illumination" on top of the directional part; that floor is
    // what remains, scaled by the same glow factor the viewer applies.
    float haze_glow = 0.25 * slng_sun_moon_glow_factor;

    vec3 amb_color   = slng_ambient.rgb;
    vec3 tmp_ambient = amb_color + (vec3(1.0) - amb_color) * slng_cloud_shadow * 0.5;
    vec3 cs          = slng_sunlight_color.rgb * (1.0 - slng_cloud_shadow);

    vec3 additive = (slng_blue_horizon.rgb * blue_weight) * (cs + tmp_ambient)
                  + (vec3(slng_haze_horizon) * haze_weight) * (cs * haze_glow + tmp_ambient);

    // Grows with distance: zero where nothing has been scattered out, full where everything has.
    additive *= (vec3(1.0) - atten);
    additive = min(additive, vec3(10.0));

    // Composite. Deliberately WITHOUT the viewer's srgb_to_linear(additive*2.0) * sky_hdr_scale:
    // that belongs to its HDR pipeline, and SLNG's skies are display-referred and never tonemapped.
    return color * atten.r + additive;
}
