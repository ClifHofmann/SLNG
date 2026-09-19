// FEAT-UI-32: the companion to selection_outline.gdshader, and the reason a transparent prim
// gets an outline instead of a filled silhouette.
//
// The outline is an inflated hull drawn with its front faces culled, so what remains inside the
// object's own footprint is the hull's BACK faces -- and those are only hidden because the object
// itself wrote depth in front of them. An alpha-blended surface writes no depth (see
// avatar-blend-faces-have-no-depth-write), so for an invisible sit-target prim nothing rejected
// them and the whole silhouette came out as a solid yellow slab. The reference viewer has no such
// failure because it draws silhouette EDGES, which have no interior to fill.
//
// So the object writes its own depth here, on a second invisible pass drawn before the outline.
// ALPHA = 0.0 contributes no colour; depth_draw_always makes it write depth anyway.
//
// Verified in an offline render that this does NOT leak into the scene depth: an opaque wall
// standing behind a masked transparent sphere stays fully visible through it. The write lands
// late enough that only the outline is measured against it.
shader_type spatial;
render_mode unshaded, depth_draw_always, shadows_disabled, fog_disabled, cull_disabled;

// Never read. SelfTest counts a shader with zero uniforms as one that failed to compile, and a
// pass that draws nothing genuinely needs none -- same arrangement as prim_hidden.gdshader.
uniform float slng_depth_mask_marker = 1.0;

void fragment() {
    ALBEDO = vec3(0.0);
    ALPHA = 0.0;
}
