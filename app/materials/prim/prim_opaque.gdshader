// FEAT-RENDER-01 variant: fully opaque prim face, back-face culled.
//
// Replaces StandardMaterial3D with Transparency.Disabled. Deliberately never writes ALPHA:
// in Godot, writing ALPHA at all moves the material toward the transparent treatment, and an
// ordinary opaque prim must stay in the opaque pass.
//
// cull_back matches the real viewer's global default (llrender.cpp:863, glCullFace(GL_BACK)).
// Do not "fix" this to cull_disabled -- everything rendering double-sided is precisely the bug
// that made solid objects look like glassy shells (fixed 2026-08-01, see ADR 0002's parity list).
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
    slng_shade(UV, v_view_position, albedo, metallic, roughness, normal_map, normal_depth, emission);

    ALBEDO = albedo;
    METALLIC = metallic;
    ROUGHNESS = roughness;
    NORMAL_MAP = normal_map;
    NORMAL_MAP_DEPTH = normal_depth;
    EMISSION = emission;
}
