# Feature: FEAT-RENDER-07 (Depth of Field / DoF)

## Context
As a later addition (specifically for the Photography and Machinima tools), a Depth of Field (DoF) effect should be implemented. This allows the camera to focus on a specific subject (e.g., the avatar or a selected object) while applying a cinematic blur to the out-of-focus background and foreground.

## Requirements
1. **Engine Integration:**
   - Utilize Godot's built-in Depth of Field post-processing capabilities (accessible via `Camera3D` attributes or `WorldEnvironment`).
2. **Focus Mechanics:**
   - **Auto-Focus:** Raycast from the center of the viewport (or mouse position) to dynamically adjust the focal distance to the hit object.
   - **Manual Focus:** Allow the user to manually set the focal distance and aperture/blur intensity (F-Stop equivalent).
3. **UI Integration:**
   - Add toggles and sliders for DoF (Enabled, Focus Distance, Blur Amount) to the upcoming Photography/Snapshot UI (`MVP3-4`).

## Acceptance Criteria
- [ ] Depth of Field can be cleanly toggled on and off without artifacts.
- [ ] Auto-focus reliably finds the distance to the subject being looked at.
- [ ] UI sliders provide smooth manual control over the blur intensity and focal plane.
- [ ] The feature is cleanly integrated into the `MVP 3` Snapshot Studio toolset.
