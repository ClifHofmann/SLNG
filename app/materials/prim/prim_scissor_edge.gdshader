// BUG-RENDER-16 variant: alpha-masked prim face with ALPHA-EDGE-BLEND antialiasing, back-face
// culled. For dense high-frequency foliage (thin grass, wispy plants).
//
// prim_scissor keeps foliage in the opaque, depth-writing pass -- no transparent sort, so it
// never flickers under camera motion. Its weakness is the EDGE: a binary test at a fixed
// threshold turns a soft blade tip into a sawtooth, and plain alpha_to_coverage only has MSAA's
// ~4 coverage steps to hide it, which on a whole screen of grass reads as blocky.
//
// This variant adds ALPHA_ANTIALIASING_EDGE: Godot computes fractional MSAA coverage from a
// SMOOTH ramp of the alpha value across the edge band (using the screen-space derivative of the
// alpha texel coordinate), instead of a hard step. Same pass, same depth write, same no-sort --
// so still flicker-free -- but the silhouette resolves as a soft gradient rather than a
// staircase, and with no dither noise (unlike prim_hash). This is StandardMaterial3D's
// "Alpha Antialiasing = Alpha Edge Blend" mode.
//
// alpha_scissor_threshold still sets where the cutout sits; alpha_edge widens or narrows the
// softened band around it. Keep the body in lockstep with prim_scissor.gdshader.
shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_back, diffuse_burley, specular_schlick_ggx, alpha_to_coverage;

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
    float alpha = slng_shade(UV, v_view_position, albedo, metallic, roughness, specular, normal_map, normal_depth, emission);

    ALBEDO = albedo;
    METALLIC = metallic;
    ROUGHNESS = roughness;
    SPECULAR = specular;
    NORMAL_MAP = normal_map;
    NORMAL_MAP_DEPTH = normal_depth;
    EMISSION = emission;
    ALPHA = alpha;
    ALPHA_SCISSOR_THRESHOLD = alpha_scissor_threshold;
    // Texel-space coordinate of the alpha source, so Godot can take its screen-space derivative
    // for the coverage ramp. Falls back to a plain UV scale when no albedo texture is bound.
    ALPHA_TEXTURE_COORDINATE = UV * vec2(has_albedo_texture ? textureSize(albedo_texture, 0) : ivec2(256));
    ALPHA_ANTIALIASING_EDGE = alpha_edge;
}
