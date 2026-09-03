# [FEAT-INV-05] Per-item actions in the Outfits view

- **Feature ID:** `FEAT-INV-05`
- **Track:** `ui` / `net`
- **Status:** `⏸️ Pending`
- **Owner:** `claude`
- **Depends on:** `FEAT-INV-04` (Outfits browser), `MVP3-1` (wear/detach), `BUG-INV-01`
- **Requested:** *"wenn man im outfit view ist sollte man auch die normalen aktionen machen
  können mit den einzelnen items — anziehen, ausziehen + aus dem outfit entfernen."*

## What's there today

`InventoryPanel.cs`'s "Outfits" tab (`_outfitsTree`) already **expands** an outfit folder to
list its individual items (`_loadedOutfitFolders`, `OnOutfitItemCollapsed`,
`GetOutfitContentsAsync`). But `OnOutfitsGuiInput` / `OnOutfitsMenuPressed` treat **every**
right-clicked row's metadata as a *folder* id, and `_outfitsMenu` only offers whole-outfit
operations (replace worn, add to worn, remove from worn, rename, save, delete). Right-clicking an
item row runs those against the item id → wrong / no-op.

## Scope

1. In `OnOutfitsGuiInput`, distinguish a **folder** row (an outfit) from an **item** row (a piece
   inside an expanded outfit) — the item rows carry a `WornItem`-shaped metadata; folder rows a
   bare folder id. Follow whatever encoding `Populate`/the outfit-expand path already writes.
2. A second `PopupMenu` for item rows:
   - **Anziehen** → `_session.AttachItemAsync(itemId)` (or `WearWearableAsync` for a
     Clothing/Bodypart layer — `DetachItemAsync` already branches on `ClassifyItem`, mirror that).
   - **Ausziehen** → `_session.DetachItemAsync(itemId)`.
   - **Aus diesem Outfit entfernen** → remove *this* item's link from *this* outfit folder (not
     the Current Outfit). Needs a small `GridSession` method, e.g.
     `RemoveItemFromOutfitFolderAsync(Guid outfitFolderId, Guid itemId)` — find the link in that
     folder whose target is `itemId` and `RemoveItemsAsync([linkId])` (same durable-delete path
     `BUG-INV-01`/`v0.20.33` switched the COF cleanup to; `MoveItem → Trash` 400s on SL).
3. Refresh the affected outfit folder + the "Angezogen" list after any of the three, marshalled
   the **safe** way (`CallDeferred(nameof(...))`, never `Callable.From(lambda).CallDeferred()`
   from a worker thread — the outfit methods in this file still use the anti-pattern, `BUG-INV-01`).
4. Reuse the worn-state marker work from `BUG-INV-01` so an item already worn shows it here too.

## Acceptance

- Right-click an item inside an expanded outfit → a menu with Anziehen / Ausziehen / Aus diesem
  Outfit entfernen, each doing exactly that and nothing to the other items.
- Right-click the outfit folder itself → unchanged (the existing whole-outfit menu).
- "Aus diesem Outfit entfernen" is durable (survives a relog / refetch) and never touches the
  linked inventory item, only the link in that outfit folder.
- Tests for the new `GridSession` method where there's a surface (`SLNG.Net.Tests`).
