# [FEAT-UI-04] In-World 3D Transform Gizmos (Move/Rotate/Scale)

- **Feature ID:** `FEAT-UI-04`
- **Track:** `ui` / `render`
- **Status:** `⏸️ Pending`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

M5-2 shipped object editing entirely through the Build/Inspector window's numeric Position/
Rotation/Size fields — there is no interactive 3D manipulator in the viewport. This was explicit,
deferred scope in the original M5-2 spec ("Transform Gizmos (3D Handles)", Step 4) and came up
again when comparing against Firestorm's Move tool
([reference screenshot](https://i.gyazo.com/955fb85c616a16425b83ff0cb09a4917.png)): selecting an
object with Edit permissions shows a 3-axis translate manipulator (red/green/blue arrows along
X/Y/Z) at the object's pivot, draggable directly in the 3D view.

Goal: add that manipulator — starting with Move (translate), Rotate and Scale as later, separate
passes (see Sub-tasks) — so objects can be repositioned by dragging in-world instead of only
through the numeric fields.

## Functional Requirements

- **Move gizmo (this pass):** on selecting an object with Move permission (mirrors the Locked/
  `canMove` gating already added to the Position fields in `ObjectEditWindow`), render 3 colored
  arrow meshes (X=red, Y=green, Z=blue) at the object's pivot, in front of everything else
  (no depth-test against world geometry, like Firestorm's).
- **Screen-space constant size:** the gizmo must stay a roughly constant on-screen size regardless
  of camera distance (scale by distance-to-camera each frame), otherwise it's unusable up close or
  invisible far away.
- **Axis-constrained drag:** mouse-down on an arrow starts a drag constrained to that single world
  axis; mouse-move projects the cursor onto the axis (screen-space ray/axis closest-point, not a
  naive raycast against a plane) to compute the new position; mouse-up ends the drag.
- **Live network sync while dragging:** reuse `GridSession.UpdateObjectTransform` (already sends
  `SetPosition`/`SetRotation`/`SetScale`) — throttle sends during drag (e.g. only on real movement,
  not every input frame) to avoid flooding the connection, then send a final authoritative update
  on mouse-up.
- **Keep the numeric fields in sync:** dragging the gizmo must update `ObjectEditWindow`'s
  Position fields live (and vice versa — editing a field should move the gizmo), so both stay
  consistent; reuse the existing optimistic-update + `NotifyComponentUpdated` pattern from
  `ApplyTransform` (careful of the self-confirm class of bug fixed there — see
  `_suppressOwnComponentNotify`).
- **No gizmo without Move permission:** matches the Position-field disable already shipped; a
  Locked or non-Move-permission object shows no draggable gizmo (or shows it visibly disabled).

## Acceptance Criteria

- [ ] Selecting an editable object shows 3 colored arrow handles at its pivot.
- [ ] Dragging an arrow moves the object along only that axis, live, both visually and over the
      network (other clients/relogging shows the moved position).
- [ ] Gizmo stays a usable, roughly constant screen size at any camera distance.
- [ ] Locked / no-Move-permission objects show no draggable gizmo.
- [ ] Position fields in the Build/Inspector window and the gizmo stay in sync in both directions.
- [ ] Deselecting / closing the edit window removes the gizmo.

## Technical Specs & Affected Files

- `app/scripts/UI/SelectionGizmo3D.cs` (new) — arrow meshes, screen-space sizing, drag hit-testing
  and axis-projection math.
- `app/scripts/ObjectSelectionController.cs` — spawn/attach the gizmo to the current
  raycast-selection target; needs to distinguish "clicked the gizmo" from "clicked the object/
  ground" in its existing raycast dispatch.
- `app/scripts/UI/ObjectEditWindow.cs` — bidirectional sync with the Position fields; reuse the
  `_suppressOwnComponentNotify` pattern.
- `src/SLNG.Net/GridSession.cs` — `UpdateObjectTransform` already exists and should be reusable
  as-is for both the numeric-field Apply path and the gizmo-drag path.

## Sub-tasks / Progress

- [ ] Move (translate) gizmo — this spec's primary scope.
- [ ] Rotate gizmo (rings) — separate follow-up pass, not blocking Move.
- [ ] Scale gizmo (corner handles / cube) — separate follow-up pass, not blocking Move.
