# Feature: FEAT-ECON-02 (Buy in-world objects)

## Context
Carved out of `MVP5-2`. Depends on `FEAT-ECON-01` (balance). Lets a user buy a
for-sale object from the right-click menu.

## Requirements
1. **Sale info:**
   - Read `SaleType` (not / original / copy / contents) and `SalePrice` from
     `ObjectProperties` for the selected root prim (request properties if not cached).
   - Only show **Kaufen …** when `SaleType != not` .
2. **Purchase flow (`protocol-re`):**
   - Confirmation dialog: object name, price, what is being bought (original moves the
     object, copy delivers a copy, contents delivers the inventory), current balance.
   - On confirm, send `ObjectBuy` (`GridClient` money/objects API) with the correct
     sale type and price.
   - **TPV / correctness:** send only the price the sim advertised; never a client-chosen
     amount.
3. **Result handling:**
   - Insufficient funds → clear message, no packet sent (or handle the sim's rejection).
   - Object no longer for sale / price changed → surface it, do not silently no-op.
   - Success → balance readout updates via `FEAT-ECON-01`; a chat/system line confirms.

## Acceptance Criteria
- [ ] A for-sale object shows "Kaufen …" in its context menu with the right price.
- [ ] A non-for-sale object does not.
- [ ] Confirming a purchase on a test grid completes and the balance drops by the price.
- [ ] Insufficient-funds and not-for-sale cases show a message, never a silent failure.

## Technical Specs & Affected Files
- `app/scripts/UI/InWorldContextMenu.cs`
- `app/scripts/ObjectSelectionController.cs`
- `src/SLNG.Net/GridSession.cs` (ObjectBuy wrapper, sale-info DTO)
- `src/SLNG.Core/GridEvents.cs`
