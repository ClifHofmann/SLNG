# [FEAT-UI-05] Multi-Object Selection & Linking

- **Feature ID:** `FEAT-UI-05`
- **Track:** `ui` / `net`
- **Status:** `⏸️ Pending`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

Discovered while testing M5-2's Physics Shape Type feature: setting a prim's Physics Shape Type
to "None" is only valid for a **child prim of a linkset** (confirmed against Firestorm docs) --
but SLNG has no way to create a linkset at all. There is no multi-object selection (SL's
Shift+click to add objects to a selection) and no Link/Unlink command. A lighter, narrower
subset of this problem (editing an EXISTING linkset's individual parts once one exists) is
covered separately by `FEAT-UI-06` (Edit Linked Parts) and is not blocked on this spec.

Goal: let the user select multiple standalone objects (Shift+click, matching SL) and link them
into a single linkset (and unlink them again), the same way Firestorm's Build floater does via
its own "Link"/"Unlink" buttons.

## Functional Requirements

- **Additive multi-select via Shift+click:** clicking an object while holding Shift adds it to
  the current selection instead of replacing it (mirrors `World`'s existing additive
  `SelectEntity`/`DeselectEntity`, already used for the independent-multi-edit-window feature --
  see `ObjectSelectionController`'s `_pinnedEntityIds` for the precedent this can build on).
  Plain click (no Shift) continues to replace the selection as it does today.
- **Visual feedback:** all Shift-selected objects show the existing selection highlight
  (`ObjectRenderer.HighlightVisual`) simultaneously, with a clear indication of which one is the
  "last selected" (SL uses this as the link root -- the linkset keeps that object's position/
  rotation as the linkset's root transform).
- **Link command:** with 2+ objects selected, a "Link" action (context menu and/or a keybind,
  matching SL's Ctrl+L) sends the link request. Needs the LibreMetaverse `ObjectManager.LinkObjects`
  (or equivalent) call, mapped through `GridSession` per the established boundary rule (no
  LibreMetaverse type crosses into `SLNG.Core`/`app`).
- **Unlink command:** with a linked object (or a specific child part, once `FEAT-UI-06` exists)
  selected, an "Unlink" action splits the linkset back into standalone prims (`ObjectManager
  .DelinkObjects` or equivalent).
- **World model implications:** linking changes `TransformComponent.ParentLocalId` for the
  newly-added children and reparents them under the new root -- needs verification of how the
  simulator communicates this (a fresh `ObjectUpdate` for each re-parented child is the likely
  mechanism, already handled by `WorldSimulation.ApplyObjectUpdate`/`RecomposeChildren`, but the
  root's own local ID does not change, unlike a naive "new object" assumption).

## Acceptance Criteria

- [ ] Shift+click adds an object to the current selection; plain click replaces it.
- [ ] 2+ selected standalone objects can be linked into one linkset via a Link command.
- [ ] A linkset can be split back into standalone prims via an Unlink command.
- [ ] Selection highlighting correctly reflects the full multi-select set at all times.
- [ ] Linking/unlinking is reflected correctly in `TransformComponent.ParentLocalId` and renders
      correctly afterward (no orphaned or duplicated visuals).

## Technical Specs & Affected Files

- `app/scripts/ObjectSelectionController.cs` -- Shift+click additive selection, Link/Unlink
  keybind or context-menu entries.
- `app/scripts/UI/InWorldContextMenu.cs` -- Link/Unlink menu items (enabled/disabled based on
  current selection count and linked-state).
- `src/SLNG.Net/GridSession.cs` -- `LinkObjects(IEnumerable<uint> localIds)` /
  `UnlinkObjects(IEnumerable<uint> localIds)`, mapped from LibreMetaverse's `ObjectManager` link
  API.
- `src/SLNG.Core/WorldSimulation.cs` -- verify re-parenting on link/unlink flows correctly through
  the existing `ApplyObjectUpdate`/`RecomposeChildren`/`_children` bookkeeping.

## Sub-tasks / Progress

- [ ] Shift+click additive multi-select (visual + `World` selection set).
- [ ] Link command (`GridSession.LinkObjects` + UI trigger).
- [ ] Unlink command (`GridSession.UnlinkObjects` + UI trigger).
- [ ] Verify re-parenting renders correctly after link/unlink (no stale transforms/visuals).
