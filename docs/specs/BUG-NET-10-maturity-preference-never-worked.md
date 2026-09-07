# [BUG-NET-10] Maturity preference: three pieces of the same gap, found via a compiler warning

- **Feature ID:** `BUG-NET-10`
- **Track:** `net`
- **Status:** `✅ Done` — confirmed in-world 2026-09-07 (Agni), with FEAT-SL-02: the Age Settings tab reflects the real account ceiling and a maturity change sticks.
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)
- **Branch:** `feature/FEAT-SL-01-second-life-readiness`
- **Version:** `v0.20.4-alpha`

## Overview & Goal

User: *"Kannst du die warnings noch beseitigen (beim bauen und starten)."* The one build warning
was `CS0067: The event 'GridSession.MaturityPreferenceChanged' is never used`. Tracing why turned
up a real, working feature (`FEAT-SL-02`, Maturity/Age preferences) that was actually broken in
three separate ways at once — the warning was the only surviving trace of a gap that otherwise
produces no visible error, just a preferences page that quietly never reflected reality.

## Root cause

All three live in the block explicitly marked `// Restored Uncommitted Methods` in
`GridSession.cs` — the same marker this session's earlier restoration audit (see `BUG-AVATAR-02`'s
"Still open" and the process-failure note in `HANDOVER.md`) already found had lost several other
pieces between being tested and a later commit made from a stale partial copy of the file. This
looks like a fourth, previously uncaught casualty of that same incident:

1. **`MaturityPreferenceChanged` was never raised.** `SetPreferredMaturityAsync` updates
   `PreferredMaturity` on a successful `UpdateAgentInformation` POST but never invoked the event —
   exactly what CS0067 flagged. `MaturityPreferencesPage.cs` already subscribes to it and its own
   comment says *"SetPreferredMaturityAsync already raised MaturityPreferenceChanged on success"* —
   the UI code was written assuming this worked.
2. **`SupportsMaturityPreference` was permanently `false`.** It had been turned into a plain
   `{ get; private set; } = false` auto-property with nothing anywhere ever assigning it —
   `git log -S "SupportsMaturityPreference = true"` across all history returns nothing. `FEAT-SL-02`'s
   own spec (§ "Found live on Aditi") explicitly documents it as reading the region's capability
   list *live*, not a cached value — the code no longer matched what its own spec said it did.
3. **`AccountMaturityMax`/`PreferredMaturity` were never seeded from the login response.**
   `LoginResponseData.AgentAccessMax`/`AgentRegionAccess` (the account's verified ceiling and
   currently active pick, per `agent_access_max`/`agent_region_access`) were never read anywhere —
   both properties stayed at their `MaturityLevel.General` constructor default for the entire
   session regardless of the actual account or grid.

Net effect before this fix: opening Preferences always showed "grid doesn't support this" (bug 2),
even after successfully changing the setting the UI never updated to reflect it (bug 1), and the
displayed "ceiling"/initial value never matched the real account (bug 3).

## Acceptance Criteria

- [x] `LoginAsync`'s success path sets `AccountMaturityMax`/`PreferredMaturity` from
      `response.AgentAccessMax`/`response.AgentRegionAccess` via the existing `MaturityAccess.FromShortString`.
- [x] `SupportsMaturityPreference` is a computed property reading
      `CurrentSim?.Caps?.CapabilityURI("UpdateAgentInformation") != null` live, matching the spec.
- [x] `SetPreferredMaturityAsync` raises `MaturityPreferenceChanged` after updating `PreferredMaturity`
      on success.
- [x] `dotnet build SLNG.sln` / `app/SLNG.App.csproj`: 0 warnings (was 1). `dotnet test`: 563/563.
      `dotnet format` clean, shader-globals clean, `--selftest` 26/26.

## Technical Specs & Affected Files

| File | Change |
|---|---|
| `src/SLNG.Net/GridSession.cs` | `LoginAsync`'s success branch seeds `AccountMaturityMax`/`PreferredMaturity`; `SupportsMaturityPreference` converted from a dead stored flag to a live computed property; `SetPreferredMaturityAsync` now raises `MaturityPreferenceChanged`. |

### Design notes

- **Found by chasing a compiler warning to its actual cause, not by suppressing it.** A `#pragma
  warning disable` or deleting the unused-looking event would have silenced CS0067 while leaving
  the real, user-facing feature broken — the warning was correct that the event was unused; the
  bug was that it *should* have been used.
- **`SupportsMaturityPreference` is deliberately still uncached.** A per-login stored flag would go
  stale the moment the agent crosses into a region with a different capability set — the same
  reasoning `hasExt`/`hasLegacy` already use for the environment capabilities elsewhere in this
  file.

## What the tests guarantee

Nothing new — `SetPreferredMaturityAsync` against a live capability was already documented as not
unit-tested (`FEAT-SL-02`'s own spec: needs a connected client and a real Linden-grid capability,
which `tests-rules` reserves local OpenSim for, and OpenSim has none of this). The existing
`MaturityAccessTests` (wire codec both directions) are unaffected and still pass. This fix is a
wiring correction, not new decodable behaviour.

## Still open

- **Not yet re-verified in-world** — needs a real login on a Linden grid to confirm the
  Preferences page now shows the correct account ceiling immediately, that changing it updates the
  page live, and that OpenSim (no `UpdateAgentInformation` capability) still correctly shows
  "not supported" rather than a stale `true`.
