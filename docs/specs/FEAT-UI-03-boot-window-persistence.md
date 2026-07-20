# [FEAT-UI-03] Boot Window State & Profile Persistence

- **Feature ID:** `FEAT-UI-03`
- **Track:** `ui`
- **Status:** `⏸️ Pending`
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
Enhance the startup (`Boot.cs`) experience by persisting window dimensions (width, height, maximize state) and pre-selecting the last used login profile upon client launch.

## Functional Requirements

### 1. Window Size & State Persistence
- **Storage:** Saved in `user://logins.cfg` under `[Window]` or `[Settings]` section.
- **Saved Properties:** `width`, `height`, `maximized`.
- **Behavior:**
  - On launch (`_Ready` in `Boot.cs`), check for saved window dimensions and apply via `DisplayServer.WindowSetSize()` / `DisplayServer.WindowSetMode()`.
  - Listen to window resize events (`GetTree().Root.SizeChanged`) or save during login/shutdown to store current window dimensions.

### 2. Last Profile Pre-Selection
- **Storage:** Saved in `user://logins.cfg` under `[Settings]` section as `last_profile`.
- **Behavior:**
  - When a profile is selected or a successful login is performed, update `last_profile`.
  - During `LoadProfiles()`, if `last_profile` exists in `_savedProfiles`, automatically select its index in `_profileDropdown` and trigger `OnProfileSelected()` to auto-fill grid and credentials.

## Acceptance Criteria
- [ ] Client launches with saved window dimensions from previous session.
- [ ] Last used login profile is automatically selected and pre-filled in the login dropdown.
- [ ] `user://logins.cfg` stores `[Settings]` with `last_profile`, `width`, `height`, `maximized`.

## Technical Specs & Affected Files
- `app/scripts/Boot.cs` — Boot scene script managing `user://logins.cfg` and window settings.
- `docs/specs/FEAT-UI-03-boot-window-persistence.md` — Feature specification.

## Sub-tasks / Progress
- [ ] Create `FEAT-UI-03` spec & update `ROADMAP.md`
- [ ] Implement `[Window]` settings load/save in `Boot.cs`
- [ ] Implement `last_profile` auto-select in `LoadProfiles()`
