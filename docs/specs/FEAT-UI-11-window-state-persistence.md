# Feature: FEAT-UI-11 (Window State Persistence & Viewport Bounding)

## Context
Floating UI windows (like Camera Controls, Chat, Settings) need to remember their position and size. Furthermore, if the main application window is resized, floating windows must not get lost outside the visible screen area (off-screen).

## Requirements
1. **State Persistence:** 
   - Every `SLNGWindow` instance must persist its `Position` and `Size` (e.g., to `user://logins.cfg` or a dedicated config file) when moved or resized.
   - Upon opening, a window should restore this saved position and size.
2. **Viewport Bounding (Clamping):**
   - Whenever the main Godot Viewport/Window is resized, all active `SLNGWindow` nodes must validate their position against the new screen bounds.
   - If a window is positioned outside the visible area, it must be automatically clamped/pushed back into the visible viewport.
   - When restoring a saved position on boot, it must also be clamped to ensure it doesn't spawn off-screen if the user switched to a smaller resolution.

## Acceptance Criteria
- [ ] Closing and re-opening a floating window restores its exact previous position and size.
- [ ] Restarting the client entirely retains these window layouts across sessions.
- [ ] Shrinking the main app window dynamically pushes any out-of-bounds floating windows back into the visible area.
- [ ] The logic is centrally implemented in the `SLNGWindow` base class (or a WindowManager singleton), automatically applying to all inheriting UI windows.
