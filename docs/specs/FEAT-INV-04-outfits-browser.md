# Feature: FEAT-INV-04 (Outfits browser — save / wear saved outfits)

- **Feature ID:** `FEAT-INV-04`
- **Track:** `net` / `ui`
- **Status:** `✅ Done` (unblocked slice — full clothing/body "wear outfit" waits on FEAT-AVATAR-01 Phase 2; confirmed in-world 2026-08-30)
- **Owner:** `claude`
- **Agent:** `ux-designer`
- **Dep:** `FEAT-UI-16`, `MVP3-1`

## Context
Firestorm-style **Outfits**: the `#Outfits` system folder (`FolderType.MyOutfits`), each direct
subfolder being one saved outfit (a set of inventory links). SLNG had no UI for it.

## Scope split

**This slice (no rebake):** browse, save-current, and all attachment-scoped apply/edit actions.

**Deferred to FEAT-AVATAR-01 Phase 2:** applying a saved outfit's **wearables** (Shape / Skin /
Clothing) and a full **Replace Outfit** — those go through `ReplaceOutfitAsync` → the rebake that
flattened the avatar on 2026-08-29.

## Implementation

### Net (`src/SLNG.Net/GridSession.cs`)
- `Guid? MyOutfitsFolderId` — `FindFolderForType(FolderType.MyOutfits)`; `null` if the grid has no
  `#Outfits` folder.
- `GetSavedOutfitsAsync()` → `IReadOnlyList<OutfitEntry>` — the `#Outfits` subfolders. Flags the
  **currently-worn** one: first via the folder-link the Current Outfit Folder carries to it
  (SL/Firestorm mechanism); if absent (common on OpenSim), falls back to "the fully-worn outfit
  with the most items" (parallel content fetch, capped at 60). Logs `[SavedOutfits] N outfits,
  active=…`.
- `SaveCurrentOutfitAsync(name)` → `Guid?` — reuses a same-name subfolder (no duplicate) or
  creates one, then links every live worn item not already linked. Names resolved first
  (`RequestFetchInventory` + a short wait) so links never get the "Link" placeholder name.
- `AddCurrentToOutfitAsync(folderId)` / `ReplaceOutfitWithCurrentAsync(folderId)` — edit a saved
  outfit: add the missing worn items, or trash all its links and re-link the whole worn set.
- `WearOutfitAttachmentsAsync(folderId)` → attaches the outfit's `AssetType.Object` links
  (additive). `ReplaceWornWithOutfitAttachmentsAsync(folderId)` → detaches every worn attachment
  the outfit doesn't contain, attaches its objects that aren't worn, and writes the COF
  folder-link (`SetCurrentOutfitLinkAsync`) so the "worn" marker follows. `RemoveOutfitFromWornAsync`
  → detaches the outfit's currently-worn attachments. All three leave Clothing/Bodypart alone.
- `GetOutfitContentsAsync(folderId)` → `IReadOnlyList<WornItem>` — an outfit's links resolved to
  their **target** name / type / category, with `Live` = worn-right-now, de-duped.
- `RenameOutfitAsync(folderId, name)` (`Inventory.UpdateFolderProperties`) /
  `DeleteOutfitAsync(folderId)` (move folder to Trash).
- Shared: `GetWornItemsWithNamesAsync` / `GetOutfitTargetIdsAsync` / `LinkWornIntoAsync`.

### Core
- `src/SLNG.Core/WornItem.cs` — `OutfitEntry(Guid FolderId, string Name, bool IsCurrent)`.

### UI (`app/scripts/UI/InventoryPanel.cs`)
- A third tab **Outfits**. **Ctrl+O** opens the window straight onto it (`OpenOnOutfits`).
- Each outfit row is collapsible (lazy-loads its contents on expand, with type icons 🧍/👕/📦/🖥
  and gold for items also worn now). The currently-worn outfit shows a ✅ prefix + gold text.
- Name field + **💾 Speichern** — save as new (or top-up a same-name outfit). The button doubles
  as **✏️ Umbenennen** while a rename is armed (Tree cell-editing needs keyboard focus this tree
  deliberately doesn't take).
- Right-click menu, mirroring Firestorm:
  - *Aktuelles Outfit ersetzen* → `ReplaceWornWithOutfitAttachmentsAsync`
  - *Zu aktuellem Outfit hinzufügen* → `WearOutfitAttachmentsAsync`
  - *Von aktuellem Outfit entfernen* → `RemoveOutfitFromWornAsync`
  - *Outfit neu benennen* → arms the rename in the name field
  - *Outfit speichern (= akt. Getrage)* → `ReplaceOutfitWithCurrentAsync`
  - *Outfit löschen* → `DeleteOutfitAsync`

## Acceptance criteria
- [x] The Outfits tab lists the `#Outfits` subfolders; Ctrl+O opens it.
- [x] Each outfit expands to show its contents with resolved names/types.
- [x] "Speichern" creates a `#Outfits` subfolder of links to everything worn (a normal saved
      outfit in Firestorm); re-saving the same name doesn't duplicate.
- [x] The currently-worn outfit is marked.
- [x] The apply actions (ersetzen / hinzufügen / entfernen) change the avatar's **attachments**
      only; clothing/body untouched, and the UI says so.
- [x] Rename / delete work; delete is recoverable (Trash).
- [x] No-connection: every path no-ops / returns empty without throwing (unit tests).

## Out of scope
- Full "Wear Outfit" incl. wearables / a real Replace-Outfit — FEAT-AVATAR-01 Phase 2.
- Outfit thumbnails ("Abbildung"), favourites.
