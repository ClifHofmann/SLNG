# [FEAT-UI-05] Multi-Object Selection & Linking

- **Feature ID:** `FEAT-UI-05`
- **Track:** `ui` / `net`
- **Status:** `🧪 Review` (implemented `v0.23.61-alpha`, not yet confirmed in-world)
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

- [x] Shift+click adds an object to the current selection; plain click replaces it.
- [x] 2+ selected standalone objects can be linked into one linkset via a Link command.
- [x] A linkset can be split back into standalone prims via an Unlink command.
- [x] Selection highlighting correctly reflects the full multi-select set at all times — it rides on FEAT-UI-32's outline, and a link-selected object is no longer dropped by a plain click elsewhere.
- [ ] Linking/unlinking is reflected correctly in `TransformComponent.ParentLocalId` and renders
      correctly afterward (no orphaned or duplicated visuals) — covered by unit tests, in-world
      confirmation outstanding.

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

- [x] Shift+click additive multi-select (visual + `World` selection set).
- [x] Link command (`GridSession.LinkObjects` + UI trigger).
- [x] Unlink command (`GridSession.UnlinkObjects` + UI trigger).
- [x] Verify re-parenting renders correctly after link/unlink (no stale transforms/visuals).

## How it came out (`v0.23.61-alpha`)

**Root order.** The object picked **last** becomes the root, confirmed by the user against the
viewer, and it goes **first** in the `ObjectLink` packet. LibreMetaverse's own doc comment on
`LinkPrims` says the opposite ("the last object in the array will be the root"); it is wrong.
OpenSim's `LLClientView.HandleObjectLink` reads `ObjectData[0]` as the parent and every later
entry as a child. LMV's comments had already been wrong once on the neighbouring transform path
the same day, so this was taken from the server that has to act on it rather than from the
client library that describes it.

**Where the gesture lives.** Shift-click gathers objects only while an edit window is open, the
same scoping as FEAT-UI-06's part picking and for the same reason: outside an edit session a
shift-click is just a click on the world, and a build window must not change what clicking means
everywhere. Shift-clicking an object already in the selection drops it again, and re-adding it
moves it to the end — which makes it the root, so the rule stays "the last one you picked".

**Buttons.** Link and Unlink sit under the "edit linked parts" checkbox, above the tabs, the way
the reference viewer's build floater arranges them. They are gated on the agent's Modify right
(`EditPermission.CanModify`, the same source as every other field since FEAT-SEC-04) rather than
left to the simulator, which refuses silently.

**The index nobody was updating.** `WorldSimulation` keeps a parent → children index for
re-composing child transforms, and nothing ever removed a prim from its OLD parent when its
parent changed — which had never happened before, because before this feature the client could
not link or unlink anything. Positions survived it (`ResolveWorldTransform` returns early once
`ParentLocalId` is 0), but every question answered from the index did not: the old root kept
counting as a linkset root, so Unlink would have stayed offered on a prim with nothing under it.
Three tests in `WorldSimulationReparentTests` cover the removal, a re-link under a different
root, and the new `ObjectReparented` event that tells the UI to re-read the buttons.
