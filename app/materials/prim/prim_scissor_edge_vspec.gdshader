// prim_scissor_edge_vspec: the viewer-specular twin of prim_scissor_edge.gdshader (BUG-RENDER-41 / BUG-RENDER-44).
//
// Below the `#define SLNG_VIEWER_SPEC` line this is prim_scissor_edge.gdshader verbatim, bar one block at the end of fragment(), so
// that file's comments (alpha treatment, culling, render_mode) apply here unchanged. The define
// switches on, inside prim_common.gdshaderinc, a custom light() that adds the reference viewer's
// own sun highlight and routes its gloss/environment reflection into SPECULAR. Because light()
// replaces Godot's whole direct lighting it cannot be a toggle in the base variant, hence a twin.
//
// ObjectRenderer swaps a face onto this twin ONLY when it carries a legacy specular MAP and the
// lab checkbox is on (PrimShaderFamily.ViewerSpecTwin); every other face keeps the base shader.
// Keep the body in lockstep with prim_scissor_edge.gdshader; SelfTest compares the uniform lists of the pair.
shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_back, diffuse_burley, specular_schlick_ggx, alpha_to_coverage;

// The viewer's legacy-specular lighting (custom light() + gloss/environment reflection).
#define SLNG_VIEWER_SPEC
#include "res://materials/prim/prim_common.gdshaderinc"

uniform float alpha_scissor_threshold : hint_range(0.0, 1.0) = 0.5;
uniform float alpha_edge : hint_range(0.0, 1.0) = 0.3;

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
    // Texel-space coordinate of the alpha source, so Godot can take its screen-space derivative
    // for the coverage ramp. Falls back to a plain UV scale when no albedo texture is bound.
    ALPHA_TEXTURE_COORDINATE = UV * vec2(has_albedo_texture ? textureSize(albedo_texture, 0) : ivec2(256));
    ALPHA_ANTIALIASING_EDGE = alpha_edge;
}
