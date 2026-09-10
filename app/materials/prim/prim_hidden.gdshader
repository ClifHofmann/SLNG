// BUG-RENDER-16: a surface that draws NOTHING. Assigned to a shared ArrayMesh surface whose
// geometry has been handed to a separate MeshInstance3D (ObjectRenderer.SplitSortedSurfaces), so
// the parent stops drawing it without the shared mesh being touched -- Godot has no per-surface
// visibility, and the mesh is shared by every object with the same geometry.
//
// Collapsing every vertex to one point makes every triangle zero-area: nothing is rasterised,
// no fragment shader runs. Opaque (no ALPHA written) so it never enters the transparent sort,
// and shadows_disabled so the shadow pass skips it too.
shader_type spatial;
render_mode unshaded, shadows_disabled, cull_disabled;

// Never read. SelfTest treats "zero uniforms" as "did not compile", and a stand-in that draws
// nothing genuinely needs none, so it carries one to stay checkable.
uniform float slng_hidden_marker = 1.0;

void vertex() {
    VERTEX = vec3(0.0);
}

void fragment() {
    discard;
}
