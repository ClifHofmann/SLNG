# [BUG-AVATAR-03] A system-wearable edit (esp. swapping an alpha layer) isn't visible without a manual rebake — and sometimes not even then

- **Feature ID:** `BUG-AVATAR-03`
- **Track:** `net`
- **Status:** `🧪 Review`
- **Owner:** `claude`
- **Depends on:** `FEAT-AVATAR-01` (system-wearable wear/remove on SSB), `BUG-AVATAR-01`
  (`RebakeAvatar` → `RequestServerSideRebakeAsync` on SSB)
- **Reported:** live, Agni / region *Millenium*, 2026-09-03. *"Ich kämpfe grad wieder mit dem
  Wechsel von Alphas, das ist noch nicht sauber."* → *"Auch nach Rebake wird das Alpha nicht
  angewendet."* → *"Jetzt ging es aber erst nach manuellem Rebake."*

## Symptom

Swapping one alpha-layer wearable for another (`RemoveWearableAsync(old)` + `WearWearableAsync(new)`)
records the change server-side (COF link delete + create via AIS, `AgentIsNowWearing` UDP) but the
avatar's body does not update. Pressing **Ctrl+Alt+R** usually fixes it after a delay; sometimes
even that doesn't apply the new alpha and it takes a second try.

## Why

1. **Nothing triggered a rebake.** `WearWearableAsync` / `RemoveWearableAsync` ended with a log
   line literally saying *"becomes visible after a rebake"* — and left that rebake to the user.
   On a server-side-baking region (SL) the sim will re-composite on its own eventually, but not
   promptly, so the edit "did nothing" until Ctrl+Alt+R.
2. **The rebake raced `cof_version`.** `RebakeAvatar` → `RequestServerSideRebakeAsync` →
   `_client.Appearance.RequestSetAppearance(forceRebake: true)`, which POSTs `{ cof_version }` to
   the `UpdateAvatarAppearance` cap. Fired right after the AIS COF edits, LibreMetaverse's stored
   `cof_version` can still be the pre-edit value, so the server composites the **previous** outfit
   — "rebaked, nothing changed". A second manual rebake a while later picked up the new version,
   which is the intermittent "sometimes works, sometimes not".
3. Agni was rate-limiting this session's cap traffic hard (`Caps rate limiter queue full …`,
   repeated `FetchInventory2` `TaskCanceledException`), which widens the window in (2).

`[Appearance] correction suppressed: appearance writing is disabled` in the same log is **not**
this bug — that's the FEAT-AVATAR-01 param-order correction, deliberately suppressed while
`SendAppearance` is off, and it carries no alpha/wearable data.

## Fix (`v0.20.37-alpha`)

- **Auto-rebake after a wearable edit, debounced.** `WearWearableAsync` / `RemoveWearableAsync`
  call new `ScheduleRebakeAfterWearableEdit()` — cancels any pending, waits **1.8 s** (so a swap's
  remove+wear, or several layers, coalesce into one rebake and the AIS writes settle), then, on an
  SSB region only, runs `RequestServerSideRebakeAsync()`. Non-SSB is untouched (the client-side
  bake already runs through `OnAppearanceSet` / `SendCorrectedAppearance`).
- **Re-read the COF before every SSB rebake.** `RequestServerSideRebakeAsync` now
  `FetchInventoryChildrenAsync(cofUuid)` first, so LibreMetaverse's store — and the `cof_version`
  it POSTs — reflects the edit. This also hardens the manual Ctrl+Alt+R path.
- `_wearableRebakeCts` cancelled/disposed in `Dispose()`.

## Still open / to verify in-world

- **Not yet re-verified in-world.** Confirm: swap an alpha, do nothing → body updates within a
  few seconds; and that the "even a manual rebake didn't apply it" case is gone (COF re-read
  should close it).
- If it still intermittently fails after this, the next suspect is the sim genuinely not acting on
  `RequestSetAppearance` under cap rate-limiting, or SLNG not re-processing the fresh
  `AvatarAppearance` — capture a `--diag` log and check for a new `[SelfBake] channels …` line
  (new bake ids) after the auto-rebake message.
- Alpha layers **stack** in SL; SLNG matches that (`WearableRules.ReplacesSameType` is false for
  Clothing/Alpha), so "switching" means the user must also take the old one off. Not a bug, but a
  candidate for a future "replace alpha" convenience.
- Tune the 1.8 s debounce against how long AIS actually takes to bump `cof_version` on a busy sim.
