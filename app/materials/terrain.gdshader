shader_type spatial;

// Detail-texture composition matching the Second Life viewer. See the include for the sources and
// for the two viewer-source traps this port had to avoid.
#include "res://materials/sl_terrain_composition.gdshaderinc"
#include "res://materials/slng_atmospherics.gdshaderinc"

// Two deliberate choices here:
//
// NOT source_color -- the samples must stay sRGB-encoded so they can be blended the way the viewer
// blends them; sl_terrain_blend converts the result to linear afterwards.
//
// ANISOTROPIC -- the ground is seen at a grazing angle over most of the screen, and plain
// filter_linear_mipmap picks its mip level from the largest derivative, so it blurs across the
// view direction as well as along it. That reads as a soft, detail-less ground while the real
// viewer stays crisp. Godot's anisotropic_filtering_level in project.godot only applies to
// samplers that ask for anisotropy, which this previously did not.
uniform sampler2D detail0 : filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D detail1 : filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D detail2 : filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D detail3 : filter_linear_mipmap_anisotropic, repeat_enable;

uniform float region_size = 256.0;

// Per-corner region parameters, in RegionHandshake order: SW, SE, NW, NE.
uniform vec4 start_height = vec4(10.0, 10.0, 10.0, 10.0);
uniform vec4 height_range = vec4(60.0, 60.0, 60.0, 60.0);

// SL region-local position in metres: x = east, y = north.
varying vec2 v_local_xy;
varying vec3 v_view_position;

// Composition value and blend-ramp coordinate are computed PER VERTEX and interpolated, not
// evaluated per fragment. That is not an optimization, it is what the viewer does, and the
// difference is visible.
//
// LLVLComposition::generateHeights fills the composition on a 1 m grid on the CPU;
// LLSurfacePatch::eval reads it (and rand_val) per terrain vertex into texcoord1, and the
// rasterizer interpolates from there. Our terrain mesh is the same 1 m grid, one vertex per
// heightmap cell, so evaluating here reproduces that sampling exactly.
//
// Per fragment we instead carried the noise's ~2.5 m and ~4.9 m octaves at full strength, where
// 1 m sampling plus linear interpolation largely averages them away. The extra high-frequency
// content pushed individual spots across a band boundary, showing up as small patches of the next
// detail texture that the real viewer does not have (live A/B, v0.7.44-alpha: a brown patch on
// otherwise unbroken grass).
varying float v_composition;
varying float v_ramp_v;

void vertex() {
    // Godot is Y-up with north along -Z; SL is Z-up with north along +Y.
    vec2 local_xy = vec2(VERTEX.x, -VERTEX.z);
    v_local_xy = local_xy;
    v_view_position = (MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;

    vec2 corner_uv = clamp(local_xy / region_size, vec2(0.0), vec2(1.0));
    float s = sl_bilinear_corners(start_height, corner_uv.x, corner_uv.y);
    float r = sl_bilinear_corners(height_range, corner_uv.x, corner_uv.y);

    // Clamped here, before interpolation, matching generateHeights.
    v_composition = sl_composition_value(local_xy, VERTEX.y, s, r);
    v_ramp_v = sl_ramp_v(local_xy);
}

void fragment() {
    // Detail UVs stay per-fragment: they are linear in position, so interpolating them is exact.
    vec2 detail_uv = sl_detail_uv(v_local_xy);

    vec3 t0 = texture(detail0, detail_uv).rgb;
    vec3 t1 = texture(detail1, detail_uv).rgb;
    vec3 t2 = texture(detail2, detail_uv).rgb;
    vec3 t3 = texture(detail3, detail_uv).rgb;

    vec3 blended = sl_terrain_blend(t0, t1, t2, t3, v_composition, v_ramp_v);
    vec3 atmos_additive;
    ALBEDO = slng_apply_atmospherics(blended, v_view_position, atmos_additive);
    // Post-lighting, so the haze is not scaled by a low sun -- see the seam's doc comment.
    EMISSION = atmos_additive;
    ROUGHNESS = 0.9;
    METALLIC = 0.0;
}
