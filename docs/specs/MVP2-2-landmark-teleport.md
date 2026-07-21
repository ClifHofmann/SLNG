# [MVP2-2] Landmark Teleport & Creation (Phase 1 of World Map, Minimap & Teleport)

- **Feature ID:** `MVP2-2`
- **Track:** `net` / `ui`
- **Status:** `✅ Done`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
`MVP2-2` ("World Map, Minimap & Teleport") bundles minimap overlay, full grid map, region
search, and teleport-to-landmark/coords. This pass ships the two smallest independently useful
slices: **teleporting via a landmark inventory item**, and **creating a landmark at the agent's
current location** (Firestorm-style "Create Landmark" dialog: name, destination folder, optional
new subfolder, notes). Neither needs new map UI or grid-coordinate math — LibreMetaverse resolves
a landmark's destination server-side from its asset UUID, and encodes a new landmark asset from
just the current region + local position. Minimap/world-map/search/teleport-by-coordinates
remain out of scope for this pass and stay open under `MVP2-2`.

## Acceptance Criteria
- [x] Right-clicking a Landmark item in the Inventory panel offers a "Teleport" action; the
      action is disabled/absent for non-landmark items and folders.
- [x] Selecting it teleports the agent via LibreMetaverse's landmark teleport API without any
      LibreMetaverse type crossing the `SLNG.Net` public boundary.
- [x] Failure (not connected, grid-side teleport failure) surfaces a message in the UI rather
      than failing silently.
- [x] Network call runs off the Godot main thread; the UI update on completion is marshalled
      via `CallDeferred`.
- [x] Unit test covering the disconnected-session failure path.
- [x] **World** top-menu entry ("Create Landmark...") opens a dialog matching Firestorm's
      "Landmarken-Details" layout: Name (pre-filled with the current region name), Speicherort
      (destination folder, defaulting to the Landmarks system folder), "Neuen Ordner erstellen"
      (create + select a new subfolder inline), Eigene Notizen (multi-line, becomes the item
      description), OK/Abbrechen.
- [x] OK creates a real landmark asset (region id + local position) and uploads it as a new
      inventory item in the chosen folder via LibreMetaverse's `NewFileAgentInventory` CAP; the
      owner gets full permissions on their own new item (not `Permissions.NoPermissions`, which
      would zero every mask including the owner's).
- [x] Failure surfaces inline in the dialog (and to the Godot console) instead of failing silently;
      success refreshes an already-expanded Landmarks folder in the Inventory panel so the new
      item is visible immediately, not just after a manual collapse/re-expand.
- [x] Unit test covering the disconnected-session failure path for creation too.

## Technical Specs & Affected Files
- `src/SLNG.Net/GridSession.cs` — `TeleportToLandmarkAsync(Guid landmarkAssetId, CancellationToken)`,
  wrapping `AgentManager.TeleportAsync(UUID, CancellationToken)`; subscribes to
  `Self.TeleportProgress` only for the call's duration to capture the final status message.
- `src/SLNG.Net/TeleportResult.cs` — neutral `(bool Success, string Message)` result DTO.
- `src/SLNG.Core/InventoryEntry.cs` — `AssetTypeIds.Landmark` (=3) constant; first consumer to
  branch on `InventoryEntry.AssetType` by name (per that file's own doc comment on when to add
  named constants).
- `app/scripts/UI/InventoryPanel.cs` — context menu gains a "Teleport" entry; item metadata
  extended from `Id,CanCopy,CanModify,CanTransfer` to also carry `AssetType,AssetId` so the menu
  can gate on asset type and the handler has the asset UUID (not just the inventory item UUID)
  to teleport with.
- `src/SLNG.Net/LandmarkCreateResult.cs` — neutral `(bool Success, Guid? ItemId, string Message)`
  result DTO for creation, mirroring `TeleportResult`'s shape.
- `app/scripts/UI/CreateLandmarkWindow.cs` — the dialog itself (`SLNGWindow` subclass), opened
  fresh per use and freed on close, same one-shot pattern as `ItemPropertiesWindow` rather than a
  persistent toggle panel.
- `app/scripts/UI/TopMenu.cs`, `app/scripts/Boot.cs` — new **World** menu (`OnCreateLandmark`)
  opens the dialog; wired instead of a toolbar button per user preference (Firestorm itself puts
  landmark creation in a menu, not the toolbar).
- `app/scripts/UI/InventoryPanel.cs` — `RefreshFolder(Guid)` (new), backed by a `folderId ->
  TreeItem` map (`_folderItems`), so a successful creation can refresh an already-open Landmarks
  folder without the caller needing to walk the `Tree` itself.
- `tests/SLNG.Net.Tests/GridSessionTests.cs` — `TeleportToLandmarkAsync_without_connection_fails_gracefully`,
  `CreateLandmarkHereAsync_without_connection_fails_gracefully`.

## Sub-tasks / Progress
- [x] `GridSession.TeleportToLandmarkAsync` + `TeleportResult`
- [x] `AssetTypeIds.Landmark` constant
- [x] Inventory context menu "Teleport" action, gated to landmark items
- [x] Status-label feedback on failure (reused, same pattern as folder-fetch failures)
- [x] Unit test for the disconnected path
- [x] `GridSession.CreateLandmarkHereAsync` + `CreateInventoryFolder` + `LandmarksFolderId` +
      `LandmarkCreateResult`
- [x] `CreateLandmarkWindow` dialog + **World** menu entry
- [x] Inventory panel auto-refresh on successful creation
- [x] Unit test for the creation disconnected path
- [ ] Deferred to a later `MVP2-2` pass: minimap overlay, full grid map, region search,
      teleport-by-region-name/coordinates, "arrived in new region" UI feedback (no
      `RegionConnected`-style event exists on `GridSession` yet — today's `CurrentRegionName`
      would need to be polled after a successful teleport if a UI wants to show it)
