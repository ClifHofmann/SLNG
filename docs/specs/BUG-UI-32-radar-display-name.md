# [BUG-UI-32] Radar shows legacy username instead of Display Name

- **Feature ID:** `BUG-UI-32`
- **Track:** `ui`
- **Status:** `✅ Done`
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
In the Radar / Minimap table, avatars were shown with their legacy username (e.g. `Kim197539`) rather than their chosen Display Name (e.g. `Kimi die Hummel`), whereas opening their Profile correctly showed their Display Name. The Radar table Name column must show the avatar's Display Name (adhering to Display Name preferences), with tooltips displaying both Display Name and username when they differ, and search filtering supporting both names.

### Root Causes
1. **MinimapOverlay ignored Display Name lookup in `ResolveName`:** `ResolveName(agentId)` for coarse-location avatars only checked `_session.TryGetCachedName` (legacy name cache) and stripped the "Resident" suffix. It never checked `TryGetDisplayName`, never called `NameDisplay.For`, and never called `_session.RequestDisplayName`.
2. **MinimapOverlay in-world avatars never requested Display Name:** `AvatarDisplayName` on `AvatarComponent` was static and called `AvatarNames.ForList(avatar.DisplayName, avatar.FirstName, avatar.LastName)`. If `avatar.DisplayName` was not yet populated or resolved, it fell back to legacy name without asking the session or calling `RequestDisplayName`.
3. **`NameDisplay.For` did not strip "Resident" before name comparison:** If `legacyName` was `"Kim197539 Resident"`, `PersonNameDisplay.Choose` received `"Kim197539 Resident"` rather than `"Kim197539"`.
4. **Radar table filtering only matched `Name`:** `RadarTable.Filter` only checked `r.Name`, so filtering by username when display name was shown (or vice versa) did not find the avatar.

### Fix
- In `src/SLNG.Core/RadarRow.cs`:
  - Added optional `string? Username = null` parameter to `RadarRow`.
- In `src/SLNG.Core/RadarTable.cs`:
  - Updated `RadarTable.Filter` to match case-insensitively on either `Name` or `Username`.
- In `app/scripts/UI/NameDisplay.cs`:
  - In `NameDisplay.For`: clean `legacyName` with `AvatarNames.WithoutDefaultLastName` before calling `PersonNameDisplay.Choose`.
- In `app/scripts/UI/MinimapOverlay.cs`:
  - Updated `RosterEntry` to record `Username`.
  - Replaced static `AvatarDisplayName` with instance `AvatarDisplayNameAndUsername(AvatarComponent avatar)`: resolves via `NameDisplay.For(_session, avatar.AgentId, legacy)` which requests and looks up display names and respects the user's Display Name preferences.
  - Replaced `ResolveName` with `ResolveNameAndUsername(Guid agentId)`: requests and resolves both legacy name and display name, returning the display name (or legacy name if no display name set).
  - Populated `RadarRow.Username` in `BuildRow`.
- In `app/scripts/UI/RadarTableView.cs`:
  - In `FillRow` for `RadarColumn.Name`: set cell tooltip to `"{row.Name} ({row.Username})"` when the username differs from the display name, or `{row.Name}` otherwise.
- In `tests/SLNG.Core.Tests/RadarTableTests.cs`:
  - Added `Filtering_matches_either_display_name_or_username` testing filter matches on display name and username.
- In `app/scripts/Boot.cs`:
  - Bump `AppVersion` to `v0.26.97-alpha`.

## Acceptance Criteria
- [x] Radar table Name column displays the avatar's Display Name (e.g. `Kimi die Hummel`).
- [x] Tooltip on the Name cell displays `Display Name (Username)` when they differ.
- [x] Avatars outside draw distance (coarse locations) resolve both legacy and display names.
- [x] Radar search filter matches against both Display Name and Username.
- [x] Unit tests pass (2843 passed), `godot --headless --path app -- --selftest` (96/96 checks passed), `dotnet format` clean.

## Technical Specs & Affected Files
- `src/SLNG.Core/RadarRow.cs`
- `src/SLNG.Core/RadarTable.cs`
- `app/scripts/UI/NameDisplay.cs`
- `app/scripts/UI/MinimapOverlay.cs`
- `app/scripts/UI/RadarTableView.cs`
- `tests/SLNG.Core.Tests/RadarTableTests.cs`
- `app/scripts/Boot.cs`
