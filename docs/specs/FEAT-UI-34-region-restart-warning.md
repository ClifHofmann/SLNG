# [FEAT-UI-34] Region restart warning — countdown, and a way out

- **Feature ID:** `FEAT-UI-34`
- **Track:** `ui` / `net`
- **Status:** `🧪 Review`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
A region restart is the one alert with a deadline: miss it and the session ends where you
stand. It used to arrive as a single line among others. It now also opens a popup that names
the region, counts down, and offers a teleport to one of the resident's own landmarks — the
behaviour of the reference viewer's `LLFloaterRegionRestarting` plus Firestorm's landmark
dropdown. The notification-window entry stays as the record.

## Protocol
`AlertMessage` with `NotificationId` `RegionRestartMinutes` or `RegionRestartSeconds` and an
LLSD block with `MINUTES`/`SECONDS` and `NAME` (llviewermessage.cpp:5127-5159). Minutes ×60.
LibreMetaverse already hands over `NotificationId` and `ExtraParams`; `GridSession.OnAlertMessage`
used to drop both. The LLSD stays inside `SLNG.Net`, the rest of the app sees a neutral
`RegionRestartEvent(RegionName, Seconds)`.

## Acceptance Criteria
- [x] A restart notice opens a window naming the region with a live countdown.
- [x] A second notice updates that window instead of stacking a second one.
- [x] Teleporting away (any region change) closes it.
- [x] The alert text is still in the notification window afterwards.
- [x] Unit tests for the alert parsing and the countdown; selftest builds the window in a real tree.
- [ ] Confirmed in-world with a real restart that gives advance notice.

## Decisions
- The countdown is a **deadline read against a monotonic clock** (`RegionRestartCountdown`), not
  a number decremented per frame, so a stalled frame cannot make it drift. It rounds up, so `0:01`
  shows until the last moment.
- A notice whose number is missing is still a restart notice: it comes out as `0:00` instead of
  being dropped. The reference viewer does the same (`asInteger()` of an absent key is 0).
- **Landmarks:** `GridSession.GetLandmarksAsync` loads the Landmarks folder subtree first (a fresh
  login would otherwise offer an empty list exactly while the clock runs), then collects landmarks
  from the cached inventory outside Trash, sorted by name. A landmark without a resolved asset id
  is left out: the wire protocol reads an empty landmark id as "teleport home".
- **Not reproduced:** the camera shake and `UISndRestart`. The shake is optional in Firestorm for
  a reason — UI that moves the camera is something some people cannot stand. If wanted, add it as a
  setting, off by default.
- The taskbar flash uses `DisplayServer.WindowRequestAttention()`.
- **Home is always the first entry and the default** (v0.24.88): it needs nothing from the inventory,
  so Teleport works the moment the window opens and there is a way out with no landmarks at all.
  `GridSession.TeleportHomeAsync` sends the landmark teleport with an empty id, as the reference
  viewer's `teleportHome` does (LibreMetaverse: `GoHomeAsync`). What the grid does with an unset or
  unusable home is its decision.
- The popup closes when the session ends: its teleport needs a live session, and it used to sit
  exactly under the "You have been logged out" dialog.

## Technical Specs & Affected Files
- `src/SLNG.Core/RegionRestartAlert.cs`, `RegionRestartCountdown.cs`, `GridEvents.cs` (`RegionRestartEvent`)
- `src/SLNG.Net/GridSession.cs` (`RegionRestartReceived`), `GridSession.Chat.cs` (`OnAlertMessage`),
  `GridSession.Inventory.cs` (`GetLandmarksAsync`)
- `app/scripts/UI/RegionRestartWindow.cs`, `app/scripts/Boot.cs`, `app/scripts/SelfTest.cs`
- `app/i18n/en-US.json`, `de-DE.json` (`ui.region_restart.*`)
- `tests/SLNG.Core.Tests/RegionRestartTests.cs`

## Sub-tasks / Progress
- [x] Parse the alert, neutral event
- [x] Countdown model + tests
- [x] Landmark list
- [x] Window, wiring in Boot, strings (de/en)
- [ ] In-world confirmation
