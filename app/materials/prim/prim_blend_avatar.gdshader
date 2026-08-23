// FEAT-RENDER-01 variant: alpha-blended face, for AVATAR surfaces (double-sided).
//
// The two-sided twin of prim_blend; see prim_opaque_avatar.gdshader for why the cull mode
// has to be its own compile-time variant instead of a uniform.
//
// Reached by graded-alpha avatar content -- hair cards above all, where both faces of a strand
// card genuinely need to draw. prim_blend's note that the viewer back-face culls blended PRIM
// faces is still true; it just does not describe worn avatar geometry, which arrives already
// authored as single-sided cards.
//
// Keep the body in lockstep with prim_blend.gdshader.
shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_disabled, diffuse_burley, specular_schlick_ggx;

#include "res://materials/prim/prim_common.gdshaderinc"

varying vec3 v_view_position;

void vertex() {
    // The normal-map coordinate is derived from the SAME base UV, so it must be computed before
    // UV is overwritten by the diffuse placement.
    v_normal_uv = slng_normal_uv(UV, VERTEX, NORMAL);
    v_specular_uv = slng_specular_uv(UV, VERTEX, NORMAL);
    UV = slng_transform_uv(UV, VERTEX, NORMAL);
    v_view_position = (MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
    vec3 albedo;
    float metallic;
    float roughness;
    float specular;
    vec3 normal_map;
    float normal_depth;
    vec3 emission;
    float alpha = slng_shade(UV, v_view_position, albedo, metallic, roughness, specular, normal_map, normal_depth, emission);

    ALBEDO = albedo;
    METALLIC = metallic;
    ROUGHNESS = roughness;
    SPECULAR = specular;
    NORMAL_MAP = normal_map;
    NORMAL_MAP_DEPTH = normal_depth;
    EMISSION = emission;
    ALPHA = alpha;
}
