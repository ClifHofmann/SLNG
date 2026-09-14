# [BUG-INV-04] Inventory offers are silently dropped — no accept dialog, item only after relog

- **Feature ID:** `BUG-INV-04`
- **Track:** `net` (+ `ui`)
- **Status:** `🧪 Review` — implemented v0.22.144-alpha, awaiting in-world verification (give a landmark from a second account).
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

Reported live 2026-09-14: *„das Übergeben von Objekten kommt noch nicht sauber an. Ich hab
vorhin eine Landmarke bekommen — 1. kam kein Fenster ob ich das will, 2. war die erst nach
Relog im Inventar zu sehen. Sowas sollte in den Cache gepusht werden."*

SLNG is the **giving** half of FEAT-INV-02 but has no **receiving** half at all. An inventory
offer from another agent (or from an object) never reaches the user: no accept/decline
window, no reply to the simulator, and no local inventory update. The item exists server-side
either way, so it appears on the next login — which is what the user saw.

## Root cause — two independent defects

### 1. The offer never leaves `GridSession.OnInstantMessage`

`OnInstantMessage` (`src/SLNG.Net/GridSession.cs`) ends with

```csharp
if (e.IM.Dialog != InstantMessageDialog.MessageFromAgent) return;
```

`InventoryOffered` (dialog 4) and `TaskInventoryOffered` (dialog 9) fall through that guard
and vanish. Nothing is raised, nothing is answered. Exactly the shape of the group-invitation
bug fixed earlier in the same method.

### 2. LibreMetaverse's own offer event would auto-decline everything

The obvious fix — subscribe to `InventoryManager.InventoryObjectOffered` — is a trap, the
same one already documented for `GroupManager.GroupInvitation`. Verified against the **pinned
LibreMetaverse 3.1.3 package** by reflection, not only against the newer vendored checkout:

- `InventoryObjectOfferedEventArgs.Accept` is settable and its constructor initialises it to
  **`false`** (`InventoryEventArgs.cs:49`).
- `InventoryManager.Self_IM` (`InventoryManager.Handlers.cs:94-198`) fires the event
  **synchronously on the network thread** and then, on the very next line, sends
  `InventoryAccepted` or `InventoryDeclined` based on `args.Accept`.

So a handler that opens a window and returns — the only thing a UI can do — sends
`InventoryDeclined` before the user has seen the window. The whole block is also gated on
`m_InventoryObjectOffered != null`, which is why *not* subscribing is currently harmless
rather than actively wrong: LMV sends nothing at all.

**Therefore: do not subscribe.** Handle the two dialogs in SLNG's own `OnInstantMessage`,
buffer the offer, ask the user, and send the reply ourselves.

### 3. The item is already in inventory — it is the local cache that is missing it

From the reference viewer, `llviewermessage.cpp:1714-1717`, in the `IM_INVENTORY_OFFERED`
branch:

> *"This is an offer from an agent. In this case, the back end has already copied the items
> into your inventory, so we can fetch it out of our inventory."*

The viewer answers the offer **and** runs `LLOpenAgentOffer::startFetch()` — an
`LLInventoryFetchItemsObserver` on the offered item id — so the local inventory model learns
about the item without a refetch of the whole tree. SLNG does neither, so the item is only
discovered the next time the destination folder is fetched from scratch, i.e. after a relog.

## Wire protocol — confirmed, not guessed

From `llimprocessing.cpp:895-935` and `llviewermessage.cpp:1590-1640`:

| | Agent offer | Object (task) offer |
|---|---|---|
| Offer dialog | `InventoryOffered` (4) | `TaskInventoryOffered` (9) |
| `BinaryBucket` | 17 bytes: `[0]` = `AssetType`, `[1..17]` = **item id** | 1 byte: `AssetType` only |
| Item id known? | yes | no (`LLUUID::null`) |
| Already in inventory? | **yes**, server-side copy | no, created on accept |
| `Message` | item name / description | item name / description |

The reply is an `ImprovedInstantMessage` back to `FromAgentID`, reusing the offer's
`IMSessionID` as the transaction id:

- **Accept:** dialog = offer + 1 (`InventoryAccepted` 5 / `TaskInventoryAccepted` 10),
  `BinaryBucket` = the 16-byte **destination folder id**.
- **Decline:** dialog = offer + 2 (`InventoryDeclined` 6 / `TaskInventoryDeclined` 11),
  `BinaryBucket` empty. The viewer sends the decline *and* locally discards to Trash
  (`LLDiscardAgentOffer`); the simulator does the server-side move.

The destination folder is the local default folder for the asset type —
`gInventory.findCategoryUUIDForType(LLFolderType::assetTypeToFolderType(info->mType))`
(`llimprocessing.cpp:770`, `:935`), which is `InventoryManager.FindFolderForType(AssetType)`
in LibreMetaverse. On decline the viewer sends the **Trash** folder id instead
(`llviewermessage.cpp:1957`).

`AgentManager.InstantMessage(fromName, target, message, imSessionID, dialog, offline,
position, regionID, binaryBucket)` exists on the pinned 3.1.3 build and carries everything
needed — confirmed by reflection.

## Threading

`OnInstantMessage` runs on a **LibreMetaverse network thread**. The offer must be converted
to an engine-neutral DTO and buffered, exactly like `GroupInvitationEvent`: `Boot` drains
`_pendingInventoryOffers` in `_Process` and opens the window on the Godot main thread. No
LibreMetaverse type (`InstantMessage`, `AssetType`, `UUID`) crosses the `SLNG.Net` boundary.

## Acceptance Criteria

- [ ] Receiving an item or folder from another avatar opens an accept/decline window naming
      the giver and the item, before anything is answered.
- [ ] **Accept** sends `InventoryAccepted` with the default folder for the asset type, and the
      item appears in the inventory tree **without a relog**.
- [ ] **Decline** sends `InventoryDeclined`, and the item does not appear in the destination
      folder.
- [ ] An offer from an in-world object (`TaskInventoryOffered`) is handled on the same path,
      with no item id to fetch.
- [x] Nothing subscribes to `InventoryManager.InventoryObjectOffered` (a regression there
      would silently auto-decline every offer).
- [x] Unit tests: bucket parsing (17-byte agent / 1-byte task / malformed), and the
      accept/decline dialog arithmetic.

## Technical Specs & Affected Files

- `src/SLNG.Core/GridEvents.cs` — new `InventoryOfferEvent` DTO (offer id, giver id, giver
  name, item name, item id, asset type, from-task flag).
- `src/SLNG.Net/GridSession.cs` — handle both dialogs in `OnInstantMessage`; raise
  `InventoryOfferReceived`; `RespondToInventoryOffer(...)` sends the reply and, on accept,
  `RequestFetchInventoryAsync` to pull the item into the store.
- `app/scripts/UI/InventoryOfferWindow.cs` — new `SLNGWindow` (accept / decline), modelled on
  `GroupInvitationWindow`.
- `app/scripts/Boot.cs` — buffer + drain + window bookkeeping, and refresh the destination
  folder in `InventoryPanel` on accept.
- `app/scripts/UI/InventoryPanel.cs` — reuse the existing
  `RefreshFolder(folderId, knownItemId, knownAssetId)`.
- Locales `en-US`, `de-DE`.

## Sub-tasks / Progress

- [x] DTO + `GridSession` offer decode and event
- [x] `RespondToInventoryOffer` (accept/decline wire reply + fetch)
- [x] `InventoryOfferWindow` + locale keys
- [x] `Boot` wiring: buffer, drain, folder refresh
- [x] Tests
- [ ] In-world verification: give a landmark from a second account
