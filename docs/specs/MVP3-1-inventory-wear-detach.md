# [MVP3-1] Inventory v2 — Wear, Detach & Attachment Management

- **Feature ID:** `MVP3-1`
- **Track:** `net` / `ui`
- **Status:** `🚧 In Progress`
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
Enable users to wear and detach objects, HUDs, body parts, and clothing directly from the Inventory Panel (`InventoryPanel.cs`). Support context menu actions ("Wear", "Add", "Detach", "Take Off") and wire them to `GridSession` / `LibreMetaverse.AppearanceManager`.

## Functional Requirements

### 1. Context Menu Adaptation (Inventory Tree)
- Update `InventoryPanel` context menu dynamically depending on whether an item is currently worn:
  - **Unworn Attachments / Prims / Objects:** Show "Wear / Attach" and "Add / Attach to...".
  - **Worn Attachments / Prims:** Show "Detach" (or "Take Off").
  - **Wearables (Clothing / Body Parts):** Show "Wear" or "Take Off".
- On selecting "Wear / Attach" or "Detach", invoke corresponding methods in `GridSession`.

### 2. GridSession Protocol Support (`SLNG.Net`)
- Implement `AttachItemAsync(Guid itemId, byte attachPoint = 0, bool replace = true)` in `GridSession`.
- Implement `DetachItemAsync(Guid itemId)` in `GridSession`.
- Implement `WearItemAsync(Guid itemId)` / `TakeOffItemAsync(Guid itemId)` for clothing/wearables.

### 3. Visual & Current Outfit Updates
- When an item is attached or detached, refresh the "Angezogen (Current Outfit)" folder in `InventoryPanel` to reflect active attachments and wearables.

## Acceptance Criteria
- [ ] Right-clicking an object/HUD in Inventory shows "Wear / Attach" or "Detach".
- [ ] Clicking "Wear / Attach" sends `RezSingleAttachmentFromInv` packet to simulator and attaches object/HUD.
- [ ] Clicking "Detach" sends `DetachAttachmentIntoInv` packet to simulator and detaches object/HUD.
- [ ] Current Outfit ("Angezogen") folder in Inventory UI reflects attached/detached items after action.
- [ ] `dotnet build` compiles cleanly and unit tests pass.

## Technical Specs & Affected Files
- `src/SLNG.Net/GridSession.cs` — Add methods for `AttachItemAsync`, `DetachItemAsync`, `Wearable` management.
- `app/scripts/UI/InventoryPanel.cs` — Context menu handling for Wear/Detach/Add/Take Off actions.
- `docs/specs/MVP3-1-inventory-wear-detach.md` — Feature specification.

## Sub-tasks / Progress
- [ ] Create `MVP3-1` spec & update `ROADMAP.md`
- [ ] Implement `AttachItemAsync` and `DetachItemAsync` in `GridSession.cs`
- [ ] Update context menu and action handlers in `InventoryPanel.cs`
- [ ] Verify HUD / Attachment wearing and detaching in client
