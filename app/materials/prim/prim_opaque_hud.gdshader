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
render_mode unshaded, blend_mix, depth_draw_opaque, cull_back, diffuse_burley, specular_schlick_ggx;

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
    vec3 normal_map;      // filled by slng_shade; unused here, see below
    float normal_depth;
    vec3 emission;
    slng_shade(UV, v_view_position, albedo, metallic, roughness, specular, normal_map, normal_depth, emission);

    // albedo + emission, NOT plain albedo: `unshaded` outputs ALBEDO only and drops EMISSION
    // entirely, so anything slng_shade routed through emission has to be folded back in here or
    // it is simply lost. That is not hypothetical -- SL's fullbright flag (FEAT-RENDER-06) does
    // exactly that routing (out_emission += out_albedo; out_albedo = 0), and since HUD faces are
    // almost always fullbright, plain `ALBEDO = albedo` rendered whole worn HUDs as solid black
    // rectangles despite every face being correctly textured. For a non-fullbright face emission
    // is zero (or a genuine glTF emissive map, which an overlay should show anyway), so the sum
    // is correct in both cases.
    ALBEDO = albedo + emission;
    // No METALLIC/ROUGHNESS/SPECULAR/NORMAL_MAP: `unshaded` ignores every one of them, and
    // writing NORMAL_MAP in particular is not free to leave in. Godot infers "this mesh needs
    // tangents" from the mere presence of that write and logs a warning per mesh that has none
    // -- which HUD attachment meshes do not (12 lines in one session, FEAT-RENDER-01 Phase 3).
    // EMISSION is written anyway so the shader stays correct if `unshaded` is ever dropped;
    // as long as it is set, Godot ignores this value.
    EMISSION = emission;
}
