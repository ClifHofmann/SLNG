// FEAT-RENDER-01 variant: fully opaque face, for AVATAR surfaces (double-sided).
//
// The two-sided twin of prim_opaque. Godot's cull mode is `render_mode`, i.e. COMPILE-TIME --
// the same reason this family has a variant per transparency mode rather than a uniform. So
// double-sidedness is a second compile-time axis, not a parameter.
//
// It exists for AvatarRenderer (FEAT-RENDER-01 Phase 3). Avatar and worn-attachment faces were
// on `CullMode.Disabled` under StandardMaterial3D, and the migration is required to be
// visually identical, so the cull mode comes across unchanged rather than being "fixed" on the
// way. That is NOT a licence to point world prims at these: prim_opaque's comment about
// double-sided prims looking like glassy shells still stands, and this file does not repeal it.
//
// Keep the body in lockstep with prim_opaque.gdshader. SelfTest compares the uniform counts of
// each twin pair, so a uniform added to only one of them fails the smoke test.
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
    slng_shade(UV, v_view_position, albedo, metallic, roughness, specular, normal_map, normal_depth, emission);

    ALBEDO = albedo;
    METALLIC = metallic;
    ROUGHNESS = roughness;
    SPECULAR = specular;
    NORMAL_MAP = normal_map;
    NORMAL_MAP_DEPTH = normal_depth;
    EMISSION = emission;
}
