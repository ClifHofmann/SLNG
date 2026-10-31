// prim_scissor_avatar_vspec: the viewer-specular twin of prim_scissor_avatar.gdshader, for a WORN face (BUG-AVATAR-17).
//
// Below the `#define SLNG_VIEWER_SPEC` line this is prim_scissor_avatar.gdshader verbatim, bar one block at the end of
// fragment(): the same twin a world prim gets (prim_scissor_vspec.gdshader). A worn rigged face whose legacy material
// carries a specular map would otherwise be lit by Godot's GGX at roughness = the Blinn-Phong alpha, a lobe ~15x
// narrower and ~550x stronger at its peak than the viewer's (gloss 222: half-width 0.26 deg against 4.0 deg), i.e.
// hard mirror-like pinpoints instead of the viewer's soft satin sheen. See prim_common.gdshaderinc, SLNG_VIEWER_SPEC.
//
// AvatarRenderer swaps a face onto this twin ONLY when it carries a specular map (FinishFaceMaterial); every other
// face keeps the base shader. Keep the body in lockstep with prim_scissor_avatar.gdshader.
shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_disabled, diffuse_burley, specular_schlick_ggx, alpha_to_coverage;

// The viewer's legacy-specular lighting (custom light() + gloss/environment reflection).
#define SLNG_VIEWER_SPEC
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
    vec3 normal_map;
    float normal_depth;
    vec3 emission;
    float alpha = slng_shade(UV, v_view_position, NORMAL, albedo, metallic, roughness, specular, normal_map, normal_depth, emission);

    ALBEDO = albedo;
    METALLIC = metallic;
    ROUGHNESS = roughness;
    SPECULAR = specular;
    NORMAL_MAP = normal_map;
    NORMAL_MAP_DEPTH = normal_depth;
    EMISSION = emission;

    // Twin only: what light() reads back (a varying can be assigned in fragment() only).
    vec4 vspec = slng_viewer_spec_capture(UV);
    v_vspec_rgb = vspec.rgb;
    v_vspec_gloss = vspec.a;
    ALPHA = alpha;
    ALPHA_SCISSOR_THRESHOLD = alpha_scissor_threshold;
}
