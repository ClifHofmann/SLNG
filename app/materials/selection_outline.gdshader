// FEAT-UI-32: the selection highlight of an object being edited -- an outline that hugs the
// object's own shape, the way the reference viewer draws it, instead of a wireframe box around
// its bounds.
//
// The reference viewer extracts the real silhouette edges on the CPU (LLSelectNode::
// renderOneSilhouette, llselectmgr.cpp) and draws them as a camera-facing ribbon. That needs the
// edge set recomputed whenever the camera moves relative to the object. This is the inverted-hull
// equivalent: draw the object a second time with the FRONT faces culled and every vertex pushed
// outwards, so only the rim survives around the silhouette. It costs one extra draw call and no
// CPU work per frame.
//
// The push happens in CLIP space, along the vertex normal projected to the screen, scaled by w.
// That makes the outline a constant number of PIXELS wide at any distance -- which is also what
// the viewer does, by a different route: SelectionHighlightThickness (0.01) is documented as a
// "fraction of view distance" and is multiplied by the camera distance before use, so its ribbon
// is screen-constant too.
//
// Only the x/y of the clip position move; z and w are untouched. The hull therefore keeps the
// object's own depth, so the object occludes its own hull everywhere except at the rim, and world
// geometry in front of the object hides the outline just as it hides the object. RenderHiddenSelections
// defaults to 0 in both the Linden viewer and Firestorm, so a selection behind a wall is not
// meant to show through.
shader_type spatial;
render_mode unshaded, cull_front, depth_draw_never, shadows_disabled, fog_disabled;

uniform vec4 outline_color : source_color = vec4(1.0, 1.0, 0.0, 1.0);
// Half the reference viewer's ribbon, because its ribbon straddles the edge and only the outer
// half is visible against the background. See the spec for the arithmetic from
// SelectionHighlightThickness to pixels.
uniform float outline_thickness_px = 4.0;

void vertex() {
    vec4 clip = PROJECTION_MATRIX * (MODELVIEW_MATRIX * vec4(VERTEX, 1.0));
    // mat3(MODELVIEW_MATRIX) rather than a normal matrix: an outline does not care that a
    // non-uniform scale skews the direction slightly, and this spelling is stable across Godot
    // versions.
    vec3 view_normal = normalize(mat3(MODELVIEW_MATRIX) * NORMAL);
    vec2 screen_normal = (PROJECTION_MATRIX * vec4(view_normal, 0.0)).xy;
    // A face seen exactly edge-on projects to nothing; pushing a zero vector is a no-op, and
    // normalize() of it is a NaN that would smear the whole triangle across the screen.
    if (length(screen_normal) > 0.0001) {
        clip.xy += normalize(screen_normal) * (outline_thickness_px * 2.0 / VIEWPORT_SIZE) * clip.w;
    }
    POSITION = clip;
}

void fragment() {
    // ALPHA is deliberately not written. Writing it at all moves the material into Godot's
    // transparent queue, where it would be depth-sorted against every other transparent surface
    // in the scene and flicker against alpha prims for no gain -- the outline is opaque.
    ALBEDO = outline_color.rgb;
}
