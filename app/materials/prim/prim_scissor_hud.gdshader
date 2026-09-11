// FEAT-RENDER-01 variant: alpha-masked face on a HUD attachment.
//
// See prim_opaque_hud.gdshader for why HUD surfaces need their own compile-time variants.
// alpha_to_coverage carries over from the other scissor variants for the same reason.
shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_opaque, cull_back, diffuse_burley, specular_schlick_ggx, alpha_to_coverage;

#include "res://materials/prim/prim_common.gdshaderinc"

uniform float alpha_scissor_threshold : hint_range(0.0, 1.0) = 0.5;

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
    float alpha = slng_shade(UV, v_view_position, albedo, metallic, roughness, specular, normal_map, normal_depth, emission);

    // albedo + emission -- see prim_opaque_hud.gdshader for why `unshaded` makes this mandatory
    // rather than cosmetic (it drops EMISSION, and SL fullbright routes the colour through it).
    ALBEDO = albedo + emission;
    // No METALLIC/ROUGHNESS/SPECULAR/NORMAL_MAP: `unshaded` ignores every one of them, and
    // writing NORMAL_MAP in particular is not free to leave in. Godot infers "this mesh needs
    // tangents" from the mere presence of that write and logs a warning per mesh that has none
    // -- which HUD attachment meshes do not (12 lines in one session, FEAT-RENDER-01 Phase 3).
    EMISSION = emission;
    ALPHA = alpha;
    ALPHA_SCISSOR_THRESHOLD = alpha_scissor_threshold;
}
