# Bug: BUG-UI-02 (Camera Tools Tilt Issues)

## Context
The user-facing Camera Tools (HUD / Controls) have two specific bugs related to the camera "Tilt" function.

## Technical Details
1. **Inverted Input:** The left and right tilt controls are inverted. Triggering a left tilt moves the camera right, and vice versa.
2. **Zoom Lockout:** The tilt function stops working completely when the camera is currently in a zoomed-in state (e.g., after focusing on an object).
   - **Root Cause Hypothesis:** The zoom state is likely overriding the camera's transform matrix, locking the rotation, or the input handler for tilt is being blocked/ignored while a zoom target is active.

## Acceptance Criteria
- [ ] Left and right tilt inputs are mapped to the correct visual rotation directions.
- [ ] The tilt function operates correctly even when the camera is zoomed in on a focus point.
