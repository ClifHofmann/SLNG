# [BUG-UI-35] Chat log text crushed horizontally in Communication window

- **Feature ID:** `BUG-UI-35`
- **Track:** `ui`
- **Status:** `✅ Done`
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](../ROADMAP.md)

## Overview & Goal

In `ChatWindow`, chat log messages appeared to "disappear" or wrap vertically as a 1-character-wide sliver of punctuation marks. This occurred after the conference participant panel (FEAT-UI-71) was added beside the log.

## Root Cause

`FEAT-UI-71` wrapped `_logView` (a `RichTextLabel`) and `BuildParticipantsPanel()` in an `HBoxContainer` (`logRow`) without setting `SizeFlagsHorizontal = SizeFlags.ExpandFill` on `_logView` or `logRow`.
In Godot's `HBoxContainer`, the horizontal axis is the main container axis. Children without `SizeFlags.Expand` are allocated only their minimum size. For an autowrapped `RichTextLabel`, its minimum width along the horizontal axis defaults to ~0 px (or a single glyph width). Consequently, `_logView` collapsed into a ~0 px to ~10 px sliver, forcing every character in every chat message to wrap vertically one character per line.

## Fix

1. In `app/scripts/UI/ChatWindow.cs`:
   - Set `SizeFlagsHorizontal = SizeFlags.ExpandFill` on `_logView`.
   - Set `SizeFlagsHorizontal = SizeFlags.ExpandFill` on `logRow`.
   - Expose `internal RichTextLabel LogView => _logView;` for test verification.
   - Localize remaining chat UI strings (outer tabs, jump button, action icons, system notices).
2. In `app/i18n/en-US.json` and `app/i18n/de-DE.json`:
   - Add localized keys for `tab_chat`, `tab_friends`, `tab_groups`, `tab_main`, `jump_to_latest`, `attach`, `history`, `give_item`, `voice_call`, `search`, and give-item notices.
3. In `app/scripts/SelfTest.cs`:
   - Add `CheckChatWindowLogLayout` to verify `_logView.SizeFlagsHorizontal` has `Expand` and layout width is >= 200 px.
4. In `app/scripts/Boot.cs`:
   - Bump `AppVersion` to `v0.26.108-alpha`.

## Acceptance Criteria

- [x] Chat messages in `ChatWindow` expand horizontally across the conversation area.
- [x] SelfTest `CheckChatWindowLogLayout` passes.
- [x] All 98 SelfTest checks and unit tests pass.
