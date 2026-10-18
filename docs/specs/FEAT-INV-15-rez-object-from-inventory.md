# [FEAT-INV-15] Rez Objects from Inventory via Drag & Drop with Permission Checks

- **Feature ID:** `FEAT-INV-15`
- **Track:** `net/ui`
- **Status:** `✅ Done`
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
Enable rezzing objects from the inventory into the 3D world via Drag & Drop onto terrain or existing objects/prims.
Rezzing must respect parcel and sim permissions (owner permissions, public build/sandbox, and group permissions).

## Acceptance Criteria
- [x] Dragging an object (`AssetType.Object`) from the inventory panel over the 3D scene shows a valid drop target.
- [x] Dropping on the 3D world raycasts to surface (terrain or prim), calculates the rez point above the hit normal, and converts Godot world coordinates to region-local SL coordinates.
- [x] Evaluates build permissions on the parcel:
  - Allowed if avatar is the parcel owner.
  - Allowed if parcel allows public creation (`ParcelFlags.CreateObjects` / `ParcelOptions.BuildEveryone`).
  - Allowed if parcel is group-owned or allows group creation (`ParcelFlags.CreateGroupObjects` / `ParcelOptions.BuildGroup`) and the user has membership/rez rights in that group.
- [x] If permission is denied, drop is refused or feedback toast is displayed ("Keine Baurechte auf dieser Parzelle" / "No permission to build on this parcel").
- [x] Sends `RezObject` packet through LibreMetaverse `RequestRezFromInventory`.
- [x] Unit tests for permission evaluation and rez invocation.
- [x] UI localization in `en-US.json` and `de-DE.json`.
- [x] `AppVersion` bumped.

## Technical Specs & Affected Files
- `src/SLNG.Core/RezObjectResult.cs`: neutral result enum for rez attempts.
- `src/SLNG.Net/GridSession.Rez.cs`: `CanBuildAt`, `EvaluateParcelBuildRights`, and `RezObjectFromInventory`.
- `app/scripts/ObjectSelectionController.cs`: exposes `TryGetWorldRezPoint` for raycasting from mouse position.
- `app/scripts/UI/RezDragPreview.cs`: floating cursor preview card with dynamic status and permission highlighting.
- `app/scripts/Input/CursorManager.cs`: custom rez/box icon cursor for CanDrop/Drag.
- `app/scripts/Boot.cs`, `app/scripts/Boot.RezDrop.cs`: `_CanDropData` and `_DropData` implementation on `Boot`, with UI occlusion check and toast notifications.
- `app/i18n/en-US.json`, `app/i18n/de-DE.json`: localized toast and cursor status strings.
- `tests/SLNG.Net.Tests/RezInventoryTests.cs`: unit tests for permissions and rez logic.
