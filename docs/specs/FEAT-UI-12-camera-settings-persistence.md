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
- [ ] Changing the camera FOV or offset persists the new values to the configuration file immediately or upon closing the window.
- [ ] Restarting the viewer restores the exact custom camera FOV and offset.
- [ ] These settings integrate seamlessly with the existing `FEAT-UI-03` / `FEAT-UI-07` preference saving infrastructure.
