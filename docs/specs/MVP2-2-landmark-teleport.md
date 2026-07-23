# [MVP2-2] Landmark Teleport & Creation (Phase 1 of World Map, Minimap & Teleport)

- **Feature ID:** `MVP2-2`
- **Track:** `net` / `ui`
- **Status:** `✅ Done` (landmark-teleport slice) — teleport execution was fixed by gemini after the
  round-4 handoff below. **2026-07-23 regression fix (claude):** user-reported "clicking Teleport
  does nothing." Root cause: row metadata grew from 7 to 8 comma-separated fields when
  `IsLink`/`LinkTargetId` were added (round 2/4 fixes below), but the context menu's `id == 5`
  handler still gated on `parts.Length != 7` exactly, so it silently `return`ed before ever calling
  `TeleportToLandmarkAsync` — no exception, no console line, matching the reported symptom exactly.
  Fixed in `InventoryPanel.TryTeleportFromItem` (`parts.Length < 7`, so a future field addition
  degrades gracefully instead of re-breaking this the same way). Also added while in there:
  double-click-to-teleport (`Tree.ItemActivated`) and a 🌐 prefix on landmark rows so they're
  visually recognizable without opening the context menu.
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
- [x] **Selecting it teleports the agent.** `GridSession.TeleportToLandmarkAsync` tries direct landmark teleport first and falls back to fetching/decoding the landmark asset (`RegionID` + `Position`) and resolving the region handle when the grid's UDP `TeleportLandmarkRequest` fails (e.g. for newly created or unindexed landmark assets). Also improved status error reporting fallback to `AgentManager.TeleportMessage`.
- [x] Failure (not connected, grid-side teleport failure) surfaces a message in the UI rather
      than failing silently.
- [x] Network call runs off the Godot main thread; the UI update on completion is marshalled
      via `CallDeferred`.
- [x] Unit test covering the disconnected-session failure path.
- [x] **World** top-menu entry ("Create Landmark...") opens a dialog matching Firestorm's
      "Landmark Details" layout (translated to English for consistency with the rest of the
      client -- the rest of the UI has no i18n framework and is English-only): Name (pre-filled
      with the current region name), Location (destination folder, defaulting to the Landmarks
      system folder), "New Folder" (create + select a new subfolder inline), Notes (multi-line,
      becomes the item description), OK/Cancel.
- [x] OK creates a real landmark asset (region id + local position) and uploads it as a new
      inventory item in the chosen folder via LibreMetaverse's `NewFileAgentInventory` CAP; the
      owner gets full permissions on their own new item (not `Permissions.NoPermissions`, which
      would zero every mask including the owner's).
- [x] Failure surfaces inline in the dialog (and to the Godot console) instead of failing silently;
      success refreshes an already-expanded Landmarks folder in the Inventory panel so the new
      item is visible immediately, not just after a manual collapse/re-expand.
- [x] Unit test covering the disconnected-session failure path for creation too.
- [x] **Live-test fix round 1 (2026-07-21):** the freshly created landmark showed up in the
      Inventory panel but "Teleport" stayed disabled on it. Root cause: `FolderContentsAsync`'s
      server-side descendants listing can briefly report a just-created item's `asset_id` as
      `Guid.Empty` (an indexing lag, confirmed against real LibreMetaverse 3.0.0 source), and the
      empty-guid guard on "Teleport" is load-bearing, not cosmetic -- `TeleportLandmarkRequest`'s
      `LandmarkID` field is documented server-side as "use LLUUID::null for home", so relaxing
      that guard would silently teleport home instead of failing safely. Fixed by threading the
      asset id already known from `CreateLandmarkHereAsync`'s own response (never subject to the
      fetch's lag) through `RefreshFolder` → `Populate`, overriding just that one row's asset id
      instead of trusting the immediate re-fetch for it.
- [x] **Live-test fix round 2:** that fix only helped if the Landmarks folder was already expanded
      at creation time (`RefreshFolder` no-ops otherwise). Split the concerns: "Teleport" is now
      gated on `AssetType == Landmark && !IsLink` alone (added `IsLink` to the row's metadata,
      instead of inferring "is a link" from asset-id emptiness), so a landmark with a momentarily
      unresolved asset id is still clickable; the actual empty-guid safety check moved to
      click-time in `InventoryPanel.TeleportAsync`, which re-fetches the parent folder fresh and
      looks the item up again if its stored asset id is empty, self-healing instead of requiring
      a manual folder refresh.
- [x] **Live-test fix round 2 (same pass), unrelated UI bugs also reported and fixed:** windows
      didn't raise above each other on click, and clicks/wheel-scroll on blank window areas leaked
      through to whatever was rendered behind (CameraHUD, 3D viewport). Added `SLNGWindow._Input`
      raise-to-front (using `GuiGetHoveredControl()`, walking the ancestor chain so a window
      nested inside another, like `ItemPropertiesWindow` inside `InventoryPanel`, also raises its
      true top-level parent) and `MouseFilter = Stop` on the window root/background panel.
- [x] **Live-test fix round 3:** the round-2 `MouseFilter.Stop` change did NOT actually stop
      wheel-zoom-while-scrolling-Inventory or click-through to world-object-selection in practice
      — rather than continuing to guess at Godot's Control consumption/propagation semantics,
      switched to a direct, unambiguous check: `AvatarController`'s wheel-zoom handler and
      `ObjectSelectionController._UnhandledInput` (which had NO ui-guard at all) now both bail out
      whenever `GetViewport().GuiGetHoveredControl() != null`. Also added `GD.Print`/`GD.PrintErr`
      diagnostics around the Teleport gating/self-heal path, since the underlying bug was still
      reproducing after two rounds and needed real console data instead of a third blind guess.
- [x] **Live-test fix round 4 (data-driven from round 3's diagnostics):** console output showed
      the freshly-created landmark's row had the *correct* patched asset id but `AssetType == 0`
      (not 3/Landmark) — proving the same server-side indexing lag that affected `asset_id` in
      round 1 ALSO affects `AssetType`, which had never been patched the same way. Fixed by also
      overriding `AssetType` (hardcoded to `Landmark`) in `Populate()`'s known-item branch.
      Also found a third, previously-unaudited click handler explaining "clicks affect HUDs":
      `AvatarRenderer.TryClickHud` (touches whatever worn HUD-attachment prim, e.g. a body-shape
      HUD, sits under a left-click) runs off `_Input` — which fires regardless of GUI consumption,
      so no `MouseFilter` change could ever have stopped it — and had zero ui-guard. Added the
      same `GuiGetHoveredControl()` check.
- [x] **Confirmed by live test (2026-07-21, after round 4):** all UI/window/input bugs above are
      fixed, and "Teleport" now correctly enables on a freshly created landmark. **Still broken:**
      clicking the now-enabled "Teleport" action does not actually teleport the agent. This is the
      open issue this spec hands off — see **Open Issue** below.

## Open Issue (handed off to gemini, 2026-07-21)

**Symptom:** "Teleport" is enabled and clickable on a landmark (freshly created or pre-existing —
not yet determined which), the click is registered (no exception, no obviously-stuck UI), but the
agent does not arrive at the landmark's location. Not yet confirmed whether:
- `GridSession.TeleportToLandmarkAsync` returns `Success = false` with a message (check the
  `[Teleport] result success=... message='...'` console line — diagnostics are already in place,
  see `app/scripts/UI/InventoryPanel.cs` `TeleportAsync`), or
- it returns `Success = true` but nothing actually happens in-world (a false positive from
  LibreMetaverse's `TeleportAsync`/`TeleportProgress` handling), or
- the call hangs/times out silently (no `CancellationToken` is passed from the UI layer today —
  `GridSession.TeleportToLandmarkAsync`'s `ct` parameter defaults to `default`, so a sim that never
  raises `TeleportProgress` with a terminal status would leave the awaited `Task<bool>` pending
  indefinitely with no user-visible failure).

**First step: get the actual console output.** The next live test should capture the three
`[Teleport]` log lines (`item=... assetId=... parentFolder=...`, the re-resolve line if it fires,
and `result success=... message='...'`) and the `[Inventory] context menu for ...` line, from
either stdout or `%APPDATA%\Godot\app_userdata\SLNG\logs\`. That alone should distinguish "grid
rejected it" (message tells you why) from "grid never responded" (hangs) from "reported success
but nothing happened" (LibreMetaverse/protocol bug).

**Hypotheses not yet ruled out, roughly in order of likelihood:**
1. **Grid-specific rejection.** This was tested against what looks like the real Second Life main
   grid (landmark names like "LBSA Plaza" seen in an earlier screenshot). SL's teleport handling
   may have server-side checks LibreMetaverse's `TeleportAsync(UUID, ct)` doesn't account for
   (e.g. a cooldown, a permission/ban check, region capacity) that surface as a `TeleportProgress`
   `Failed` status with a message — which the current code already captures and surfaces, so check
   that message first.
2. **The asset upload itself may not be fully "real."** `CreateLandmarkHereAsync` builds the
   landmark asset client-side (`AssetLandmark.Encode()`) from the agent's *current* region+position
   at the moment of creation and uploads the raw bytes — this was never independently verified
   against a real SL-created landmark's asset bytes (only against LibreMetaverse's own `Encode()`
   implementation, which was trusted at face value). If `RegionID`/`Position` are wrong, stale, or
   the format doesn't match what the sim expects for `TeleportLandmarkRequest.LandmarkID` to
   resolve, teleport could legitimately fail server-side even with a well-formed asset id.
3. **`TeleportAsync`'s `TeleportProgress` correlation may be unreliable in this codebase's usage.**
   `GridSession.TeleportToLandmarkAsync` subscribes/unsubscribes `Self.TeleportProgress` around a
   single call, but if two teleport attempts (or a teleport plus some unrelated `TeleportProgress`-
   raising event) overlap, `lastMessage` could reflect the wrong attempt. Unlikely on a single
   click, but worth ruling out if retrying doesn't reproduce consistently.
4. **Not actually reaching the network call at all.** Re-verify (with the diagnostics) that
   `OnContextMenuIdPressed`'s `id == 5` branch in `InventoryPanel.cs` is really being hit and that
   `_session` is non-null at that point — low probability given the enable/disable gating already
   confirmed correct, but cheap to rule out first.

**Where to look:**
- `src/SLNG.Net/GridSession.cs` — `TeleportToLandmarkAsync` (~line 632) and `CreateLandmarkHereAsync`
  (~line 808).
- `app/scripts/UI/InventoryPanel.cs` — `OnContextMenuIdPressed`'s `id == 5` branch and `TeleportAsync`.
- LibreMetaverse 3.0.0 source (`gh api repos/cinderblocks/LibreMetaverse`, tag `v3.0.0`) —
  `LibreMetaverse/Agent/AgentManager.Teleporting.cs` (`TeleportAsync(UUID, ct)`,
  `RequestTeleport(UUID)`) and `LibreMetaverse/Agent/AgentManager.PacketHandlers.cs` (how
  `TeleportProgress`/`TeleportFinish` packets actually resolve the awaited Task) — this file was
  read for the `LandmarkID` semantics (see round-1 note above) but not for the full success/finish
  packet-handling path, which is where this bug most likely lives.
- `data/message_template.msg` in the same repo — the raw wire definitions for
  `TeleportLandmarkRequest` / `TeleportFinish` / `TeleportFailed`, if a lower-level look is needed.

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
- `app/scripts/UI/SLNGWindow.cs` — raise-to-front (`_Input` + `GuiGetHoveredControl()`, walks the
  ancestor chain) and `MouseFilter = Stop` on the window root/background panel. Shared base class
  for every floating window, not landmark-specific, but the fixes landed in this same live-test
  cycle.
- `app/scripts/AvatarController.cs` — wheel-zoom handler's ui-guard now also checks
  `GuiGetHoveredControl()`, not just LineEdit/TextEdit focus.
- `app/scripts/ObjectSelectionController.cs` — world click/raycast (`_UnhandledInput`) gained the
  same `GuiGetHoveredControl()` guard (had none before).
- `app/scripts/AvatarRenderer.cs` — `TryClickHud` (worn HUD-attachment touch, driven by `_Input`)
  gained the same guard.

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
- [x] Live-test rounds 1–4: asset-id lag, asset-type lag, window raise-to-front, window/world/HUD
      click-through and wheel-zoom-through-Inventory — all confirmed fixed by live test
- [ ] **Open, handed to gemini:** actual teleport execution — see **Open Issue** above
- [ ] Deferred to a later `MVP2-2` pass: minimap overlay, full grid map, region search,
      teleport-by-region-name/coordinates, "arrived in new region" UI feedback (no
      `RegionConnected`-style event exists on `GridSession` yet — today's `CurrentRegionName`
      would need to be polled after a successful teleport if a UI wants to show it)
