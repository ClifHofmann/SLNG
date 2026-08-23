// FEAT-RENDER-01 variant: fully opaque face on a HUD attachment.
//
// The third surface in the family. Two render_modes separate it from the avatar variant, and
// both are compile-time, which is why it is a file rather than a uniform:
//
//   cull_disabled -- HUD geometry is worn geometry; same reasoning as the avatar variants.
//   unshaded      -- the HUD SubViewport has its OWN World3D and no lights in it, so a shaded
//                    material renders black. The real viewer draws HUDs flat and
//                    environment-independent too, so this is parity and not a workaround.
//
// The unshaded mode is also what must keep HUDs OUT of FEAT-RENDER-01 Phase 5: atmospherics on
// a HUD would fog an overlay by its distance from the camera, which is meaningless. An unshaded
// shader cannot receive them, so the seam is closed by construction rather than by remembering.
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
    slng_shade(UV, v_view_position, albedo, metallic, roughness, specular, normal_map, normal_depth, emission);

    ALBEDO = albedo;
    METALLIC = metallic;
    ROUGHNESS = roughness;
    SPECULAR = specular;
    NORMAL_MAP = normal_map;
    NORMAL_MAP_DEPTH = normal_depth;
    EMISSION = emission;
}
