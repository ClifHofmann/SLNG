// FEAT-RENDER-01 variant: alpha-blended face, for AVATAR surfaces (double-sided).
//
// The two-sided twin of prim_blend; see prim_opaque_avatar.gdshader for why the cull mode
// has to be its own compile-time variant instead of a uniform.
//
// Reached by graded-alpha avatar content -- hair cards above all, where both faces of a strand
// card genuinely need to draw. prim_blend's note that the viewer back-face culls blended PRIM
// faces is still true; it just does not describe worn avatar geometry, which arrives already
// authored as single-sided cards.
//
// Keep the body in lockstep with prim_blend.gdshader.
shader_type spatial;
// BUG-RENDER-09: depth_prepass_alpha. A blended face writes NO depth, and an avatar face is
// cull_disabled with many overlapping layers -- so the front and back of the same leg, or two
// worn alpha layers over the same skin, have no defined order and resolve into hard-edged
// translucent patches (live screenshots of a shin under two alpha-layer wearables, 2026-09-03).
// depth_prepass_alpha adds a depth prepass for the near-opaque part of the surface (Godot cuts
// the prepass at alpha 0.99), so the solid skin/strand-core establishes depth exactly like an
// opaque face while the soft edge still blends.
//
// BUG-RENDER-12 replaced this with depth_draw_always for one round (v0.21.19-20), porting the
// viewer's `write_depth = rigged` (lldrawpoolalpha.cpp:240-248). REVERTED, confirmed in-world:
// with an unconditional depth write only the "nearest so far" chain survives at each pixel, so a
// hair card's faint halo -- anything above the minimum-alpha discard, i.e. one 8-bit step --
// claims depth and rejects every strand behind it. The stack stops accumulating and the hair
// renders as a single translucent layer with the shirt showing through. This is the same failure
// as the 2026-07 "coloured in with a marker" attempt, now precisely named.
//
// Two reasons it does NOT port cleanly, both worth knowing before anyone tries again:
//   * The viewer BACK-FACE culls rigged alpha (LLGLSPipelineAlpha touches only GL_BLEND; the
//     pipeline's own GL_CULL_FACE stays on), while this variant is cull_disabled -- so roughly
//     twice as many fragments here can claim depth as there.
//   * Depth write is only half of the viewer's design. The other half, authored-order batching
//     (llvovolume.cpp:6332-6335), IS ported and kept: AvatarRenderer merges consecutive
//     same-material faces into one surface, which is what removes the sort instability. That half
//     stands on its own; this half does not.
render_mode blend_mix, depth_draw_opaque, depth_prepass_alpha, cull_disabled, diffuse_burley, specular_schlick_ggx;

#include "res://materials/prim/prim_common.gdshaderinc"

// The viewer's MINIMUM_ALPHA, "~ 1/255" (lldrawpoolalpha.cpp:63) -- one 8-bit alpha step. See the
// discard in fragment() for the citation.
const float MINIMUM_ALPHA = 0.004;

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

    // BUG-RENDER-12: the viewer's own minimum-alpha discard, kept after depth_draw_always was
    // reverted -- it is correct on its own terms. class2/deferred/alphaF.glsl:208-213:
    //     // Insure we don't pollute depth with invis pixels in impostor rendering
    //     if (final_alpha < minimum_alpha) { discard; }
    // minimum_alpha is MINIMUM_ALPHA = 0.004f, "~ 1/255" (lldrawpoolalpha.cpp:63), set on every
    // shader the alpha pool prepares (:129). Below one 8-bit step of alpha a fragment contributes
    // nothing visible, so it should not reach the depth prepass or the blend either.
    //
    // A bare discard does NOT move this material to the opaque queue the way
    // ALPHA_SCISSOR_THRESHOLD would; it stays a blended surface, which is the point.
    if (alpha < MINIMUM_ALPHA) {
        discard;
    }

    ALPHA = alpha;
}
