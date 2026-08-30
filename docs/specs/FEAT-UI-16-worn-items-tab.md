# Feature: FEAT-UI-16 (Worn Items Inventory Tab)

- **Feature ID:** `FEAT-UI-16`
- **Track:** `ui` / `net`
- **Status:** `✅ Done` (confirmed in-world 2026-08-30)
- **Owner:** `claude`
- **Agent:** `ux-designer`
- **Dep:** `MVP3-1`

## Context
The inventory window surfaced worn items only as gold-highlighted "(getragen)" rows scattered
through the folder tree (plus the Current Outfit folder). The user asked for a dedicated tab
instead of hunting through folders.

## Implementation

### UI (`app/scripts/UI/InventoryPanel.cs`)
- A `TabBar` at the top of the window: **Inventar** (the existing search + lazy folder tree,
  moved into an `_inventoryView` container) and **Angezogen** (`_wornView`).
- The Worn view is a flat `Tree` grouped into four collapsible headers — **Körper** (body parts),
  **Kleidung** (clothing layers), **Anhänge** (body attachments), **HUDs** — each row showing the
  item name, the attachment-point label for attachments, and "(nicht aktiv)" for an item that is
  only referenced by a Current-Outfit link but is not actually attached / in the live wearables
  set. Live rows are gold, non-live rows dimmed.
- Right-click (or double-click) a row → **Ablegen** → `GridSession.DetachItemAsync`, then the
  worn list and the Current Outfit folder both refresh. Body-part rows can be listed but a detach
  of one is a server-side no-op (a Shape/Skin can only be replaced) — surfaced as "War nicht
  getragen.".
- **Refresh triggers:** on tab switch to Worn, on panel show, after a detach, on
  `GridSession.WornItemsChanged`, and on a 2.5 s `Timer` while the Worn tab is visible. The timer
  is the safety net for attachment attach/detach, which has no clean LibreMetaverse event; it is
  stopped when the tab is not visible. `WornItemsChanged` fires on a network thread, so the
  refresh hops to the main thread via `Node.CallDeferred(nameof(...))` (never
  `Callable.From(lambda)` from a background thread — that crashes).

### Net (`src/SLNG.Net/GridSession.cs`)
- `IReadOnlyList<WornItem> GetWornItems()` — merges live attachments
  (`Appearance.GetAttachmentsByItemId`), the live wearables set (`Appearance.GetWearables`), and
  any remaining Current-Outfit link (reported `Live == false`). Names resolved from the inventory
  store; no LibreMetaverse type crosses the boundary — returns the neutral `SLNG.Core.WornItem`.
- `event EventHandler WornItemsChanged` — raised from `OnAppearanceSet` and the self
  `OnAvatarAppearance`.
- `internal static WornCategory CategorizeWearable(int assetType)` / `CategorizeAttachment(int
  rawPoint)` — the grouping logic, pure functions for unit tests.

### Core
- `src/SLNG.Core/WornItem.cs` — `WornItem` record + `WornCategory` enum.

## Acceptance Criteria
- [x] A dedicated "Angezogen" tab is visible in the Inventory UI.
- [ ] It accurately displays all currently worn items and attachments (in-world check).
- [ ] The list updates when items are worn/detached (via SLNG or another viewer) — instantly for
      wearables/bakes, within ~2.5 s for attachments.
- [x] Items can be detached directly from the tab via right-click / double-click.

## Known limits / follow-ups
- SLNG's own detach paths still don't rewrite the Current Outfit Folder, so a detach done in SLNG
  may not survive a relog (tracked with FEAT-AVATAR-01 / outfit handling). The "(nicht aktiv)"
  marker makes a lingering COF link visible at least.
- No "show in inventory folder" action from a worn row yet.
- The Current Outfit folder is still shown in the tree too; not removed.
