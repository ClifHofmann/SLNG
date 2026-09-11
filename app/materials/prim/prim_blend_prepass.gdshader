// BUG-RENDER-16 variant: alpha-blended prim face WITH an alpha depth-prepass, back-face culled.
//
// The fifth transparency treatment, for dense high-frequency foliage (thin grass, wispy plants)
// where none of the other three is right:
//
//   prim_scissor  binary cutout -> depth write, no sort, no pop -- but the soft blade tips are
//                 chopped and the field reads as hard, blocky shapes.
//   prim_hash     stochastic cutout -> depth write, no sort, no pop -- but the gradient resolves
//                 as a per-pixel dither that, with no TAA, reads as crunchy over-sharp speckle.
//   prim_blend    true blending -> a clean soft edge -- but NO depth write, so Godot's per-object
//                 AABB-centre transparent sort (far coarser than the viewer's per-draw sort)
//                 swaps the draw order of overlapping foliage as the camera orbits: the flicker.
//   prim_blend_prepass  blend for COLOUR, plus depth_prepass_alpha: an alpha-tested depth
//                 prepass runs first, so overlapping foliage self-occludes by real depth and the
//                 sort order stops mattering for it, while the colour pass still blends the edge
//                 softly. This is StandardMaterial3D's "Depth Pre-Pass" transparency mode.
//
// Cost: the geometry is drawn twice (depth prepass + colour). Acceptable for the grass subset
// this is opt-in for; not a default.
//
// Keep the body in lockstep with prim_blend.gdshader -- same varyings, same slng_shade call.
shader_type spatial;
render_mode blend_mix, depth_prepass_alpha, cull_back, diffuse_burley, specular_schlick_ggx;

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
    float alpha = slng_shade(UV, v_view_position, NORMAL, (INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).y, albedo, metallic, roughness, specular, normal_map, normal_depth, emission);

    ALBEDO = albedo;
    METALLIC = metallic;
    ROUGHNESS = roughness;
    SPECULAR = specular;
    NORMAL_MAP = normal_map;
    NORMAL_MAP_DEPTH = normal_depth;
    EMISSION = emission;
    ALPHA = alpha;
}
