# [FEAT-UI-32] Selection highlight: an outline that hugs the object

- **Feature ID:** `FEAT-UI-32`
- **Track:** `render` / `ui`
- **Status:** `🧪 Review` (implemented `v0.23.56-alpha`, shader verified in an offline render, in-world not yet confirmed)
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

Selecting an object for editing drew a **wireframe box around its bounding box**, in yellow for the
root prim and cyan for the children, with depth testing off so it was visible through walls. The
reference viewer draws something quite different: an outline that follows the object's own
silhouette. On anything that is not a cube — a sphere, a torus, a sculpt, a mesh — the box says
almost nothing about what is actually selected, and on a large linkset it is a cage of boxes.

Goal: match the reference viewer's selection highlight.

## What the reference viewer does

From `indra/newview/llselectmgr.cpp` in the Linden viewer source (vendored under
`scratch/slviewer`), cross-checked against Firestorm's `app_settings/settings.xml`:

| Thing | Reference viewer | Where |
|---|---|---|
| Shape | The object's real **silhouette edges**, extracted on the CPU and drawn as a camera-facing ribbon | `LLSelectNode::renderOneSilhouette`, `LLVOVolume::generateSilhouette` |
| Root colour | `SilhouetteParentColor` = `Yellow` = `1 1 0` | `skins/default/colors.xml` |
| Child colour | `SilhouetteChildColor` = `0.13 0.42 0.77` — a deep blue, not cyan | `skins/default/colors.xml` |
| Thickness | `SelectionHighlightThickness` = `0.01`, documented as a *fraction of view distance* and multiplied by the camera distance before use — so it is **constant on screen** | `settings.xml`, `renderOneSilhouette` |
| Through walls | `RenderHiddenSelections` defaults to **0** in the Linden viewer *and* in Firestorm. When it is on, the occluded part is drawn additively at alpha 0.4 | `settings.xml`, `renderOneSilhouette` |

## How SLNG does it

Not by extracting silhouette edges. That set changes whenever the camera moves relative to the
object, so it has to be recomputed and re-uploaded; it is the expensive half of the viewer's
implementation. SLNG draws an **inverted hull** instead: the same geometry a second time, front
faces culled, every vertex pushed outwards, so only a rim survives around the silhouette. One extra
draw call, no per-frame CPU work, and the result is the same picture.

Two details carry the whole thing:

**The push happens in clip space.** `app/materials/selection_outline.gdshader` offsets only `x`/`y`
of the clip position, along the vertex normal projected to the screen and scaled by `w`. That makes
the outline a constant number of pixels wide at any distance — the same property the viewer gets
from its "fraction of view distance" thickness. Because `z` and `w` are untouched, the hull keeps
the object's own depth: the object occludes its own hull everywhere except at the rim, and geometry
in front of the object hides the outline exactly as it hides the object. That is the
`RenderHiddenSelections = 0` behaviour, for free.

Arithmetic for the pixel value: the viewer's ribbon subtends `0.01` rad, which is
`0.01 / (2·tan(fov/2))` of the viewport height, and the ribbon straddles the edge so only half of
it shows against the background. At 1080p and 60° that outer half is a little under 5 px, hence the
4 px default.

**The hull's normals are welded.** A prim box arrives with 24 vertices carrying six face normals.
Push each face along its own normal and the faces separate at every corner, leaving a notch in the
outline exactly where the eye looks for a corner. `ObjectRenderer.BuildOutlineHull` therefore welds
vertices by position (0.1 mm quantum, across surfaces) and averages the normals meeting there, so a
corner has one outward direction and the hull stays closed. The mesh's own normals are used when it
has them, which is every prim and every decoded mesh asset.

The geometric fallback, for geometry that arrives without normals, **negates** the cross product:
Godot winds front faces clockwise, so `(b-a)×(c-a)` points *into* the object. The first version did
not negate it, which shrank the hull inside the object and made the outline vanish completely — see
"Verification" below, which is how that was caught.

## Acceptance Criteria

- [x] The highlight follows the object's shape rather than its bounding box.
- [x] Root prim yellow, child prims the reference viewer's blue.
- [x] Constant thickness on screen at any camera distance.
- [x] Occluded by geometry in front of the object, matching `RenderHiddenSelections = 0`.
- [x] No corner notches on a hard-edged prim.
- [x] The hull is re-cut when the prim's geometry is replaced while it is selected (a sculpt or
      mesh that finishes loading after the click; a prim type changed in the build window).
- [ ] Confirmed in-world against Firestorm on a prim, a sculpt, a mesh and a linkset.

## Technical Specs & Affected Files

- `app/materials/selection_outline.gdshader` (new) — the inverted-hull vertex offset.
- `app/scripts/ObjectRenderer.cs` — `BuildOutlineHull` / `GetOutlineHull` (welding and a capped
  cache keyed by the source mesh, so a linkset of identical parts cuts one hull),
  `ApplySelectionOutline` replacing `ApplyHighlightBox`, and `TickSelectionOutlines` for the
  re-cut.

## Verification

The shader was checked before it shipped, in an offline Godot project rendering a cube and a torus
against an occluder — no grid login involved. The first render showed **no outline at all**, which
located the inverted normals immediately; the second showed a clean outline on the welded hull, the
notches on an unwelded one for comparison, and the outline correctly cut off by a wall in front of
it.

## Known limitations

- **Single-sided geometry gets no outline on its front.** Culling front faces leaves nothing to
  inflate on a sheet that has no back. Prims are closed solids, so this affects mesh assets built
  as single-sided sheets.
- **An alpha-blended prim does not occlude its own hull**, because a blended surface writes no
  depth (see `avatar-blend-faces-have-no-depth-write`). The outline of a glass prim will therefore
  show through it. Fixing it needs a depth-only pre-pass for the selected object.
- No setting for `RenderHiddenSelections` yet; SLNG behaves as the viewers' default (off).
