# Feature: FEAT-ECON-01 (L$ balance display)

## Context
Carved out of `MVP5-2`. The first slice of in-world economy: just *see* your L$
balance. There is no economy code in the project yet.

## Requirements
1. **Protocol (`protocol-re`):**
   - Seed the balance from the login response (`money` field), if present.
   - Track `MoneyBalanceReply` (LibreMetaverse `AgentManager.Balance` / `MoneyBalanceReply`
     event) and update on every change.
   - Expose an engine-neutral `int Balance` + `BalanceChanged` event on `GridSession`.
     No LMV type crosses the boundary.
2. **UI:**
   - A compact `L$ <n>` readout in a corner HUD element (top bar area), consistent with
     the existing HUD styling.
   - Updates live; brief highlight on change is optional.
3. **Non-goals:** paying, buying, transaction history — those are `FEAT-ECON-02` and the
   remainder of `MVP5-2`.

## Acceptance Criteria
- [ ] The current L$ balance is visible after login.
- [ ] It updates within a second of a balance change (tested with a self-pay or an object payout on a test grid).
- [ ] No LibreMetaverse type is exposed from `SLNG.Net`.
- [ ] Unit test on the wire→DTO mapping.

## Technical Specs & Affected Files
- `src/SLNG.Net/GridSession.cs`
- `src/SLNG.Core/GridEvents.cs`
- `app/scripts/UI/` (HUD readout)
