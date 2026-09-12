// FEAT-RENDER-0X variant: fully opaque prim face, but NOT back-face culled.
//
// Everything else is prim_opaque.gdshader verbatim -- see that file for why cull_back is the
// default there and must stay the default for ordinary opaque prims. This variant exists ONLY
// for the one case the real viewer also makes an exception for: a face whose GLTF material
// explicitly declares mDoubleSided (lldrawpool.cpp:839, :856). ObjectRenderer must route a face
// here ONLY when PbrMaterialData.DoubleSided is true -- never as a default, never as a guess.
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
    slng_shade(UV, v_view_position, NORMAL, albedo, metallic, roughness, specular, normal_map, normal_depth, emission);

    ALBEDO = albedo;
    METALLIC = metallic;
    ROUGHNESS = roughness;
    SPECULAR = specular;
    NORMAL_MAP = normal_map;
    NORMAL_MAP_DEPTH = normal_depth;
    EMISSION = emission;
}
