// FEAT-RENDER-01 variant: alpha-blended prim face (glass, soft edges, translucent tints),
// back-face culled.
//
// Replaces StandardMaterial3D with Transparency.Alpha. Still cull_back: the real viewer
// back-face culls alpha-blended prim faces exactly like opaque ones -- lldrawpoolalpha.cpp
// lifts culling only for particles and explicitly double-sided GLTF materials.
shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_back, diffuse_burley, specular_schlick_ggx;

#include "res://materials/prim/prim_common.gdshaderinc"

varying vec3 v_view_position;

void vertex() {
    UV = slng_transform_uv(UV);
    v_view_position = (MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
    vec3 albedo;
    float metallic;
    float roughness;
    vec3 normal_map;
    float normal_depth;
    vec3 emission;
    float alpha = slng_shade(UV, v_view_position, albedo, metallic, roughness, normal_map, normal_depth, emission);

    ALBEDO = albedo;
    METALLIC = metallic;
    ROUGHNESS = roughness;
    NORMAL_MAP = normal_map;
    NORMAL_MAP_DEPTH = normal_depth;
    EMISSION = emission;
    ALPHA = alpha;
}
