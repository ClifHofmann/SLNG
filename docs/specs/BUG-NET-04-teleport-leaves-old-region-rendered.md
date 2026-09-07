# [BUG-NET-04] Teleport leaves the old region's terrain/objects rendered

- **Feature ID:** `BUG-NET-04`
- **Track:** `net`
- **Status:** `✅ Done` (superseded) — in-world 2026-09-07 (Agni): the original "old region lingers after a teleport" symptom has not recurred. The remaining teleport problem — destination region comes up nearly empty + `Mesh`/`Instance` RID leak + `Vector3 cannot be normalized` flood + disposed-`ImageTexture` exceptions — is a distinct teardown/lifecycle bug, tracked as `BUG-NET-13`.
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)
- **Branch:** `feature/FEAT-SL-01-second-life-readiness` (found and fixed mid-session, same branch)
- **Version:** `v0.20.0-alpha`

## Overview & Goal

Reported live, on Aditi: *"nach dem Teleport sehe ich noch die sim auf der ich gerade war also
auch die objekte"* — after teleporting, the previous region's terrain and objects stayed
rendered, un-recentered, indefinitely.

## Root cause

BUG-NET-03 built the region-cleanup path on one signal: the grid sends a `DisableSimulator`
packet for a region we've left, LibreMetaverse turns that into `SimDisconnected`,
`GridSession.OnSimDisconnected` raises `RegionDisconnectedReceived`, and
`WorldSimulation.OnRegionDisconnected` calls `World.RemoveRegion`. That path is correct and
well-tested for a **walking border crossing**: with `Settings.Agent.MultipleSims = true`, the old
region genuinely becomes a live BUG-NET-03 neighbor circuit, and the grid tells us when it's no
longer relevant.

**A teleport is not a border crossing.** `NetworkManager.Connect(..., setDefault: true, ...)` —
what LibreMetaverse calls internally when `TeleportFinish` arrives — never disconnects the OLD
`CurrentSim`; it only swaps the `CurrentSim` pointer (`SetCurrentSim`) and adds the new one.
Verified by reading the pinned-package's own source path (`Connect` → `SetCurrentSim`): the old
simulator is left exactly as connected as it was, with no client-side signal that it is no
longer relevant, and its removal depends entirely on the ORIGINATING sim eventually sending
`DisableSimulator` — which is not guaranteed to happen promptly for a teleport to a distant,
non-adjacent region, unlike a walking crossing where the grid's own interest-list logic is
naturally driving that decision as you move.

## Acceptance Criteria

- [x] A teleport to a region more than one region-grid step (256 m) away from the destination
      removes the old region's terrain and objects immediately, without waiting for
      `DisableSimulator`.
- [x] A normal walking border-crossing (adjacent region, ≤1 step in either axis) is **untouched**
      — still handled entirely by the existing BUG-NET-03 `DisableSimulator` path.
- [x] No double-cleanup risk: removing an already-removed region is a no-op
      (`World.RemoveRegion` is idempotent).
- [x] Unit tests written and passing (12 new, pinning the distance math the decision depends on).

## Technical Specs & Affected Files

| File | Change |
|---|---|
| `src/SLNG.Net/GridSession.cs` | New `OnSimChanged` handler on `Network.SimChanged`; new `RegionGridOffset` helper (factored out of the existing `NeighborDir` diagnostic); `NeighborDir` now calls it instead of duplicating the math |
| `tests/SLNG.Net.Tests/RegionGridOffsetTests.cs` | new — 12 tests |

### Design notes

- **`Network.SimChanged`, not a new poll or timer.** LibreMetaverse already raises this event at
  exactly the moment `CurrentSim` changes, carrying `SimChangedEventArgs.PreviousSimulator` — the
  one piece of information `OnSimConnected` alone cannot reconstruct ("a sim connected" tells you
  nothing about what you left behind). Confirmed via reflection against the pinned 3.1.3 assembly
  that this event and property exist exactly as expected.
- **The distance threshold is the same >1 region-step definition `NeighborDir`'s compass label
  already implies**, just turned into a decision instead of a label. This is deliberately
  conservative: a border crossing to an IMMEDIATELY adjacent region (dx/dy both ≤ 1) is left
  entirely to the existing, working `DisableSimulator` path, so this fix cannot race or duplicate
  BUG-NET-03's cleanup for the case that already worked. Only a region that is provably too far
  away to ever be a legitimate neighbor is removed eagerly.
- **Reuses `World.RemoveRegion` via the same `RegionDisconnectedReceived` event** the existing
  path already uses — no new removal code, no new render-side handling to get right. If the real
  `DisableSimulator` later arrives for the same region anyway (it usually still will, just later
  than useful), the second `RemoveRegion` call is a no-op.
- **A late packet from the old circuit can still recreate a stale entity** for a moment, if the
  originating sim keeps delivering `ObjectUpdate`/`AvatarUpdate` for a few more packets before its
  own `DisableSimulator` finally lands — `OnObjectUpdate` et al. pass `e.Simulator.Handle` straight
  through regardless of which region is "current". This is a much smaller residual risk than the
  original bug (a few stray packets vs. permanently stuck), self-corrects once the real
  `DisableSimulator` arrives, and matches what any viewer relying on the same circuit-closure
  signal would see.

## What the tests guarantee

`RegionGridOffsetTests` pins the handle-decomposition math (`RegionGridOffset`) `OnSimChanged`'s
neighbor-vs-teleport decision depends on: same region → zero offset, all 8 immediate neighbors
(cardinal and diagonal) → offset within `±1`, several genuinely distant handles (including a
40-region cross-continent jump) → offset exceeding `±1` on at least one axis, and that the offset
is antisymmetric (swapping the two handles negates it, the same relationship `OnSimChanged` and
`NeighborDir` both rely on implicitly).

`OnSimChanged` itself is **not** directly unit-tested — it needs a live `NetworkManager.SimChanged`
event, which needs a connected `GridClient`, out of scope for `SLNG.Net.Tests`'s local-OpenSim
policy in the same way `RegionHasServerSideBaking()` and `SetPreferredMaturityAsync` already are
(see `FEAT-SL-01`/`FEAT-SL-02`). The Aditi session that found this bug is what will confirm the
fix.

## Still open

- **Not yet re-verified in-world.** The bug was reported once; this fix has not yet been
  confirmed against a real teleport on Aditi.
- **The "late packet recreates a stale entity" residual risk** above is reasoned about, not
  measured — unknown how many packets, if any, actually arrive from the old circuit before the
  real `DisableSimulator` lands in practice.
