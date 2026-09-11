// BUG-RENDER-16 variant: alpha-blended prim face that ALSO writes depth for every fragment,
// back-face culled. For dense high-frequency foliage (thin grass, wispy plants).
//
// The other four treatments each fail this content in one way:
//   prim_scissor / prim_scissor_edge  no flicker, but a 4x-MSAA coverage edge still reads hard.
//   prim_hash                         no flicker, but the dither reads as crunchy speckle (no TAA).
//   prim_blend                        soft edge, but NO depth write -> Godot's per-object sort
//                                     swaps overlapping blades as the camera moves: the flicker.
//   prim_blend_prepass                depth_prepass_alpha's threshold is ~opaque, so mid-alpha
//                                     grass still sorts and still flickers.
//
// prim_blend_depth uses depth_draw_always: every fragment writes depth, so a nearer blade
// depth-rejects the ones behind it and the draw order stops mattering -- no flicker. Where two
// blades overlap you see the front one rather than a blend of the two (effectively opaque there),
// but the OUTER silhouette -- the part that reads as "soft" -- still blends against whatever is
// behind the whole clump (sky, fence, terrain). For grass that trade is close to invisible.
//
// Keep the body in lockstep with prim_blend.gdshader.
shader_type spatial;
render_mode blend_mix, depth_draw_always, cull_back, diffuse_burley, specular_schlick_ggx;

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
    float alpha = slng_shade(UV, v_view_position, NORMAL, (INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).y, albedo, metallic, roughness, specular, normal_map, normal_depth, emission);

    ALBEDO = albedo;
    METALLIC = metallic;
    ROUGHNESS = roughness;
    SPECULAR = specular;
    NORMAL_MAP = normal_map;
    NORMAL_MAP_DEPTH = normal_depth;
    EMISSION = emission;
    ALPHA = alpha;
}
