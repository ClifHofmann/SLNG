# Bug: BUG-UI-01 (Window Collapse/Minimize Size)

## Context
When minimizing or collapsing a UI window (like Chat or Settings), the window content is hidden, but the outer window frame remains at its original full size.

## Technical Details
- All floating UI windows inherit from the base class `SLNGWindow` (e.g., `SLNG.App.UI.SLNGWindow`).
- The minimize/collapse action currently hides the inner content container.
- **Root Cause:** The outer Godot `Control` or `PanelContainer` node is not dynamically resizing to wrap only the remaining header. Its `size` or `custom_minimum_size` is likely keeping it expanded.

## Acceptance Criteria
- [ ] Clicking the minimize/collapse button hides the window content.
- [ ] The outer window frame automatically shrinks vertically to only show the title bar / header.
- [ ] Expanding the window restores both the content visibility and the previous outer frame size.
- [ ] The fix is applied centrally in `SLNGWindow.cs` (or its associated Godot scene) so all inheriting windows automatically benefit from it.
