# [FEAT-INV-15] Rez Objects from Inventory via Drag & Drop

- **Feature ID:** `FEAT-INV-15`
- **Track:** `net/ui`
- **Status:** `🧪 Review`
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
Enable rezzing objects from the inventory into the 3D world via Drag & Drop onto terrain or existing objects/prims,
the way the reference viewer does (`scratch/slviewer/indra/newview/lltooldraganddrop.cpp`). Parcel rights are only a
live hint on the drag card: the viewer does not check them on a drop, the simulator decides.

## Acceptance Criteria
- [x] Dragging an object (`AssetType.Object`) from the inventory panel over the 3D scene shows a valid drop target.
- [x] Dropping on the 3D world raycasts to a surface (terrain or prim) and converts Godot world coordinates to region-local SL coordinates.
- [x] The drag card shows whether a drop at the cursor would rez, and a build-rights HINT (see Review fixes).
- [x] Sends a `RezObject` packet (built in `GridSession.Rez.cs`, not through LibreMetaverse `RequestRezFromInventory`).
- [x] Unit tests for the hint rule, the ray, the remove-item rule and the packet.
- [x] UI localization in `en-US.json` and `de-DE.json`.
- [x] `AppVersion` bumped.
- [ ] In-world test (OpenSim and Second Life): rez on land, on a prim, a no-copy item, a Trash item, a worn item,
      across a region border, with a group active on group-build land.

## Technical Specs & Affected Files
- `src/SLNG.Core/RezObjectResult.cs`: neutral result enum for rez attempts.
- `src/SLNG.Core/RezRay.cs`: the ray fields of the packet (land vs. object) and the in-region test.
- `src/SLNG.Core/RezRules.cs`: `allowCopyBy` and the `RemoveItem` rule.
- `src/SLNG.Core/BuildPermission.cs`: the build-rights hint.
- `src/SLNG.Core/InventoryItemRemovedEvent.cs`: raised when a rez used a no-copy item up.
- `src/SLNG.Net/GridSession.Rez.cs`: `CanBuildAt` (hint), `IsInCurrentRegion`, `CheckRezItem`, `RezObjectFromInventory`, packet builder.
- `app/scripts/ObjectSelectionController.cs`: `TryGetRezTarget` (camera, raw hit point, hit object's full UUID).
- `app/scripts/UI/RezDragPreview.cs`: floating card with live status; used only for rezzable objects.
- `app/scripts/Boot.RezDrop.cs`: `CanDropRez` / `DropRez` (called by the drop target), UI occlusion check, toasts, the `[Rez]` log line.
- `app/scripts/UI/WorldDropTarget.cs`: transparent full-screen control, Boot's first child. `Boot` is mouse-filter Ignore, and Godot's drop search skips Ignore controls, so a drop on the 3D view reached nobody. The target turns Stop only while an inventory-item drag is running, Ignore otherwise.
- `app/scripts/UI/InventoryPanel.cs`: drag card for rezzable items only; refreshes the folder when a no-copy item is used up.
- `app/i18n/en-US.json`, `app/i18n/de-DE.json`: localized toast and card status strings.
- `tests/SLNG.Core.Tests/BuildPermissionTests.cs`, `RezRayTests.cs`, `RezRulesTests.cs`, `tests/SLNG.Net.Tests/RezInventoryTests.cs`.

## Review fixes (v0.27.97-alpha)
Everything below follows `LLToolDragAndDrop` (`lltooldraganddrop.cpp`); line numbers refer to the vendored viewer in `scratch/slviewer`.

- **Own `RezObject` packet** (`dropObject`, :1846-1997). LibreMetaverse's `RequestRezFromInventory` hard-codes
  `RemoveItem=false`, `RayStart==RayEnd`, `BypassRaycast=1`, `RezSelected=true` (5-arg overload) and sends the inventory type in `Type`.
  - `AgentData.GroupID` is the agent's ACTIVE group (`gAgent.getGroupID()`, :1935), not the parcel's.
  - Land hit: `BypassRaycast=1`, `RayStart` = camera, `RayEnd` = the exact surface hit (not lifted), `RayTargetID` = null (`dad3dRezObjectOnLand` passes `bypass_sim_raycast=true`, :2448).
  - Object hit: `BypassRaycast=0`, `RayEnd = hit + (hit - camera)` (the viewer doubles the ray, :1913-1917), `RayTargetID` = the hit prim's full UUID (:1884-1892; `dad3dRezObjectOnObject`, :2526).
  - `RezSelected=false` (it is "are we editing", :1905), `RayEndIsIntersection=false`, `FromTaskID` null.
  - `RemoveItem` when the agent cannot copy the item (`allowCopyBy`, :1874-1878; `LLPermissions::allowOperationBy`, `llpermissions.cpp`:475-499). The item is dropped from the local store at once (:1990-1997) and `GridSession.InventoryItemRemoved` makes the panel redraw its folder.
  - `InventoryData` = `LLViewerInventoryItem::packMessage` (`llviewerinventory.cpp`:539-556): `Type` = asset type, `InvType` = inventory type, `TransactionID` null. `FolderID` is null for an item in the Trash or the Library (:1956-1964). `CRC` is left 0 (LibreMetaverse does the same; the sim reads the item from its own inventory).
- **No client-side build-rights block.** The viewer does not check parcel rights on a drop. `RezObjectResult.NoBuildRights` and the `ui.rez.no_permission` toast are gone; `RezObjectFromInventory` always sends.
- **Build-rights hint** (drag card only): `LLViewerParcelMgr::allowAgentBuild()` (`llviewerparcelmgr.cpp`:688-700) =
  `LLParcel::allowModifyBy(agent, active group)` (`llparcel.cpp`:368-395) or `isParcelOwnedByAgent(parcel, GP_LAND_ALLOW_CREATE)` (`llviewerparcelmgr.cpp`:2638-2666).
  Public land and the owner may build; "Build: Everyone" lets everyone; "Build: Group" only if the ACTIVE group is the parcel's group (membership in another group does not count); group-owned land needs the `GP_LAND_ALLOW_CREATE` power (`roles_constants.h`:101, bit 25; LibreMetaverse `GroupPowers.AllowRez`) in the owning group. The powers come from the membership data `OnCurrentGroups` already receives.
- **Worn items are refused** (`isWearingAttachment` -> `ACCEPT_NO`, :2408 and :2469): `RezObjectResult.Worn`, card status and toast. A link is resolved first and the resolved item id is tested (`LiveWornItemIds`).
- **Current region only.** A drop whose region-local x/y is outside the region the agent is in is not sent: card status and toast.
- **Avatars are not a rez target** (`pick`, :840-860: an attachment counts as its avatar, an avatar gets `ACCEPT_NO`). A drop on an avatar or a worn item is treated as "no surface".
- **Cursor:** `_CanDropData` returns false whenever the drop cannot rez (not connected, not an object, worn, over a window, no surface, other region), so Godot shows its forbidden cursor. The global `CanDrop`/`Drag` custom cursor override in `CursorManager` is removed (it replaced the drop cursor for every drag and drop in the UI). It returns true when a rez would be sent, also when the hint says "no build rights".
- **Drag card scope:** `RezDragPreview` only for a rezzable object, with the icon its inventory row shows; every other drag keeps the plain `Label`.
