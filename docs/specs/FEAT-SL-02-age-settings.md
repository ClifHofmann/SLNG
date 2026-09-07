# [FEAT-SL-02] Age settings — Second Life content-rating preference

- **Feature ID:** `FEAT-SL-02`
- **Track:** `net` / `ui`
- **Status:** `✅ Done` — confirmed in-world 2026-09-07 (Agni), with BUG-NET-10: the Age Settings tab shows the account's real ceiling, options above it disabled, and a maturity change is accepted and reflected.
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)
- **Branch:** `feature/FEAT-SL-01-second-life-readiness` (continues the same SL-readiness branch)
- **Version:** `v0.20.0-alpha`

## Overview & Goal

Second Life gates which regions and content an account is shown by a per-account content-rating
preference — General, Moderate, or Adult — separate from whatever an account is actually verified
for. SLNG had no equivalent of the reference viewer's Preferences → General maturity checkboxes at
all: nothing read the account's own ceiling, nothing let a user raise or lower the preference, and
without it an account defaults to General and simply cannot see Moderate or Adult regions —
regardless of what the account is entitled to. Requested directly: "Ich brauch irgendwo die Age
settings im viewer."

This is **not a TPV compliance gap** (nothing in the policy requires it); it is a feature needed to
use Second Life at all once past login.

## Protocol, verified against the reference viewer

Three fields tie together, confirmed against `secondlife/viewer` source (`llstartup.cpp`,
`llagent.cpp`, `llagentaccess.cpp`, `llviewerregion.cpp`), not assumed:

- **`agent_access_max`** (login response) — the account's own verified ceiling. *"this is their
  actual ability to access content"* (`llstartup.cpp`). Read once via
  `gAgent.setMaturity(text[0])`.
- **`agent_region_access`** (login response) — the account's CURRENT preference at login,
  *"always <= agent_access_max"*. Seeds the local `PreferredMaturity` setting.
- **`UpdateAgentInformation`** capability — `POST {"access_prefs":{"max": "PG"|"M"|"A"}}` to
  change the preference later (`LLAgent::sendMaturityPreferenceToServer`). The response echoes
  back the ACTUAL value the server accepted, which can be lower than requested — the server
  clamps to the account's verified ceiling exactly the way `LLAgentAccess::setMaturity` clamps a
  stale local preference after re-verification.

Wire codes, confirmed in `indra_constants.h` / `llviewerregion.cpp`: `SIM_ACCESS_PG=13` ↔ `"PG"`,
`SIM_ACCESS_MATURE=21` ↔ `"M"` (displayed as "Moderate" in the UI, "Mature" on the wire — same
thing), `SIM_ACCESS_ADULT=42` ↔ `"A"`. `LLAgentAccess::convertTextToMaturity` reads only the
**first character** of a field — `"PG"[0]` is `'P'` — which is why the decoder here does the same
rather than a full-string match.

**OpenSim has no equivalent.** Grepped the fetched OpenSim source (`scratch/opensim_fetch`) for
`UpdateAgentInformation`: zero matches. No capability is ever registered, so the feature is
inert there by construction, not by a special case in SLNG's own code — the capability URI is
simply never present.

## Acceptance Criteria

- [x] The account's verified ceiling and current preference are parsed from the login response.
- [x] A preferences tab ("Age Settings" / "Altersfreigabe") shows the current preference and lets
      it be changed, options above the account's ceiling shown disabled rather than hidden (so an
      unverified account sees *why* Adult isn't offered, matching the reference viewer's own
      dialog).
- [x] Changing it POSTs to `UpdateAgentInformation` and adopts whatever the server actually
      granted — never blindly trusts the requested value.
- [x] On a grid with no such capability (OpenSim), the tab says so instead of showing a control
      that would silently do nothing.
- [x] Unit tests written and passing (270 in `SLNG.Net.Tests`, up from 255).

## Technical Specs & Affected Files

| File | Change |
|---|---|
| `src/SLNG.Core/MaturityLevel.cs` | new — `General`/`Moderate`/`Adult`, engine- and protocol-neutral |
| `src/SLNG.Net/MaturityAccess.cs` | new — wire short-string codec, matches the reference viewer's mapping exactly |
| `src/SLNG.Net/GridSession.cs` | `AccountMaturityMax`, `PreferredMaturity`, `SupportsMaturityPreference`, `MaturityPreferenceChanged` event, `SetPreferredMaturityAsync`; login-response parsing |
| `app/scripts/UI/MaturityPreferencesPage.cs` | new — the preferences tab |
| `app/scripts/Boot.cs` | tab registration in `SetupHud`; `BindSession` call in the post-login block (mirrors `_inventoryPanel?.Initialize(_session)`) |
| `app/i18n/*.json` | `ui.preferences.tab_maturity`, `ui.preferences.maturity_*` (10 keys) |
| `tests/SLNG.Net.Tests/MaturityAccessTests.cs` | new — codec round-trip, `GridSession` defaults, refusal when disconnected |

### Design notes

- **`MaturityLevel`'s ordinal order is load-bearing.** `General < Moderate < Adult` lets
  `MaturityPreferencesPage.Refresh` disable a dropdown entry with a plain `level > max` instead of
  a separate severity table. Documented on the enum itself so it isn't an accident someone removes.
- **Session-bound like `InventoryPanel`, not like `EnvironmentWindow`.** The page is constructed
  once at boot (before any session exists) and rebound via `BindSession(_session)` after every
  successful login, because the account's own verified ceiling is only known once a login response
  has actually arrived — the same reasoning `_inventoryPanel?.Initialize(_session)` already
  follows for the same reason.
- **`MaturityPreferenceChanged` closes the loop on a server-side clamp.** If the server refuses
  the requested level and returns a lower one, the page must reflect that even though the change
  wasn't initiated by a fresh `ApplyAsync` call reading its own result — the event covers both
  paths (a login and a POST) with one subscription.
- **Disabled options, not hidden ones.** Mirrors the reference viewer: an unverified account sees
  "Adult" present but greyed out, with the status line explaining the account's actual ceiling,
  rather than a menu that silently has one fewer item and no explanation.

## What the tests guarantee

`MaturityAccessTests` pins the wire codec both directions (including the first-character-only
quirk `"PG"` → `'P'` → General, and OpenSim's empty-field case → General), that
`MaturityLevel`'s ordinal order is what the UI's ceiling comparison depends on, and that a fresh
`GridSession` defaults to General/no-support before any login rather than an unset or unsafe
value. `SetPreferredMaturityAsync` against a live capability is **not** unit-tested — same
reasoning as `RegionHasServerSideBaking()` in `FEAT-SL-01`: it needs a connected client and a real
Linden-grid capability, which `tests-rules` reserves for local OpenSim, and OpenSim has no such
capability to test against. The Aditi login is what exercises it for real.

## Found live on Aditi, fixed same session

First real-grid test surfaced exactly the kind of bug this spec's original "Still open" section
anticipated: opened right after login, the tab said *"Dieses Grid unterstützt keine
Inhaltseinstufung"* — on Aditi, a grid that plainly does support it. Root cause:
`SupportsMaturityPreference` reads the region's capability list live, but `BindSession` (called
from `OnLoginPressed`'s success branch) runs before the capability seed necessarily resolves —
the exact same `SimConnected`-vs-`EventQueueRunning` race `GridSession` already documents for the
environment capabilities, just never applied to this page. `Refresh()`'s result was then cached
in the UI with nothing to invalidate it once the caps actually arrived.

Fixed by making `Refresh()` public and calling it again every time Preferences opens (Boot's
`OnOpenPreferences`), matching the codebase's own existing pattern for the same class of problem
(`_qualityPage?.Refresh(); _designPage?.Refresh();` there already, for hotkey-driven staleness).
Not a full fix for the narrowest possible window (opening Preferences within the first instant
after login, before caps resolve, could still show the stale message once) — but self-heals on
the next open, and in practice caps resolve well before a user navigates to Preferences.

## Still open

- **The narrowest race window above is still theoretically possible** — nothing currently
  invalidates the page proactively the moment capabilities actually resolve; it only refreshes on
  next open. A `GridSession` event tied to `EventQueueRunning` would close this fully if it
  recurs.
- **Whether the server's clamp behaviour matches expectation** for an account with no age
  verification at all (does `agent_access_max` arrive as `"PG"` for such an account, or something
  else?) is unobserved.
