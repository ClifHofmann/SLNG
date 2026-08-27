# Bug: BUG-UI-03 (Camera Input State Stuck on Focus Loss)

## Context
When zooming with the camera using `Alt + Left Mouse Button (LMB)` and dragging the cursor outside the application window, returning to the window causes the camera controller to behave erratically.

## Technical Details
- **Symptom:** If the user releases `LMB` outside the window and then returns, pressing `Alt` again causes the camera zoom to jump. The engine still believes `LMB` is pressed because it missed the `InputEventMouseButton` (released) event.
- **Root Cause:** Godot's input state gets desynced when the mouse leaves the viewport or window bounds. The camera controller is not clearing its internal state (e.g., `is_zooming`, `is_panning`) when window focus or mouse focus is lost.
- **Solution:** The camera controller needs to listen for `NOTIFICATION_WM_WINDOW_FOCUS_OUT` or `NOTIFICATION_WM_MOUSE_EXIT` (or monitor Godot's focus events) and forcefully clear any active drag/zoom input states and reset tracked modifier keys.

## Acceptance Criteria
- [ ] Dragging the mouse outside the window while zooming (Alt+LMB) and releasing the button correctly aborts the zoom state.
- [ ] Returning to the window and pressing `Alt` does not cause a sudden camera jump.
- [ ] The camera controller robustly resets its input tracking variables upon losing window or mouse focus.
