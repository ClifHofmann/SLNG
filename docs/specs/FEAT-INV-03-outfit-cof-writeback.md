# Feature: FEAT-INV-03 (Outfit / COF write-back on detach + cleanup)

- **Feature ID:** `FEAT-INV-03`
- **Track:** `net` / `ui`
- **Status:** `🧪 Review` (implemented `v0.11.11-alpha`, needs in-world check)
- **Owner:** `claude`
- **Agent:** `protocol-re`
- **Dep:** `FEAT-UI-16`, `MVP3-1`

## Problem
Detaching things in SLNG did not survive a relog, and the inventory kept showing them as worn:

- `DetachItemAsync` moved a Current-Outfit (COF) link to Trash **only when nothing was actually
  attached** (`!wasAttached`). For a real attachment it left the COF link alone, trusting the sim
  to remove it. On OpenSim that does not reliably happen, so the link survived and the item was
  re-worn on next login.
- `DetachAllAttachments` / `DetachByLocalId` send `ObjectDetach` by localId and **never touch the
  COF** — so "Detach All" is undone by the next login.
- No way to prune a COF that has accumulated dead links (from failed attaches, the old broken
  `RemoveFromOutfit` send, cross-viewer detaches).

## Implementation (`src/SLNG.Net/GridSession.cs`)

- **`DetachItemAsync`** — the COF-link-to-Trash step now runs for a real attachment too, not just
  the stale-link case. `DetachResult` unchanged; `StaleLinksRemoved` now also counts a normal
  detach's link.
- **`DetachAllAttachments`** — reads each detached prim's `AttachItemID` name-value, and after the
  `ObjectDetach` moves those items' COF links to Trash via the shared helper. Logs the count.
- **`RemoveOutfitLinksForItems(ICollection<Guid>)`** — shared helper: moves every COF link whose
  target (or own id) is in the set to Trash, drops it from the local store, returns the count.
- **`CleanUpCurrentOutfit()` → `OutfitCleanupResult(DeadLinks, TrashedTargetLinks, UnwornAttachmentLinks)`**
  — moves to Trash: (a) links that resolve to nothing, (b) links whose target item is itself
  already in Trash, (c) links to `AssetType.Object` items not currently attached. **Never** touches
  a Clothing/Bodypart link (removing one needs a rebake — out of scope, FEAT-AVATAR-01) or a
  currently-worn item. Trashed, not purged — recoverable.

Everything moves links, never real items: the outfit is edited, inventory is not.

## UI (`app/scripts/UI/InventoryPanel.cs`, "Angezogen" tab)
- A **"🧹 Outfit aufräumen"** button above the worn list → `CleanUpCurrentOutfit()`, then refreshes
  the worn list and the Current Outfit folder. Reports exactly what moved ("3 tote Links + 2 nicht
  getragene Anhänge → Papierkorb", or "Outfit ist sauber").
- The Current Outfit folder in the tree is re-fetched when the inventory window is shown, so an
  external (Firestorm) change is picked up without a relog.

## Acceptance criteria
- [ ] Detaching an attachment in SLNG removes it from the Current Outfit (survives a relog).
- [ ] "Detach All Attachments" also clears those items from the Current Outfit.
- [ ] "Outfit aufräumen" moves dead links + unworn attachment links to Trash and leaves
      clothing/body parts and worn items alone.
- [x] All of it moves links only, never a real inventory item; everything is recoverable from Trash.
- [x] No-connection: every path no-ops / returns zero without throwing (unit tests).

## Out of scope
- Removing a **system wearable** (Clothing/Bodypart) from the outfit — needs a safe rebake path,
  tracked by FEAT-AVATAR-01.
- Live COF sync via an inventory-update subscription (the on-show re-fetch + the FEAT-UI-16 timer
  cover the common cases).
