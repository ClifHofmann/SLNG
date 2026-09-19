# [FEAT-UI-06] Edit Linked Parts

- **Feature ID:** `FEAT-UI-06`
- **Track:** `ui`
- **Status:** `🧪 Review`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

Lightweight subset of `FEAT-UI-05` (Multi-Object Selection & Linking), split out because it needs
no new protocol work: SLNG already resolves a clicked child prim's parent and world transform
(`TransformComponent.ParentLocalId`, `WorldSimulation.ResolveWorldTransform`/`RecomposeChildren`)
-- but both `ObjectSelectionController`'s click handler and `ObjectEditWindow.EditObject` always
walk up to the linkset's root before selecting/editing, so an individual child prim inside an
existing linkset can never be targeted directly. Real SL/Firestorm has an "Edit Linked Parts"
toggle (Build floater) for exactly this. Mesh/attachment products that ship as many linked prims
(a common case already handled specially for rendering, see the rigged-mesh-attachment logic in
`WorldSimulation.ApplyObjectUpdate`) are otherwise unreachable for individual editing (e.g.
per-part Physics Shape Type, which is what surfaced this gap -- "None" is only valid on a child
prim, and there was no way to select one).

## Functional Requirements

- **Toggle, not a mode switch nobody can find:** an "Edit Parts" checkbox/button, visible when a
  linked object is selected (root has children, or the clicked object itself is a child).
- **When OFF (default, current behavior):** clicking any prim of a linkset selects/edits the
  root, matching today's behavior exactly -- no regression for the common case.
- **When ON:** clicking a prim selects/edits that SPECIFIC part directly, without the
  root-resolution walk-up currently in `ObjectSelectionController`/`ObjectEditWindow.EditObject`.
- **Per-part editing:** the Build/Inspector window opened for a child part shows and edits THAT
  part's own transform/flags/material/physics/etc, not the root's.

## Acceptance Criteria

- [x] With Edit Parts OFF, clicking any part of a linkset still selects/edits the root (no
      behavior change from before this feature).
- [x] With Edit Parts ON, clicking a child part selects/edits that part specifically.
- [x] Toggling Edit Parts is discoverable from the in-world context menu. **Superseded
      `v0.23.58-alpha`:** the checkbox lives in the edit window, above the tabs, where the
      reference viewer keeps it ("Teile bearbeiten"). It belongs to the edit session, not to the
      right-click menu that started it, and putting it there means it can be flipped while
      looking at the object instead of re-opening a menu over it.
- [x] With an edit window open, a plain **left click** on the linkset picks which prim is being
      edited, and the open window follows it.

## Technical Specs & Affected Files

- `app/scripts/ObjectSelectionController.cs` -- gate the root-resolution walk-up on the toggle
  state.
- `app/scripts/UI/ObjectEditWindow.cs` -- gate `EditObject`'s root-resolution walk-up the same way.
- `app/scripts/UI/InWorldContextMenu.cs` -- toggle entry (removed again in `v0.23.58-alpha`).
- `app/scripts/Boot.cs` -- `RetargetObjectEditWindow`, and `ObjectEditWindow.CurrentEntityId` so
  the window's `Closed` handler unpins the prim it is actually showing rather than the one it was
  opened on.

## Sub-tasks / Progress

- [x] Add an Edit Parts toggle state, plumbed to both root-resolution call sites.
- [x] Expose the toggle from the context menu.
- [x] Move it into the edit window and drive part selection by left click (`v0.23.58-alpha`).

## Left click picks the part (`v0.23.58-alpha`)

Requested live: *"das Teile bearbeiten sollte aus dem Kontextmenü raus und ins Edit-Menü rein —
die Auswahl von einem Parent-Item erfolgt dann einfach per LMB bei einem aktiven Objekt."*

A left click inside an open edit session now selects rather than touches, and the window already
open moves onto the prim that was clicked instead of a second window opening beside it — one
window per prim of a linkset would bury the screen, and the reference viewer has a single build
floater that follows the selection.

Deliberately scoped to the linkset that is already open for editing. Clicking any other object
still runs its click action, because an open build window should not swallow every left click in
the world. The flip side is that while a window is open you cannot touch the object it is showing;
that is also how the reference viewer behaves in edit mode.

One trap this uncovered: `ObjectRenderer.HighlightVisual` took its "highlight only the one part"
path on deselect as well as on select. With the toggle now reachable mid-session, flipping it
between selecting and deselecting stripped the outline off a single prim and left it stuck on
every other part of the linkset. Deselect always walks the whole linkset now.

## A child prim is edited in its parent's frame (`v0.23.59-alpha`)

Reported in-world one version later, and the reason it matters: *"bei Linksklick ist mir das
Objekt gerade zerfallen, ich vermute ein Teil wurde weit verschoben."* Picking a part and then
touching the gizmo flung that part across the region — and it was a real, persisted move on the
simulator, not a display error.

A `MultipleObjectUpdate` for a **child** prim is read relative to its root. Verified in OpenSim's
own source rather than assumed: `SceneGraph.UpdatePrimSinglePosition` →
`SceneObjectGroup.UpdateSinglePosition`, which calls `UpdateRootPosition` for the root part and
`part.UpdateOffSet(pos)` for every other one; `UpdateSingleRotation` splits the same way into
`UpdateRootRotation` and `part.UpdateRotation`, which writes `RotationOffset`. Both the gizmo and
the numeric fields were sending the **world** transform for every prim, so a child was displaced
by roughly its root's position in the region — over a hundred metres on an ordinary build.

Fixed in one place: `SLNG.Core.LinksetTransform` holds the composition and its exact inverse, and
`WorldSimulation.ResolveWorldTransform` now calls the same forward function so the pair cannot
drift apart. The gizmo converts before sending, and refuses to send at all while the root prim has
not arrived — there is no frame to express the child in, and a guess would fling it. The numeric
fields both show and send a child's parent-relative transform, which is also what the reference
viewer displays for a linked part.

### The wire flag has to agree with the frame (`v0.23.60-alpha`)

Sending the right vector was only half of it. Reported straight after: *"ich hab jetzt einen
verlinkten Cube bewegt und nach dem Loslassen sehe ich den nicht mehr."*

LibreMetaverse's three-argument `SetPosition` sends `UpdateType.Position | UpdateType.Linked`,
and the three-argument `SetRotation` an `ObjectRotation` packet — both mean *the whole group*, and
the simulator then reads the vector as the **group's absolute position**
(`SceneGraph.UpdatePrimGroupPosition`). So the corrected child offset, a value like
`(0.5, 0, 0.2)`, teleported the entire linkset to that spot read as a region coordinate: the
corner of the region at ground level, which from the camera is simply gone.

`UpdateObjectTransform` now takes a `singlePrim` flag and uses LibreMetaverse's `childOnly`
overloads, which leave `Linked` off and take the simulator's single-prim route instead
(`SceneObjectGroup.UpdateSinglePosition` → `UpdateRootPosition` for the root part,
`part.UpdateOffSet` for any other). Confirmed against the pinned LibreMetaverse 3.1.6 source, not
the stale vendored copy, because the flag is the whole point.

The flag and the frame are one decision and are documented together on that method: a group update
and a single-**root** update both want the root's absolute region position — `UpdateRootPosition`
keeps the children in place by shifting their offsets — while a single update for a **child** wants
its offset. Which route is taken follows "edit linked parts", which is also what the reference
viewer does: with it on, dragging the root moves only the root prim inside the linkset.

Two things were repaired on the way: `ApplyTransform` used to write only the world
`Position`/`Rotation` into the ECS, so the next unrelated `ObjectUpdate` restored the old place
(`ResolveWorldTransform` recomputes the world transform from the local one); and scale is
deliberately left alone, because in SL a prim's size is its own and does not inherit from the
root.
