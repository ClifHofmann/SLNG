# Feature: FEAT-ECON-02 (Buy in-world objects)

- **Status:** `✅ Done` — in-world 2026-09-20: a purchase went through, and paying a scripted object was confirmed earlier in the same round. The scope grew past the spec on the way: paying is not buying (a script names its own terms with `llSetPayPrice`), a left click has to do what the object's click action says, and a touch has to say WHERE on the object it landed — without that last one a vendor panel that reads `llDetectedTouchFace` ignores the click entirely, which is what "ich klicke drauf und es passiert nichts" was.

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
- [x] A for-sale object shows "Kaufen …" in its context menu with the right price. The entry
      is supplied into an ALREADY OPEN menu, because the price arrives with `ObjectProperties`
      — i.e. after the click that selected the object. Without that, the first right-click on a
      vendor would never offer a purchase.
- [x] A non-for-sale object does not.
- [x] Confirming a purchase on a test grid completes. Confirmed in-world 2026-09-20. The
      balance readout follows the simulator's own `MoneyBalanceReply` (FEAT-ECON-01), which is
      the only figure it ever shows.
- [x] Insufficient-funds and not-for-sale cases show a message, never a silent failure. The
      shortfall is caught BEFORE anything is sent and named in L$ (`RefreshAffordable`), rather
      than left to the simulator to answer with a bare warning. Guarded by `BalanceTests`; not
      exercised live, since it needs a deliberately unaffordable purchase.

## Technical Specs & Affected Files
- `app/scripts/UI/InWorldContextMenu.cs`
- `app/scripts/ObjectSelectionController.cs`
- `src/SLNG.Net/GridSession.cs` (ObjectBuy wrapper, sale-info DTO)
- `src/SLNG.Core/GridEvents.cs`
