# Polish: BUG-UI-04 (Adjustable Window Transparency)

## Context
The user requested less transparency across all UI windows and modals (*"alle modale/fenster sollten noch weniger transparenz haben"*) and later expanded this to a user-configurable setting. The current Glassmorphism style uses a fixed alpha level that can make the UI too sheer and hard to read against busy 3D backgrounds. 

## Requirements
1. **User Preference Slider:**
   - Add a slider in the Preferences UI (e.g., under a "UI" or "Graphics" tab) to control Window Opacity.
   - The value should be persisted in `preferences.cfg` (e.g., `[ui] window_opacity = 0.85`).
2. **Dynamic Theme Adjustment:**
   - `SLNGWindow` (or a central theme manager) must read this preference on boot and when changed.
   - Dynamically adjust the background color alpha of the `StyleBoxFlat` used for window backgrounds based on the slider value.
3. **Global Application:**
   - Ensure this change propagates to all inheriting windows (Inventory, Chat, Profiles, Camera Controls) without requiring per-window hardcodes.
4. **Blur Evaluation:**
   - Ensure the background blur (if active) still looks correct across different opacity levels.

## Acceptance Criteria
- [ ] The user can adjust UI window transparency via a slider in Preferences.
- [ ] The setting is saved and persists across sessions.
- [ ] The opacity updates live (or upon opening a new window) across all UI modals.
