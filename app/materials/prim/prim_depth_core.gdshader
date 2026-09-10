// BUG-RENDER-16: the alpha depth pass -- a DEPTH-ONLY companion to prim_blend.gdshader for dense
// high-frequency foliage, chained onto the blend material as its next_pass.
//
// WHY THIS EXISTS. Godot's transparent queue draws in one order per frame and never writes
// depth, so wherever that order is wrong a far blade is composited OVER a near one. Freezing the
// order (--alpha-sort-freeze) proved it is not the RE-ordering that flickers: a wrong-but-frozen
// order still shows the far blade cutting through the near one, and when the avatar walks the
// parallax slides those crossings across every plume -- that is the "zappeln". Turning the camera
// about the eye moves no crossing (every occlusion relation is preserved under pure rotation),
// which is why orbiting was always calm. Only real per-pixel depth removes it.
//
// WHAT IT DOES. Runs before every ordinary transparent draw (RenderPriority -2, i.e. ahead of
// water at -1 and the default bucket at 0 -- the alpha comparator sorts by priority FIRST,
// render_forward_clustered.h:722-726) and writes depth for every fragment whose alpha reaches
// core_alpha_threshold, while leaving the colour buffer untouched (ALPHA = 0 under blend_mix is
// dst*(1-0) = dst). The blend pass then runs as before, depth-tested against those cores: a soft
// fringe behind another blade's core is rejected in EVERY draw order, a core's own blend pass
// lands on the identical depth (same vertex path, COMPARE_OP_GREATER_OR_EQUAL passes on equal)
// and blends exactly as it did, and the only pairs the sort still decides are fringe-over-fringe,
// both below the threshold and therefore low-contrast.
//
// This is the reference viewer's own construction, twice over: LLDrawPoolAlpha::render runs a
// colour-masked depth pass with setMinimumAlpha(0.33f) over the alpha pool
// (lldrawpoolalpha.cpp:212-227), and forwardRender(rigged=true) writes depth for every worn alpha
// face before the unrigged pass (lldrawpoolalpha.cpp:240-248). Godot's built-in
// depth_prepass_alpha is the same idea with a threshold of 0.99 (render_forward_clustered.cpp:1823,
// not tunable), which is why --foliage-alpha=prepass still flickered: mid-alpha grass never
// entered it. This pass owns its threshold.
//
// Cost: the foliage subset is rasterised twice, but this pass is unshaded, samples one texture
// channel and writes nothing but depth. Not in the shadow map and not in the opaque prepass
// (no depth_prepass_alpha / alpha_to_coverage, see uses_depth_in_alpha_pass()).
//
// depth_draw_always is what makes a transparent-queue material write depth at all
// (scene_shader_forward_clustered.cpp:443-445 disables the write for depth_draw_opaque only).
// cull_back must match prim_blend.gdshader -- a core written for a face the colour pass culls
// would occlude with nothing drawn on it.
shader_type spatial;
render_mode blend_mix, depth_draw_always, cull_back, unshaded, shadows_disabled, fog_disabled;

#include "res://materials/prim/prim_common.gdshaderinc"

// The viewer's 0.33 (lldrawpoolalpha.cpp:217). ObjectRenderer sets it from
// RenderConfig.FoliageCoreAlpha so it can be A/B'd with --foliage-core-alpha=N.
uniform float core_alpha_threshold : hint_range(0.0, 1.0) = 0.33;

void vertex() {
    // Only the diffuse placement is needed, but it must be the SAME transform prim_blend applies
    // so the two passes rasterise the identical texel at the identical depth.
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
    // Depth only. Zero alpha under blend_mix leaves the colour buffer exactly as it was.
    ALBEDO = vec3(0.0);
    ALPHA = 0.0;

    // Nudge the core a hair FARTHER than the colour pass will land (reversed Z: smaller is
    // farther). The two passes are different shader variants (this one unshaded, prim_blend
    // shaded), and the driver is free to contract/reorder the position maths differently in
    // each, so their depths agree only to within an ulp or two -- and GREATER_OR_EQUAL on a
    // coin-flip ulp rejected the colour pass of its OWN core on roughly half the triangles.
    // Seen in-world on v0.22.24 as pine needle clumps drawn as sky-coloured silhouettes (the
    // water behind them was rejected by the core too, so the hole showed the sky) while the
    // neighbouring clumps rendered correctly. 1e-4 relative is ~1000x the ulp noise and moves
    // a core 1 cm at 100 m, 0.1 mm at 1 m: a fringe closer behind a core than that still
    // blends, which nothing can see.
    DEPTH = FRAGCOORD.z * (1.0 - 1e-4);
}
