# Feature: FEAT-UI-12 (Camera Settings Persistence)

## Context
Currently, custom camera parameters such as the viewing angle (FOV) and camera offset (position relative to the avatar/target) reset between sessions. These settings need to be saved and restored to improve the user experience.

## Requirements
1. **Settings Persistence:**
   - Extend the existing preference persistence system (e.g., `preferences.cfg` or user profile config) to store camera-specific variables.
   - Specifically track: `Camera.FOV` (Blickwinkel) and `Camera.Offset` (Kamera-Offset).
2. **Settings Restoration:**
   - When the client boots or the camera controller initializes, it must load and apply these saved values instead of reverting to hardcoded defaults.
3. **Settings UI (Camera / Preferences):**
   - If not already exposed, provide UI inputs (sliders/spinboxes) in the Camera Settings or Preferences window to adjust these values.

## Acceptance Criteria
- [x] Changing the camera FOV or offset persists the new values to the configuration file immediately or upon closing the window.
- [x] Restarting the viewer restores the exact custom camera FOV and offset.
- [x] These settings integrate seamlessly with the existing `FEAT-UI-03` / `FEAT-UI-07` preference saving infrastructure.

## Implementation notes

- `CameraSettings` (`app/scripts/UI/CameraSettings.cs`) — same `user://preferences.cfg` `[camera]`
  section as the BUG-UI-02 pad-speed multipliers. New: `Fov` (50–100°, def 75), `RearDistance`
  (1–20 m, def 4), `FocusHeight` (0.5–3 m, def 1.8 — replaces the hardcoded eye-height in
  `AvatarController`). Every `Set*` clamps before persisting; `Load` clamps each read.
- `CameraPreferencesPage` "View" group: three sliders (° / m formatting) + the shared reset.
- `AvatarController.SetCameraSettings` (wired from `Boot` post-login) applies FOV + distance at
  once; `_Process` re-applies FOV and re-snaps `RearDistance` → `_zoom` whenever the setting
  *changes* (so the sliders are live), tracked apart from wheel/pad zoom. `ResetCamera` uses
  `RearDistance`. Confirmed in-world 2026-08-27 (the rear-distance live re-apply was a follow-up
  fix, `4fc727e`, after the first pass only applied it at login/Esc).
