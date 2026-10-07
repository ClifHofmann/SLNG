# [FEAT-UI-70] Chat URLs, SLurls, Resident Name Resolution, @ Mentions & Emoji Picker

- **Feature ID:** `FEAT-UI-70`
- **Track:** `ui` / `core`
- **Status:** `✅ Done`
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
Modern Second Life and Firestorm viewers provide rich communication affordances:
1. **URL & SLurl Resolution:** Web links (`http://`, `https://`) and Second Life URLs (`secondlife:///app/agent/...`, `secondlife:///app/group/...`, `secondlife:///app/teleport/...`) are auto-detected and rendered as clickable hyperlinks in chat history and notification windows.
2. **Resident Name Resolution:** Avatar names in chat and notification bodies can be clicked to open profiles.
3. **@ Mentions:** Typing `@` in the chat input opens an autocomplete popup listing nearby avatars, friends, and active conversation participants. Mentioned users are highlighted in chat history, and tagging the logged-in user triggers an alert/highlight.
4. **Emoji Picker & Shortcodes:** Dedicated emoji button next to the chat bar opening a categorized emoji picker (Smileys, People, Animals, Food, Travel, Objects, Symbols) that inserts Unicode emojis at cursor position, plus common emoji shortcodes.

## Acceptance Criteria
- [x] **URL Detection:** `http://...` and `https://...` in chat messages and notifications are rendered as clickable links that open in the default web browser via `OS.ShellOpen`.
- [x] **SLurl Support:** `secondlife:///app/agent/<uuid>/about`, `secondlife:///app/group/<uuid>/about`, and `secondlife:///app/teleport/<sim>/<x>/<y>/<z>` are parsed and trigger profile view, group info, or teleporting.
- [x] **Notifications Integration:** `NotificationWindow` formats URLs and clickable links in entry texts and details.
- [x] **@ Mentions Autocomplete:** Typing `@` in `ChatWindow`'s input field shows an autocomplete popup with nearby avatars and friends. Selecting an entry inserts `@DisplayName` or `@Username`.
- [x] **@ Mention Highlighting:** Messages containing `@Name` format the tag with highlight coloring; messages mentioning the current user are emphasized.
- [x] **Emoji Picker:** The `mood` button in `ChatWindow` toggles an `EmojiPickerWindow` / popup that allows selecting emojis by category or search.
- [x] **Pure Core Parser Tests:** `SLNG.Core` unit tests for chat text parsing, URL extraction, BBCode escaping, and mention detection.

## Technical Specs & Affected Files
- `src/SLNG.Core/ChatTextParser.cs` (pure domain parser for URLs, SLurls, mentions, emojis)
- `tests/SLNG.Core.Tests/ChatTextParserTests.cs` (unit tests)
- `app/scripts/UI/ChatWindow.cs` (RichTextLabel meta handler, mention autocomplete integration, emoji button)
- `app/scripts/UI/ChatMentionPicker.cs` (autocomplete popup for @ mentions)
- `app/scripts/UI/EmojiPickerWindow.cs` (emoji picker popup with categorized grids and search)
- `app/scripts/UI/NotificationWindow.cs` (clickable URLs in notification list and details)
- `app/scripts/Boot.cs` (teleport/profile dispatch if needed, AppVersion bump)
- `app/i18n/en-US.json` and `app/i18n/de-DE.json` (localization)

## Sub-tasks / Progress
- [x] Phase 1: `ChatTextParser` in `SLNG.Core` + Unit tests for URLs, SLurls, mentions & emojis.
- [x] Phase 2: Wire `ChatWindow` and `NotificationWindow` for clickable Web URLs and SLurls.
- [x] Phase 3: Implement `@` Mention Autocomplete picker in `ChatWindow`.
- [x] Phase 4: Implement `EmojiPickerWindow` and wire the emoji button in `ChatWindow`.
- [x] Phase 5: Verification (`dotnet build`, `dotnet test`, selftest), documentation and commit.
