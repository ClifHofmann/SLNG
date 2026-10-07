# [FEAT-UI-69] Friend Online/Offline Status in Main Chat

- **Feature ID:** `FEAT-UI-69`
- **Track:** `ui`
- **Status:** `🧪 Review`
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
Firestorm and other viewers allow users to see friend presence transitions (when someone from the friends list goes online or offline) directly inside the Main / Local chat tab. This feature introduces an optional preference toggle and formats friend online/offline announcements as clean, non-intrusive system notices in the Main chat tab with clickable avatar profile links.

## Acceptance Criteria
- [x] Setting `friend_presence_in_local_chat` added to `UiSettings` (default: `false`, persisted in `preferences.cfg`).
- [x] Checkbox added to `DisplayPreferencesPage` under the existing friend toast toggle.
- [x] Friend presence notices appear in the Main ("main") chat tab when the setting is enabled.
- [x] Does not increment unread chat counter (`countUnread: false`).
- [x] Friend names in the notice link to their avatar profile (`[url=avatar:<guid>]`).
- [x] Notice is localized in English and German (`en-US.json` and `de-DE.json`).
- [x] Unit tests pass, `AppVersion` bumped.

## Technical Specs & Affected Files
- `app/scripts/UI/UiSettings.cs`
- `app/scripts/UI/DisplayPreferencesPage.cs`
- `app/scripts/UI/ChatWindow.cs`
- `app/scripts/Boot.cs`
- `app/i18n/en-US.json`
- `app/i18n/de-DE.json`
