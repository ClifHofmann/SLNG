// FEAT-RENDER-01 variant: alpha-masked face, for AVATAR surfaces (double-sided).
//
// The two-sided twin of prim_scissor; see prim_opaque_avatar.gdshader for why the cull mode
// has to be its own compile-time variant instead of a uniform.
//
// This is the variant the avatar path leans on hardest. Both of AvatarRenderer's alpha routes
// end here: the system bake at threshold 0.5, and the hard-cutout branch of ApplyAlphaCutout at
// 0.25. alpha_to_coverage carries over from the twin for the same reason -- MSAA 4x is on
// project-wide, and it is what keeps a cutout silhouette from stair-stepping.
//
// Keep the body in lockstep with prim_scissor.gdshader.
shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_disabled, diffuse_burley, specular_schlick_ggx, alpha_to_coverage;

#include "res://materials/prim/prim_common.gdshaderinc"

uniform float alpha_scissor_threshold : hint_range(0.0, 1.0) = 0.5;

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
    ALPHA_SCISSOR_THRESHOLD = alpha_scissor_threshold;
}
