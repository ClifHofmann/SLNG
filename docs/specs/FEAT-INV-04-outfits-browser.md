# Feature: FEAT-INV-04 (Outfits browser — save / wear saved outfits)

- **Feature ID:** `FEAT-INV-04`
- **Track:** `net` / `ui`
- **Status:** `🚧 In Progress` (unblocked slice — full "wear outfit" waits on FEAT-AVATAR-01 Phase 2)
- **Owner:** `claude`
- **Agent:** `ux-designer`
- **Dep:** `FEAT-UI-16`, `MVP3-1`

## Context
Firestorm-style **Outfits**: the `#Outfits` system folder (`FolderType.MyOutfits`), each direct
subfolder being one saved outfit (a set of inventory links). SLNG had no UI for it.

## Scope split

**This slice (unblocked, no rebake):**
- Browse the saved outfits (`#Outfits` subfolders).
- **Save the current outfit** as a new `#Outfits` subfolder — links to everything currently worn
  (body parts, wearables, attachments). Pure inventory writes.
- **Wear the attachment part** of a saved outfit (Objects / HUDs) via the existing `AttachItemAsync`
  object path.

**Deferred to FEAT-AVATAR-01 Phase 2:**
- Applying a saved outfit's **wearables** (Shape / Skin / Clothing) and a full **Replace Outfit** —
  those go through `ReplaceOutfitAsync` → the rebake that flattened the avatar on 2026-08-29.

## Implementation

### Net (`src/SLNG.Net/GridSession.cs`)
- `Guid? MyOutfitsFolderId` — `FindFolderForType(FolderType.MyOutfits)`; `null` if the grid has no
  `#Outfits` folder.
- `Task<IReadOnlyList<OutfitEntry>> GetSavedOutfitsAsync()` — the direct subfolders of `#Outfits`,
  as neutral `SLNG.Core.OutfitEntry(FolderId, Name)`.
- `Task<Guid?> SaveCurrentOutfitAsync(string name)` — `CreateFolder(#Outfits, name)`, then one
  `Inventory.CreateLinkAsync` per `GetWornItems()` entry with `Live == true` (inv-type derived from
  the category: BodyPart/Clothing → Wearable, else Object). Returns the new folder id.
- `Task<int> WearOutfitAttachmentsAsync(Guid outfitFolderId)` — fetches the folder's links and
  calls `AttachItemAsync` for each whose target is `AssetType.Object`; wearables are skipped and
  counted. Returns how many attach calls were sent.

### Core
- `src/SLNG.Core/WornItem.cs` — add `OutfitEntry(Guid FolderId, string Name)`.

### UI (`app/scripts/UI/InventoryPanel.cs`)
- A third tab **Outfits** next to Inventar / Angezogen.
- Top row: a name field + **💾 Speichern** → `SaveCurrentOutfitAsync`, then refresh the list.
- List of saved outfits; right-click / double-click → **Anziehen (nur Anhänge)** →
  `WearOutfitAttachmentsAsync`, with a hint that clothing/body parts follow with Phase 2.

## Acceptance criteria
- [ ] The Outfits tab lists the `#Outfits` subfolders.
- [ ] "Speichern" creates a new `#Outfits` subfolder containing links to everything currently worn
      (visible in Firestorm as a normal saved outfit).
- [ ] "Anziehen (nur Anhänge)" attaches the outfit's objects; the avatar's clothing is untouched
      and the UI says so.
- [ ] No-connection: every path no-ops / returns empty without throwing.

## Out of scope
- Full "Wear Outfit" / "Replace Outfit" incl. wearables — FEAT-AVATAR-01 Phase 2.
- Rename / delete a saved outfit, outfit thumbnails, "add to outfit".
