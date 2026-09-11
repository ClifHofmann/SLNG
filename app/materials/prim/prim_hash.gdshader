// FEAT-RENDER-01 variant: HASHED alpha cutout, back-face culled (BUG-RENDER-09).
//
// The fourth transparency treatment in the family, between prim_scissor and prim_blend:
//
//   prim_scissor  binary test at a fixed threshold -> depth write, but a soft gradient becomes
//                 a sawtooth edge (only MSAA's ~4 alpha_to_coverage steps soften it).
//   prim_blend    true blending -> a clean fade, but NO depth write, so overlapping faces of the
//                 same surface sort against each other. On an avatar (cull_disabled, many
//                 overlapping layers) that is the "blocky see-through chunks across the face"
//                 regression that reverted v0.20.41.
//   prim_hash     per-pixel STOCHASTIC discard whose probability is the pixel's alpha. Depth is
//                 written exactly like the opaque pass, so nothing can sort wrong, and a gradient
//                 resolves as a dither instead of a step. Temporal/spatial noise is the price;
//                 on a large low-contrast surface (skin under an alpha-layer wearable) it reads
//                 as a soft fade, which is what this variant exists for.
//
// ALPHA_HASH_SCALE is what puts Godot's fragment shader into hashed-alpha mode at all -- writing
// it is the switch, the value only scales the noise. Do NOT also declare alpha_to_coverage here:
// that is prim_scissor's silhouette antialiasing and it would dither an already-dithered result.
//
// Keep the body in lockstep with prim_scissor.gdshader / prim_hash_avatar.gdshader. SelfTest
// compares the uniform names of each twin pair, so a uniform added to only one of them fails the
// smoke test.
shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_back, diffuse_burley, specular_schlick_ggx;

#include "res://materials/prim/prim_common.gdshaderinc"

uniform float alpha_hash_scale : hint_range(0.0, 2.0) = 1.0;

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
    float alpha = slng_shade(UV, v_view_position, NORMAL, albedo, metallic, roughness, specular, normal_map, normal_depth, emission);

    ALBEDO = albedo;
    METALLIC = metallic;
    ROUGHNESS = roughness;
    SPECULAR = specular;
    NORMAL_MAP = normal_map;
    NORMAL_MAP_DEPTH = normal_depth;
    EMISSION = emission;
    ALPHA = alpha;
    ALPHA_HASH_SCALE = alpha_hash_scale;
}
