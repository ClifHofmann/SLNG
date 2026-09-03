# [BUG-AVATAR-03] Wearable/alpha edit not visible without a rebake — and the rebake drops worn attachments

- **Feature ID:** `BUG-AVATAR-03`
- **Track:** `net`
- **Status:** `🚧 In Progress` — first fix attempt reverted (made it worse); real cause identified.
- **Owner:** `claude`
- **Depends on:** `FEAT-AVATAR-01`, `BUG-AVATAR-01` (`RebakeAvatar` → `RequestServerSideRebakeAsync`)
- **Reported:** live, Agni / *Millenium*, 2026-09-03. *"Wechsel von Alphas … noch nicht sauber"* →
  *"auch nach Rebake wird das Alpha nicht angewendet"* → *"jetzt ging es aber erst nach manuellem
  Rebake"* → ***"mir fehlen jetzt schon das 2. mal Items nach dem Relog — die Haare sind nicht
  mehr angezogen und die Schuhe auch nicht"*** → *"die Schuhe sind im Nachhinein aufgetaucht, die
  Haare nicht."*

## Two problems, and they pull in opposite directions

### A — a wearable edit isn't visible until a rebake
`WearWearableAsync` / `RemoveWearableAsync` record the COF change (AIS link delete/create,
`AgentIsNowWearing`) and log *"becomes visible after a rebake"*, leaving the rebake to the user's
**Ctrl+Alt+R**. Swapping an alpha layer looks like it did nothing.

### B — the rebake (`RequestSetAppearance`) drops worn attachments  ← the serious one
`RebakeAvatar` → `RequestServerSideRebakeAsync` → `_client.Appearance.RequestSetAppearance(forceRebake: true)`.
On SSB that is meant to be a `{ cof_version }` POST to `UpdateAvatarAppearance`, but LibreMetaverse
also **reconciles the worn set from the COF** inside that call (`RezMultipleAttachmentsFromInv` is
in every rebake's log). With `SendAppearance = false` LMV's wearable/attachment cache is **empty**,
so it rebuilds the set purely from a fresh COF fetch — and on Agni that fetch is rate-limited
(`Caps rate limiter queue full`, repeated `Failed getting data from FetchInventory2 … A task was
canceled`). A COF link whose target does not resolve in that window is treated as stale, and the
reconcile **drops it**. Observed: COF item count moving 43 → 39 → 41 across rebakes, `cof_version`
bumping each time, and worn attachments (hair, shoes) gone after the next relog — shoes came back
on a later server re-sync, the hair link did not.

## What was tried and reverted

**`v0.20.37` (reverted in `v0.20.38`):** auto-fired the rebake after every wearable edit
(`ScheduleRebakeAfterWearableEdit`, 1.8 s debounce) and had `RequestServerSideRebakeAsync` fetch
the COF first to beat a `cof_version` race. This **made B worse** — it ran the attachment-dropping
`RequestSetAppearance` after *every* alpha edit instead of only when the user chose to, and the
extra COF fetch added to the rate-limit pressure that causes the drop. Full revert of `df99875`.

## Current state (`v0.20.38`)

Back to `v0.20.36` behaviour: a wearable edit does **not** auto-rebake; Ctrl+Alt+R still calls
`RequestSetAppearance` unchanged. Problem A is back (need a manual rebake), problem B is at least
no longer amplified. **Interim guidance for the user: prefer a relog over Ctrl+Alt+R to see a
wearable change — the sim re-composites on its own from the `cof_version` bump — and do not spam
the rebake, each call is a chance to lose an attachment on a busy grid.**

## Real fix — direction, not yet built

Stop routing the SSB rebake through LibreMetaverse's `RequestSetAppearance` at all. SLNG should
POST `{ cof_version }` to the `UpdateAvatarAppearance` capability **directly**
(`HttpCapsClient.PostAsync`, the same way `FetchOneBatchAsync` / the RenderMaterials query already
bypass LMV) — a pure nudge that never touches the worn set. Needs: the cap URL from
`CurrentSim.Caps.CapabilityURI("UpdateAvatarAppearance")`, and the current `cof_version` (the COF
folder's `Version` in the store, or track the value from the sim's `Requesting bake for COF
version N` path). Then the auto-rebake from `v0.20.37` becomes safe to reinstate on top of it.

Also worth pinning: does LMV's reconcile drop the COF **link** server-side (durable) or only its
own local view? The relog persistence says at least the hair link did not come back — treat it as
durable until proven otherwise.

## Acceptance

- A wearable/alpha edit becomes visible without a manual rebake **and** without ever changing the
  worn attachment set.
- N rebakes in a row (manual or auto) never remove a worn attachment, on a rate-limited grid.
- Test coverage for the direct `UpdateAvatarAppearance` POST shape where there's a surface.
