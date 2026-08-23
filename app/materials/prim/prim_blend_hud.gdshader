// FEAT-RENDER-01 variant: alpha-blended face on a HUD attachment.
//
// See prim_opaque_hud.gdshader for why HUD surfaces need their own compile-time variants.
// The common case here is a HUD panel with a soft-edged or partly translucent background.
shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_opaque, cull_disabled, diffuse_burley, specular_schlick_ggx;

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
