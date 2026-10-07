# [BUG-UI-34] Landmark and inventory tree folders auto-expand on mouse hover

- **Feature ID:** `BUG-UI-34`
- **Track:** `ui`
- **Status:** `✅ Done`
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
In the Landmark view (`LandmarksWindow`), collapsed folders automatically expanded without any mouse click whenever the cursor hovered over them. Folders should only expand or collapse when the user explicitly clicks the fold arrow (▶ / ▼) or double-clicks the folder row. Hovering over a folder must never alter its collapsed state.

### Root Causes
1. **Permanent `DropModeFlags.OnItem`:** In `LandmarksWindow.cs`, `_tree` was initialized with `DropModeFlags = (int)Tree.DropModeFlagsEnum.OnItem;`. In Godot, setting drop mode flags permanently flags the tree as an active drop target and engages Godot's built-in drag/drop target detection.
2. **Godot `EnableDragUnfolding` Default:** In Godot Engine (`Tree`), `EnableDragUnfolding` (`enable_drag_unfolding`) defaults to `true`. When active alongside drop mode flags or drag events, Godot automatically unfolds any `TreeItem` hovered over by the cursor after a short wait time (`dragging_unfold_wait_msec`).
3. **Signal Cascading to Expanded State Map:** Once Godot unfolded a folder, the `Tree.ItemCollapsed` signal fired, updating `_expandedFolderKeys` in `LandmarksWindow.cs` so the folder became permanently recorded as expanded.
4. **Missing Double-Click Folder Toggle:** Double-clicking on a folder did not toggle its collapsed state in `OnItemActivated`.

### Fix
- In `app/scripts/UI/LandmarksWindow.cs`:
  - Removed `DropModeFlags = (int)Tree.DropModeFlagsEnum.OnItem;` from `_tree`. Drag-and-drop into folders continues to work cleanly via `_CanDropData`/`_DropData` and `GetItemAtPosition(atPosition)`.
  - Explicitly set `EnableDragUnfolding = false;` on `LandmarkTree` to guarantee Godot never auto-unfolds collapsed tree items on hover.
  - Set `FocusMode = FocusModeEnum.None;` so the tree does not capture keyboard focus away from chat/avatar controls.
  - Updated `OnItemActivated` to toggle folder collapse/expand on double-click or Enter.
- In `app/scripts/UI/InventoryPanel.cs`:
  - Explicitly set `EnableDragUnfolding = false;` on `_tree`, `_wornTree`, and `_outfitsTree`.
- In `app/scripts/UI/MoveLandmarkWindow.cs` & `app/scripts/UI/LandmarkDedupWindow.cs`:
  - Explicitly set `EnableDragUnfolding = false;` and `FocusMode = FocusModeEnum.None;`.
- In `app/scripts/Boot.cs`:
  - Bumped `AppVersion` to `v0.26.100-alpha`.

## Acceptance Criteria
- [x] Folders in `LandmarksWindow` never expand or collapse on mouse hover.
- [x] Folders only expand/collapse on clicking the fold arrow or double-clicking the folder row.
- [x] Dragging landmarks into folders still functions as expected.
- [x] Inventory and other floating window trees have `EnableDragUnfolding = false` enabled defensively.
- [x] `dotnet build SLNG.sln`, `dotnet build app/SLNG.App.csproj`, `dotnet test` (2843 passed), `godot --headless --path app -- --selftest` (97/97 passed), `dotnet format` clean.

## Technical Specs & Affected Files
- `app/scripts/UI/LandmarksWindow.cs`
- `app/scripts/UI/InventoryPanel.cs`
- `app/scripts/UI/MoveLandmarkWindow.cs`
- `app/scripts/UI/LandmarkDedupWindow.cs`
- `app/scripts/Boot.cs`
