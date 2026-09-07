# [BUG-UI-06] Login screen: profile dropdown shows a raw URL, grid dropdown disagrees with it

- **Feature ID:** `BUG-UI-06`
- **Track:** `ui`
- **Status:** `✅ Done` — confirmed in-world 2026-09-07: selecting a saved profile shows a readable "Name @ Gridname" and the grid dropdown follows it.
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)
- **Branch:** `feature/FEAT-SL-01-second-life-readiness`
- **Version:** `v0.20.5-alpha`

## Overview & Goal

User, from a login-screen screenshot: the saved-profile dropdown showed
`purisViewer resident @ https://login.agni.lindenlab.com/cgi-bin/login.cgi` — a raw login URL
where a grid name belongs — and, worse, the separate grid dropdown right below it was showing
"OSGrid" at the same time the actual login URL underneath was Agni's: **"Das was oben ausgewählt
ist sollte auch in der Auswahl stehen."**

## Root cause

Two related gaps in `Boot.cs`'s login-screen wiring, both in code that predates this session:

1. **The saved-profile dropdown's visible text was literally the `ConfigFile` section key** —
   `$"{creds.FirstName} {creds.LastName} @ {creds.GridLoginUri}"`, built once at save time
   (the login-success handler) and never reformatted for display. A login URL is not a grid name.
2. **Selecting a saved profile never touched the grid dropdown.** `OnProfileSelected` sets
   `_gridInput.Text` (the actual login URL used to connect) straight from the saved config, but the
   separate `GridDropdown` `OptionButton` keeps whatever it was last showing — there was no code
   path that ever moved its selection to match. `GridDropdown`'s own `ItemSelected` handler only
   goes one way (dropdown → text field); nothing went the other way (loaded text field → dropdown).
   A user who last touched the OSGrid entry, then picked a saved Second-Life profile, saw exactly
   the screenshot: "Second Life" content underneath, "OSGrid" still highlighted above it.

## Acceptance Criteria

- [x] The saved-profile dropdown shows `"{first} {last} / {grid name}"` (e.g.
      `purisViewer Resident / Second Life`), never a raw URL.
- [x] The grid name used matches `GridDropdown`'s own wording exactly (reuses its item text, not a
      second, possibly-inconsistent naming scheme).
- [x] An unrecognized/custom grid (typed by hand, not one of the four `GridDropdown` entries)
      falls back to showing just its host, never the full `login.cgi` path.
- [x] Selecting a saved profile — including the automatic one at boot, restoring the last-used
      profile — updates `GridDropdown`'s selection to match the loaded login URL, or clears the
      selection entirely when it's a custom grid that doesn't match any entry.
- [x] The underlying `ConfigFile` section-key format is untouched, so an existing `logins.cfg` from
      before this fix keeps loading correctly — only what's displayed changed, not how profiles are
      identified/stored.
- [x] `dotnet build SLNG.sln` / `app/SLNG.App.csproj`: 0 warnings. `dotnet test`: 563/563.
      `dotnet format` clean, shader-globals clean, `--selftest` 26/26.

## Technical Specs & Affected Files

| File | Change |
|---|---|
| `app/scripts/Boot.cs` | New `GetGridDisplayName(string?)` (URI → `GridDropdown`'s own item text, or the host as a fallback) and `SyncGridDropdownToUri(string?)` (selects the matching `GridDropdown` entry, or clears selection). `LoadProfiles()` now builds each dropdown item's text from the profile's stored `first`/`last`/`grid` fields via `GetGridDisplayName` instead of using the raw section key. `OnProfileSelected` calls `SyncGridDropdownToUri` after loading `_gridInput.Text`. |

### Design notes

- **Display changed, storage didn't.** The `ConfigFile` section name (`"{first} {last} @
  {uri}"`) is still what identifies a saved profile internally (`_savedProfiles`, `last_profile`) —
  reformatting *that* would risk losing track of existing users' saved logins on upgrade for a
  purely cosmetic ask. Only the dropdown's visible text is computed fresh from the profile's own
  stored fields every time `LoadProfiles()` runs.
- **One source of truth for grid names.** `GetGridDisplayName` reads `GridDropdown`'s own items
  rather than a second, separately-maintained name table — the exact bug being fixed (two UI
  elements disagreeing about the same grid) would have been trivial to reintroduce with a
  duplicated list.
- **`OptionButton.Select()` doesn't re-emit `ItemSelected`** (confirmed against Godot's own
  behavior before relying on it) — `SyncGridDropdownToUri` calling `_gridDropdown.Select(i)` cannot
  loop back into `GridDropdown`'s own handler and stomp `_gridInput.Text` right after it was set.

## What the tests guarantee

Nothing new — this is Godot UI wiring (`app/scripts`, outside `tests-rules`' `src/`-only scope) with
no `SLNG.Core`/`SLNG.Net`/`SLNG.Assets` surface touched. The full 563-test suite passing unmodified
confirms no engine-agnostic behaviour changed.

## Still open

- **Not yet re-verified in-world** — needs an actual look at the login screen with at least one
  saved profile on a Linden grid and one on OSGrid, confirming both the dropdown text and the grid
  selection now agree in every case (including the automatic last-profile restore at boot, and a
  custom/unrecognized grid clearing the selection rather than showing a wrong one).
