// FEAT-RENDER-01 variant: hashed alpha cutout, for AVATAR surfaces (double-sided).
//
// The two-sided twin of prim_hash, and the reason that shader exists at all (BUG-RENDER-09).
// A Bakes-on-Mesh bake carries the wearer's alpha-layer wearable as a SOFT gradient in its alpha
// channel. Both of the existing routes render it wrong:
//
//   * prim_scissor_avatar (the current behaviour, threshold 0.25/0.5) turns the gradient into a
//     binary edge -- the reported "torn / sawtooth skin above the alpha" on a mesh-body foot.
//   * prim_blend_avatar (tried in v0.20.41, reverted in v0.20.46) fades correctly but drops depth
//     write, and an avatar face is cull_disabled with many overlapping layers, so the head's own
//     faces sorted against each other into see-through chunks.
//
// Hashed alpha keeps prim_scissor's depth behaviour (so nothing can sort wrong) while resolving
// the gradient as a dither rather than a step. See prim_hash.gdshader for the full rationale and
// for why alpha_to_coverage must not be added here.
//
// Keep the body in lockstep with prim_hash.gdshader.
shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_disabled, diffuse_burley, specular_schlick_ggx;

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
    float alpha = slng_shade(UV, v_view_position, NORMAL, (INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).y, albedo, metallic, roughness, specular, normal_map, normal_depth, emission);

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
