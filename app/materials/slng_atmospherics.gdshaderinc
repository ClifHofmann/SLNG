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
// The additive is converted from sRGB to linear before it is added -- see the composite at the
// bottom. That is a transfer function, not an exposure knob, and getting it wrong is invisible to
// any amount of density tuning.
//
// The directional haze glow IS implemented, and it had to be: it is not a sunset refinement but the
// dominant term wherever haze_density is high (measured live: haze_weight 0.96, so the in-scatter is
// almost entirely the haze branch). It needs the angle between the view ray and the sun in one
// coordinate space; AvatarController publishes slng_sun_direction_view per frame for exactly that,
// because Godot's VIEW_MATRIX is only in scope inside fragment() and not in a function it calls.

// The viewer's srgbF.glsl transfer, under an slng_ prefix because terrain.gdshader includes both
// this file and sl_terrain_composition.gdshaderinc, which defines its own sl_srgb_to_linear.
vec3 slng_srgb_to_linear(vec3 cs) {
    vec3 low_range  = cs / vec3(12.92);
    vec3 high_range = pow((cs + vec3(0.055)) / vec3(1.055), vec3(2.4));
    return mix(high_range, low_range, lessThanEqual(cs, vec3(0.04045)));
}

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
global uniform vec4 slng_glow;
global uniform vec3 slng_sun_direction_view;

// out_additive is the IN-SCATTERED light, handed back separately so the caller can route it into
// EMISSION rather than ALBEDO. That distinction is the viewer's, not a Godot detail:
// atmosFragLighting does `light += additive` on the FINISHED, already-lit pixel. Folding it into
// albedo instead makes Godot multiply the haze by the scene lighting -- so at dusk, when the sun is
// dim, the haze is dimmed with it and distance goes dark instead of bright. Water shows this worst,
// because at range its albedo is nearly all in-scatter and it has no other bright term to hide it:
// live, 2026-09-04, the sea stayed a flat brown out to the horizon while the sky above it glowed.
vec3 slng_apply_atmospherics(vec3 color, vec3 view_position, out vec3 out_additive) {
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

    // --- haze glow (viewer lines 88-106) --------------------------------------------------------
    // Pinning this at its 0.25 floor, as the first version of this file did, is not a small
    // simplification -- for a region with a high haze_density it deletes the dominant term. Live,
    // 2026-09-04: haze_density 4 against blue_density.r 0.16 gives haze_weight 0.96, so the additive
    // is almost entirely the haze branch, and with the branch pinned low `color * atten.r + additive`
    // collapses to a dark smear. That is why the lighthouse came out far darker than Firestorm's and
    // the sea went brown: extinction was removing 72 % of the colour at 176 m and nothing was being
    // added back.
    //
    // rel_pos_norm points from the eye toward the fragment; slng_sun_direction_view is the sun in
    // the SAME space, published per frame by AvatarController (see its doc comment for why the
    // transform cannot happen in a shader function).
    vec3 rel_pos_norm = normalize(rel_pos);
    float haze_glow = dot(rel_pos_norm, slng_sun_direction_view);
    haze_glow *= max(0.0, dot(slng_sun_direction_view, rel_pos_norm));
    haze_glow = 1.0 - haze_glow;
    // A minimum angle: a smaller value allows a tighter, brighter hotspot.
    haze_glow = max(haze_glow, 0.001);
    haze_glow *= slng_glow.x;
    // glow.z is negative, so this is a sort of 1/angle falloff.
    haze_glow = clamp(pow(haze_glow, slng_glow.z), -100000.0, 100000.0);
    // "minimum anti-solar illumination" -- the floor that used to be the whole term.
    haze_glow += 0.25;
    haze_glow *= slng_sun_moon_glow_factor;

    // --- in-scatter (viewer lines 108-128) ------------------------------------------------------

    vec3 amb_color   = slng_ambient.rgb;
    vec3 tmp_ambient = amb_color + (vec3(1.0) - amb_color) * slng_cloud_shadow * 0.5;
    vec3 cs          = slng_sunlight_color.rgb * (1.0 - slng_cloud_shadow);

    vec3 additive = (slng_blue_horizon.rgb * blue_weight) * (cs + tmp_ambient)
                  + (vec3(slng_haze_horizon) * haze_weight) * (cs * haze_glow + tmp_ambient);

    // Grows with distance: zero where nothing has been scattered out, full where everything has.
    additive *= (vec3(1.0) - atten);
    additive = min(additive, vec3(10.0));

    // Composite -- viewer atmosphericsF.glsl:37-40, transfer included.
    //
    // v0.20.66 dropped the srgb_to_linear(additive*2.0) as "HDR pipeline we do not have". That was
    // wrong, and not by a little: it is a TRANSFER FUNCTION, not an exposure control, and this
    // shader was the only thing around here not applying it.
    //
    //   * ALBEDO here is LINEAR -- albedo_texture is declared `: source_color`, so Godot converts
    //     sRGB to linear on sample (prim_common.gdshaderinc:13).
    //   * the sky next to it is LINEAR -- sky.gdshader:645 ends on srgb_to_linear(sky_color).
    //   * `additive` is built from Windlight colours (blue_horizon, sunlight_color, ambient),
    //     which arrive from the EEP LLSD display-referred, i.e. sRGB.
    //
    // Adding sRGB values to a linear frame put the haze on a different curve from the sky it is
    // supposed to blend into -- which is exactly why a horizon cannot be made to match by tuning
    // density, and why the far distance stayed too dark to merge. The curve also cuts both ways in
    // the right directions: it dims a small additive (less wash at middle distance, where the
    // lighthouse was) and lifts a large one (more haze far out, where sea meets sky).
    //
    // sky_hdr_scale is still omitted -- that one really is the viewer's HDR exposure, and it is
    // 1.0 outside it.
    out_additive = slng_srgb_to_linear(clamp(additive * 2.0, vec3(0.0), vec3(1.0)));

    // Extinction only. The caller adds out_additive through EMISSION so the lighting cannot scale
    // it -- see this function's own doc comment.
    return color * atten.r;
}
