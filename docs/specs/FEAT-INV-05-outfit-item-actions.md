# [FEAT-INV-05] Per-item actions in the Outfits view

- **Feature ID:** `FEAT-INV-05`
- **Track:** `ui` / `net`
- **Status:** `✅ Done` — implemented `v0.20.60`; confirmed in-world 2026-09-07: the per-item context menu (Anziehen / Ausziehen / Aus diesem Outfit entfernen) works on a single item row.
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

## Implementation (`v0.20.60-alpha`)

The reason right-clicking an item did nothing was one line: outfit-content rows were built with
`SetMetadata(0, "")` and `SetSelectable(0, false)` -- deliberately display-only -- and
`OnOutfitsGuiInput` bails out when the metadata does not parse as a folder Guid. So the handler ran
and returned before ever reaching a menu.

- **Row encoding.** Content rows now carry `item:{itemId}` and are selectable. The prefix is what
  keeps the two row kinds apart: a folder row's metadata stays a bare Guid, so neither menu can be
  handed the other's id -- the exact mix-up the spec was filed about.
- **`_outfitItemMenu`** (Anziehen / Ausziehen / --- / Aus diesem Outfit entfernen).
  `OnOutfitsGuiInput` routes by row kind; the outfit-folder menu is untouched.
- **Ids captured at popup time**, not read back from the tree selection when an entry is clicked --
  a refresh in between would otherwise run the action against whatever happened to be selected.
- **Anziehen / Ausziehen** go through the existing `AttachItemAsync` / `DetachItemAsync`, which
  already branch on `ClassifyItem`, so a Clothing/Bodypart layer takes the wearable path and an
  Object the attachment path without the panel having to know the difference.
- **`GridSession.RemoveItemFromOutfitFolderAsync(outfitFolderId, itemId)`** deletes the link(s) to
  that item in that outfit and nothing else -- not the inventory item, not the same item's link in
  another outfit, not its Current-Outfit link (taking a thing off is a different verb, offered
  separately). `RemoveItemsAsync`, not `MoveItem -> Trash`, which 400s on SL and reappears on the
  next refetch.
- **Only links are ever deleted.** An outfit folder should hold nothing else, but if a real item
  has been dropped into one, deleting it would destroy inventory over a menu entry that promises to
  edit an outfit. A non-link match is refused and logged instead.
- Every result is marshalled back through `RunOnMainThread` (BUG-INV-01), and the affected outfit's
  contents reload so the gold "getragen" marker and the membership are both current.

### Tests

`SLNG.Net.Tests/OutfitLinkRemovalTests` (7) pin the decision half, `SelectOutfitLinksToRemove`,
which was extracted specifically so it could be tested without a grid. Its failure modes are silent
and point in opposite directions: delete too little and the item stays in the outfit while the UI
claims success; delete the wrong entry and real inventory is gone. Covered: the matching link is
selected, ALL duplicate links to the same item are (a survivor reads as "nothing happened"), links
to other items never are, a matching real item is reported but never selected, folders are ignored
even when the id matches, and `Guid.Empty` matches nothing -- otherwise "remove from outfit" on a
row with no id would delete every broken link in the folder.

**Not yet verified in-world.**

