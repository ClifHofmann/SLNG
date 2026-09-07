# [BUG-AVATAR-01] "Avatar neu backen" was a no-op on every grid, including SL

- **Feature ID:** `BUG-AVATAR-01`
- **Track:** `net`
- **Status:** `🧪 Review` — in-world retest 2026-09-07 (Agni) still reads as broken: Ctrl+Alt+R prints `Avatar wird neu gebacken …` then `Dieses Grid backt serverseitig — … wird übersprungen.` with no visible rebake. The underlying `{cof_version}` nudge on SSB is correct; the contradictory message comes from `Boot.RebakeAvatar` also calling the OpenSim-only `BakeAvatarAsync()`, and there is no positive confirmation the cap POST landed. Fix needed (small, `Boot.cs`): on SSB skip `BakeAvatarAsync` and report the cap POST result.
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)
- **Branch:** `feature/FEAT-SL-01-second-life-readiness` (found and fixed mid-session, same branch)
- **Version:** `v0.20.0-alpha`

## Overview & Goal

Reported live on Aditi, with a screenshot: the self avatar's system head and hands rendered
blank white — the default Linden placeholder, meaning no baked texture was bound at all — while
mesh clothing on the torso rendered fine. The escape hatch for exactly this ("Avatar neu backen",
Ctrl+Alt+R) turned out to do nothing.

## Root cause

`GridSession.RebakeAvatar()` carried a "HARD STOP 2026-08-31" that unconditionally refuses to call
`AppearanceManager.RequestSetAppearance()` on **every** grid, logging a line and returning. That
stop was correct for the problem it was written for: with `SendAppearance = false`,
`RequestSetAppearance` is not gated by that flag, so calling it on the **legacy** (client-side
baking) path sends whatever `MakeAppearancePacket` produces — which, from a cold state with no
decoded wearables, is a scrambled or all-zero bake that had stripped the avatar on OpenSim
repeatedly (documented in the stop's own comment).

That reasoning does not apply on a server-side-baking (SSB) region. Verified by reading the
pinned package's actual branch (`AppearanceManager.RequestSetAppearanceAsync`):

```csharp
var useClientSideBaking = !ServerBakingRegion();
if (!useClientSideBaking) {
    ...
    if (!ServerBakingDone || forceRebake) {
        if (await UpdateAvatarAppearanceAsync(...)) { ... }
    }
}
```

`UpdateAvatarAppearanceAsync` is the `UpdateAgentInformation`-style safe path: a capability POST
of `{ cof_version }`, no visual params, no textures — it never reaches `MakeAppearancePacket` at
all. None of the Aug-31 concerns apply. Refusing it unconditionally meant SLNG had no way to ask
an SL sim to re-push the self avatar's `AvatarAppearance` outside of an actual wearable edit —
including after a login whose initial appearance was never sent, or arrived and was dropped
(LibreMetaverse applies a COF-version staleness guard to inbound `AvatarAppearance` packets for
self; see "Still open" below).

## Acceptance Criteria

- [x] `RebakeAvatar()` on a server-side-baking region calls
      `AppearanceManager.RequestSetAppearance(forceRebake: true)` instead of refusing.
- [x] The OpenSim / legacy hard stop is otherwise **unchanged** — this fix does not touch or
      weaken the Aug-31 protection for the case it was written for.
- [x] Confirmed against the pinned package's own source that the SSB branch never reaches
      `MakeAppearancePacket`, so nothing from the Aug-31 incident can recur through this path.
- [x] Both projects build clean; 534 tests still pass.

## Technical Specs & Affected Files

| File | Change |
|---|---|
| `src/SLNG.Net/GridSession.cs` | `RebakeAvatar()` branches on `RegionHasServerSideBaking()` before the diagnostic/hard-stop; new `RequestServerSideRebakeAsync` helper |

### Design notes

- **`forceRebake: true`**, not the default `false`. Per the pinned source, `forceRebake` zeroes
  LibreMetaverse's cached bake-slot ids before asking, so a click always requests a genuinely
  fresh composite rather than something that could short-circuit as "already done."
- **The pre-existing diagnostic block (bake-slot report via `MakeAppearancePacket()`) is skipped
  entirely on SSB**, not just the hard stop after it — that report describes the legacy send path
  and would print meaningless "ZERO" slots on a grid that never populates them, which would read
  as evidence of a problem that doesn't exist there.
- **Deliberately not wired to fire automatically at login.** The bug is confirmed and the fix is
  verified against source, but whether an SL login ever independently fails to deliver the self
  avatar's initial `AvatarAppearance` (as opposed to this specific report being a stale COF-version
  drop, or a wearable-state peculiarity) is not yet established — see "Still open." Making the
  button work is the low-risk, immediately useful fix; an automatic trigger is a separate decision
  once there's evidence it's actually needed.

## What the tests guarantee

Nothing new by unit test — same reasoning as `RegionHasServerSideBaking()`'s other consumers in
`FEAT-SL-01`: the SSB branch needs a connected client and a real Linden-grid capability, which
`tests-rules` reserves for local OpenSim, and OpenSim has no such capability to exercise it
against. Confidence here comes from reading the exact branch the pinned LibreMetaverse package
takes, not from a mock. The Aditi session is what will confirm the fix in practice.

## Still open

- **Not yet re-verified in-world** — this fix has not yet been confirmed to actually resolve a
  blank avatar via a real Ctrl+Alt+R click on Aditi.
- **Why the avatar was blank in the first place is not fully explained.** Two candidate
  mechanisms were found while investigating, neither confirmed:
  1. LibreMetaverse's `AvatarAppearanceHandler` applies a COF-version staleness guard to inbound
     `AvatarAppearance` packets for self (`COFVersion <= Client.Appearance.LastUpdateReceivedCOFVersion`
     → silently dropped, `Logger.DebugLog` only). If SLNG's own COF-cleanup work (`FEAT-INV-03`,
     duplicate-link trashing) or something else advances the SIM's COF version without LMV's
     internal tracker agreeing, a genuinely fresh appearance push could be discarded as "stale."
  2. Simply: nothing ever asked for one this session, because no wearable was worn/removed through
     SLNG's own UI after arriving on this grid, and the sim's own unsolicited initial push (if any)
     either never happened, was missed, or was the thing dropped by (1).

  `SLNG_LMV_DEBUG=1` would surface the "Dropping stale AvatarAppearance for self" line if (1) is
  what happened; that is the next diagnostic step if the manual rebake fix does not resolve it.
