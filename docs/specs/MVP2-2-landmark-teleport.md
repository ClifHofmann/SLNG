# [MVP2-2] Landmark Teleport (Phase 1 of World Map, Minimap & Teleport)

- **Feature ID:** `MVP2-2`
- **Track:** `net` / `ui`
- **Status:** `🧪 Review`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
`MVP2-2` ("World Map, Minimap & Teleport") bundles minimap overlay, full grid map, region
search, and teleport-to-landmark/coords. This pass ships the smallest independently useful
slice: **teleporting via a landmark inventory item**, since it needs no new map UI and no
grid-coordinate math — LibreMetaverse resolves a landmark's destination server-side from its
asset UUID. Minimap/world-map/search/teleport-by-coordinates remain out of scope for this pass
and stay open under `MVP2-2`.

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
- `tests/SLNG.Net.Tests/GridSessionTests.cs` — `TeleportToLandmarkAsync_without_connection_fails_gracefully`.

## Sub-tasks / Progress
- [x] `GridSession.TeleportToLandmarkAsync` + `TeleportResult`
- [x] `AssetTypeIds.Landmark` constant
- [x] Inventory context menu "Teleport" action, gated to landmark items
- [x] Status-label feedback on failure (reused, same pattern as folder-fetch failures)
- [x] Unit test for the disconnected path
- [ ] Deferred to a later `MVP2-2` pass: minimap overlay, full grid map, region search,
      teleport-by-region-name/coordinates, "arrived in new region" UI feedback (no
      `RegionConnected`-style event exists on `GridSession` yet — today's `CurrentRegionName`
      would need to be polled after a successful teleport if a UI wants to show it)
