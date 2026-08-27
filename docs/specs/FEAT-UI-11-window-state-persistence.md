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
- [x] Closing and re-opening a floating window restores its exact previous position and size.
- [x] Restarting the client entirely retains these window layouts across sessions.
- [x] Shrinking the main app window dynamically pushes any out-of-bounds floating windows back into the visible area.
- [x] The logic is centrally implemented in the `SLNGWindow` base class (or a WindowManager singleton), automatically applying to all inheriting UI windows.

## Implementation notes

- Persistence already existed in `SLNGWindow` (`PersistId`, `RestorePersistedGeometry`,
  `SavePersistedGeometry`, `user://preferences.cfg` `[window_geometry]`, save on drag/resize end).
  This task added the viewport re-clamp and rolled `PersistId` out.
- `SLNGWindow._Ready` subscribes to `GetViewport().SizeChanged`; the handler re-clamps and, only
  if the window actually moved, persists the corrected spot (so dragging the app-window edge
  doesn't hammer the config once per resize event per open window). Unsubscribed in `_ExitTree`
  (the root Viewport outlives every window).
- `ClampToViewport` keeps ≥ 40 px of the top-left (title bar) on screen; footprint is
  `Size * Scale` so it is correct under FEAT-UI-07 UI scaling. Used by both restore and resize.
- `PersistId` set on the singleton windows: `camera_hud`, `preferences` (pre-existing), `chat`,
  `inventory`, `create_landmark`. **Not** persisted: `ObjectEditWindow` / `ItemPropertiesWindow` /
  `ChatHistoryWindow` (multiple can be open at once — a shared slot would stack them);
  `ScriptDialogWindow` already uses a per-object id.
