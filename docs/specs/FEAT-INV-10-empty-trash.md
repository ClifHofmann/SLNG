# [FEAT-INV-10] Empty the Trash, delete from it for good, restore out of it

- **Feature ID:** `FEAT-INV-10`
- **Track:** `ui` / `net`
- **Status:** `🧪 Review`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
Since BUG-INV-09 a Delete moves things into the Trash, and nothing ever left it. This adds the
reference viewer's three Trash actions: **Empty Trash** on the Trash folder, and **Restore** and
**Delete permanently** on anything inside it. Permanent deletion is the only inventory action with
no way back, so it sits behind its own confirmation and is only reachable inside the Trash.

## Protocol
| Action | AIS (Second Life) | UDP (OpenSim, no AIS) | Viewer source |
|---|---|---|---|
| Empty Trash | `DELETE {cap}/category/{trash}/children` | `PurgeInventoryDescendents` | `purge_descendents_of`, llviewerinventory.cpp:1648-1702 |
| Delete permanently | `DELETE {cap}/item/{id}` or `/category/{id}` | `RemoveInventoryObjects` | `remove_inventory_object`, :1530-1641 |
| Restore | — (a move) | `MoveInventoryItem` / `MoveInventoryFolder`, no restamp | `LLItemBridge::restoreItem`, llinventorybridge.cpp:1953-1967 |

OpenSim accepts a folder purge only inside Trash or Lost and Found (`XInventoryService.ParentIsTrashOrLost`),
but deletes an **item** wherever it is (`DeleteItems` has no such check). `GridSession.PurgeFromTrashAsync`
therefore refuses anything outside the Trash itself, not only the menu.

## Acceptance Criteria
- [x] The Trash can be emptied; the prompt says how much is about to go.
- [x] A single item or folder in the Trash can be deleted for good, after its own confirmation.
- [x] An item or folder can be restored out of the Trash; the menu entry names the destination.
- [x] Nothing worn is purged: Empty Trash and Delete permanently both refuse, and say what is worn.
- [x] Unit tests for the decisions (`TrashTests`); selftest builds the menus in a real tree.
- [ ] Confirmed in-world, on OpenSim and on Second Life.

## Decisions
- **Restore does not go back to where the thing came from.** Nobody knows that: the grid keeps no
  record of an item's previous folder and neither does the reference viewer. Like the viewer, an item
  goes to the system folder for its type (a snapshot to the Photo Album), a folder to the top of the
  inventory, and anything without a matching system folder to the top as well. The roadmap entry's
  first description said otherwise and was corrected.
- **Not LibreMetaverse's `EmptyTrashAsync` / `RemoveDescendantsAsync`** (pinned 3.1.6). Both drop the
  Trash folder itself from the local store afterwards (`RemoveLocalUi` removes the node it is given);
  `FindFolderForType(Trash)` then falls back to the root and the next Delete moves things there. The
  AIS branch of `EmptyTrashAsync` also reports success when the request threw, and its UDP branch
  only removes the children the local store happens to know.
- **The Trash id is verified against the folder** (`VerifiedTrashFolder`): `FindFolderForType`
  answers "no such folder" with the inventory root, and a purge of the root's descendants would be
  the whole inventory.
- **Worn = attached right now or in the live wearables set**, not a Current Outfit link, which can be
  stale and would block emptying the Trash for ever. The viewer disables Empty Trash only for worn
  attachments (`hasAttachmentsInTrash`); SLNG also counts worn wearables, because unlike the viewer its
  Delete does not take a worn thing off first.
- Refusals are explained in the status line instead of greyed-out menu entries — the same choice the
  panel made for "Als Verknüpfung einfügen".
- The clipboard is cleared once the thing on it no longer exists, as the viewer does on a purge.

## Technical Specs & Affected Files
- `src/SLNG.Net/GridSession.Trash.cs` (new), `TrashSummary.cs`, `TrashPurgeResult.cs`
- `src/SLNG.Net/GridSession.Inventory.cs` (`DeleteFolder` checks the Trash is real)
- `app/scripts/UI/InventoryPanel.cs`, `app/scripts/SelfTest.cs`
- `app/i18n/en-US.json`, `de-DE.json` (`ui.inventory_trash.*`)
- `tests/SLNG.Net.Tests/TrashTests.cs`

## Sub-tasks / Progress
- [x] Network half + tests
- [x] Menus, prompts, strings (de/en), selftest check
- [ ] In-world confirmation
