# [BUG-UI-39] Clear transient object selection on sit or click-off

- **Feature ID:** `BUG-UI-39`
- **Track:** `ui`
- **Status:** `✅ Done`
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](../ROADMAP.md)

## Overview & Goal

When right-clicking an in-world object, the object is selected to anchor the context menu and show what is targeted.
Outside an active Edit session, this selection is transient: when the user sits on the object, sits on the ground, clicks away into empty sky or terrain, dismisses the context menu, or hits Escape, the selection highlight and simulator selection should be cleared immediately.
When the user is in an active Edit session (`_editSessionOpen`), the selection remains persistent and is preserved across interactions until explicitly deselected or the edit session is closed, matching Second Life and Firestorm reference viewer behavior (`LLSelectMgr`).

## Root Cause

1. In `app/scripts/ObjectSelectionController.cs`, left-clicking terrain (`LocalId = "TERRAIN"`) hit the collider but failed `uint.TryParse("TERRAIN", out _)`. The branch fell through without executing the empty-space deselect logic, leaving the transient highlight on the previously clicked object indefinitely.
2. In `app/scripts/UI/InWorldContextMenu.cs`, dismissing the menu or selecting non-edit actions (such as "Sit", "Touch", "Inspect", etc.) did not notify the selection controller to clear the transient selection.
3. Sitting on an object or bare ground, or transitioning to sitting, did not clear the transient highlight.
4. Deselection only notified the linkset root LocalId to the simulator instead of clearing linkset child parts that were selected via `SelectFamily`.

## Fix

1. In `app/scripts/UI/InWorldContextMenu.cs`:
   - Added `OnClosedWithoutEdit` callback.
   - Updated `AddMenuButton` so non-edit actions fire `OnClosedWithoutEdit`.
   - Added `Dismiss()` method triggered on clicks outside and on `Key.Escape`.
   - Added `IsInsideTree()` guard to `ClampIntoViewport`.
2. In `app/scripts/ObjectSelectionController.cs`:
   - Added `DeselectFamily` to cleanly deselect linkset child parts on the simulator.
   - Added `ClearTransientSelection()` which safely clears `_lastClicked` in `World` and deselects via `DeselectFamily` when outside an edit session, while preserving selection when `_editSessionOpen` is true.
   - Connected `_contextMenu.OnClosedWithoutEdit += ClearTransientSelection;` in `Initialize`.
   - Handled left-clicking terrain (`!isTaggedObject`) to invoke `ClearTransientSelection()` (or `DeselectAll()` in build mode).
   - Handled left-clicking empty space and handling unhandled `Key.Escape` to clear transient selection.
   - Handled left-clicking another object to clear prior transient selection before performing touch or sit.
   - Added `LastClicked` and internal `MarkRightClickedForTesting` helper.
3. In `app/scripts/Boot.cs` and `app/scripts/Boot.KeyActions.cs`:
   - Wired `ClearTransientSelection()` into `OnSitClicked`, `OnSitOnGroundClicked`, `ToggleSitStand()`, and sitting state transitions.
   - Bumped `AppVersion` to `v0.27.20-alpha`.
4. In `app/scripts/SelfTest.cs`:
   - Added `CheckTransientSelectionClear` verifying right-click transient selection clearing on menu dismiss, sitting/clearing, and persistence inside edit sessions.

## Acceptance Criteria

- [x] Right-clicking an object highlights it and opens the context menu.
- [x] Sitting on an object or ground clears the transient selection.
- [x] Left-clicking terrain, empty sky, or another object clears the transient selection.
- [x] Dismissing the context menu or pressing Escape clears the transient selection.
- [x] Inside an Edit session, selection is persistent and preserved across sit / ground clicks.
- [x] SelfTest `CheckTransientSelectionClear` passes (118/118 checks pass).
