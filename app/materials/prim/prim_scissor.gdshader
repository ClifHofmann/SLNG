// FEAT-RENDER-01 variant: alpha-masked prim face (fences, foliage), back-face culled.
//
// Replaces StandardMaterial3D with Transparency.AlphaScissor. Writing ALPHA_SCISSOR_THRESHOLD
// keeps the material in the OPAQUE pass and discards below the threshold, which is what makes
// this cheaper than real blending -- the reason ObjectRenderer prefers it for world prims
// (avatar content uses alpha hashing instead; see AvatarRenderer).
//
// alpha_to_coverage mirrors the AlphaAntiAliasing.AlphaToCoverage the StandardMaterial3D path
// set on exactly these faces, and it is NOT decorative: project.godot has
// anti_aliasing/quality/msaa_3d=2 (4x), so this is what keeps cutout edges -- foliage, fences --
// from turning into hard stair-steps. Dropping it would be a visible regression, not a no-op.
// (Older comments in ObjectRenderer claimed MSAA was off project-wide; it isn't.)
// It can be baked into this variant rather than being another compile-time axis because
// ObjectRenderer sets it on every scissor face and never on any other.
shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_back, diffuse_burley, specular_schlick_ggx, alpha_to_coverage;

#include "res://materials/prim/prim_common.gdshaderinc"

uniform float alpha_scissor_threshold : hint_range(0.0, 1.0) = 0.5;

varying vec3 v_view_position;

void vertex() {
    slng_transform_uv(UV);
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
    ALPHA_SCISSOR_THRESHOLD = alpha_scissor_threshold;
}
