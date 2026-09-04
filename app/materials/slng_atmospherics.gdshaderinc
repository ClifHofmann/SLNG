// Atmospherics seam for SLNG's world surfaces (FEAT-RENDER-01, ADR 0002, ADR 0003).
//
// Shared by prims, avatars, terrain, and water.
// Windlight / EEP atmospheric scattering is applied here per-fragment in Phase 5.
//
// Ported from the reference viewer's calcAtmosphericVars(),
// indra/newview/app_settings/shaders/class1/windlight/atmosphericsFuncs.glsl (vendored at
// scratch/slviewer). Line numbers below refer to that file. Two properties of the original are
// deliberately reproduced rather than simplified, because both are visible:
//
//   * the extinction term is a vec3, not a scalar. SL's distance haze is wavelength-dependent --
//     that is what makes far-away geometry go BLUE rather than merely grey, and it is most of what
//     makes an SL vista read as an SL vista.
//   * altitude is clamped before the distance is taken, so looking up or flying high does not
//     accumulate haze the atmosphere does not have.
//
// Still an approximation in one respect, and the remaining gap is the interesting one: the viewer
// computes extinction (`atten`) AND in-scattered light (`additive`, including the glow that builds
// up around the sun) and composites `color * atten + additive`. Mixing toward haze_color reproduces
// the extinction half only, so sunsets lose their directional glow. Closing that needs
// blue_horizon / haze_horizon / sunlight / ambient / glow / lightnorm as well -- see the
// FEAT-RENDER-08 spec.

global uniform vec4 slng_blue_density;
global uniform float slng_haze_density;
global uniform float slng_density_multiplier;
global uniform float slng_distance_multiplier;
global uniform float slng_max_y;
global uniform vec4 slng_haze_color;

vec3 slng_apply_atmospherics(vec3 color, vec3 view_position) {
    vec3 rel_pos = view_position;

    // (TERRAIN) limit altitude -- viewer line 57. Guarded on max_y > 0 as the viewer is not: there
    // it is a validated setting, here it is a global uniform that a region which never reported one
    // would leave at 0, and an unguarded `max_y / rel_pos.y` would then collapse rel_pos to the
    // origin and switch the haze off entirely.
    if (slng_max_y > 0.0 && abs(rel_pos.y) > slng_max_y) {
        rel_pos *= (slng_max_y / rel_pos.y);
    }

    float rel_pos_len = length(rel_pos);

    // viewer lines 70, 79, 84. blue_density and haze_density are summed BEFORE the exponential --
    // scaling by haze_density alone (as the first version here did) is both colourless and weaker
    // than the atmosphere the region actually describes.
    vec3 combined_haze = max(slng_blue_density.rgb + vec3(slng_haze_density), vec3(1e-6));
    float density_dist = rel_pos_len * slng_density_multiplier;
    vec3 atten = exp(-combined_haze * density_dist * slng_distance_multiplier);

    // Component-wise mix: each channel is attenuated by its own extinction, which is what turns
    // distance blue instead of grey.
    return mix(slng_haze_color.rgb, color, clamp(atten, vec3(0.0), vec3(1.0)));
}
