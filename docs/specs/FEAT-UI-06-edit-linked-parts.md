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
- [x] Toggling Edit Parts is discoverable from the in-world context menu.

## Technical Specs & Affected Files

- `app/scripts/ObjectSelectionController.cs` -- gate the root-resolution walk-up on the toggle
  state.
- `app/scripts/UI/ObjectEditWindow.cs` -- gate `EditObject`'s root-resolution walk-up the same way.
- `app/scripts/UI/InWorldContextMenu.cs` -- toggle entry.

## Sub-tasks / Progress

- [x] Add an Edit Parts toggle state, plumbed to both root-resolution call sites.
- [x] Expose the toggle from the context menu.
