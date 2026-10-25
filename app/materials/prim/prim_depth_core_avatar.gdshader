// BUG-RENDER-50: the alpha DEPTH pass of a worn rigged mesh -- the avatar twin of prim_depth_core.
//
// The reference viewer draws rigged alpha in a first pass of its own that WRITES DEPTH
// (lldrawpoolalpha.cpp:203-205, "first pass, render rigged objects only and render to depth buffer";
// :240 write_depth = rigged), in attachment order (WornAlphaOrder). So a worn item drawn later that
// lies BEHIND an earlier one is hidden where the earlier one is drawn, instead of painting over it.
// Godot's transparent pass writes no depth, so that half was missing: a wearer's rear add-on, drawn
// after her bikini, showed its strips through the fabric.
//
// This pass writes depth only, for the fragments of one worn item whose alpha reaches
// core_alpha_threshold, and leaves the colour buffer alone (ALPHA = 0 under blend_mix). It is NOT a
// next_pass and NOT a render priority: both would draw it in the wrong place. Godot sorts a next_pass
// at the same depth and priority as its colour pass, and that tie is decided by an unstable introsort
// (BUG-RENDER-16), so the pass would land before its own colour about half the frames; a lower priority
// runs it before EVERY transparent draw of the scene. AvatarRenderer puts it on an instance of its own
// at WornAlphaOrder.DepthOffset -- after every surface of its item, before the next item -- so it
// occludes exactly what the viewer's write occludes: what is drawn later and lies behind.
//
// Identical to prim_depth_core.gdshader but cull_disabled, because the avatar colour variants are (see
// prim_blend_avatar.gdshader): a core written for a face the colour pass draws must match it. The
// uniforms must stay the same as prim_depth_core's (SelfTest "shader variants").
shader_type spatial;
render_mode blend_mix, depth_draw_always, cull_disabled, unshaded, shadows_disabled, fog_disabled;

#include "res://materials/prim/prim_common.gdshaderinc"

// Set from RenderConfig.WornAlphaDepth (--worn-alpha-depth=N). See there for why 0.33 and not the
// viewer's 0.004.
uniform float core_alpha_threshold : hint_range(0.0, 1.0) = 0.33;

void vertex() {
    // The SAME placement prim_blend_avatar applies, so both passes rasterise the identical texel.
    UV = slng_transform_uv(UV, VERTEX, NORMAL);
}

void fragment() {
    float alpha = albedo_color.a;
    if (has_albedo_texture) {
        alpha *= texture(albedo_texture, UV).a;
    }
    if (alpha < core_alpha_threshold) {
        discard;
    }
    ALBEDO = vec3(0.0);
    ALPHA = 0.0;

    // A hair farther than the colour pass landed, as in prim_depth_core: a later layer lying ON this
    // one (an onion layer, a coplanar decal) must still pass, whatever ulp the two shader variants
    // round to. 1e-4 relative: 0.2 mm at 2 m.
    DEPTH = FRAGCOORD.z * (1.0 - 1e-4);
}
