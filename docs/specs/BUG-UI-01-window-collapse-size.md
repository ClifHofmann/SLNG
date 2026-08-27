# Bug: BUG-UI-01 (Window Collapse/Minimize Size)

- **Status:** `✅ Done` — confirmed in-world 2026-08-27
- **Owner:** `claude`

## Context
When minimizing or collapsing a UI window (like Chat or Settings), the window content is hidden, but the outer window frame remains at its original full size.

## Technical Details
- All floating UI windows inherit from the base class `SLNGWindow` (`SLNG.App.UI.SLNGWindow`), a `MarginContainer`.
- The minimize/collapse action currently hides the inner content container.
- **Root Cause (confirmed):** `SLNGWindow` is a `Container`, so its height is floored by
  `GetCombinedMinimumSize()` = `max(CustomMinimumSize.Y, inner-content min height)`. The old
  `ToggleMinimize` hid the content and then set `Size = new Vector2(Size.X, 0)` — but every
  subclass sets a `CustomMinimumSize` (CameraHUD `160×140`, ChatWindow `400×340`,
  PreferencesWindow `600×420`, …) which clamped that `0` straight back up to full height. The
  frame never shrank.

## Fix (`SLNGWindow.cs`, central — every subclass benefits)
- `ToggleMinimize` now tracks an explicit `_isMinimized` flag and, on minimize:
  1. saves `_preMinimizeSize` **and** `_preMinimizeMinSize` (the subclass's `CustomMinimumSize`),
  2. hides the content container + resize handle,
  3. drops the height floor to `0` while keeping the width floor (a window that also snapped
     narrow would truncate its own title),
  4. defers the actual resize to `ApplyMinimizedSize` — hiding the content invalidates the inner
     layout's minimum size, which isn't recomputed until the next layout pass, so a synchronous
     `Size =` still clamps against the pre-collapse height.
- `ApplyMinimizedSize` (deferred) sets `Size.Y = GetCombinedMinimumSize().Y` (≈ the header
  height, ~48 px), leaving width untouched. Guarded on `_isMinimized` so a rapid
  minimize→restore double-click before the deferred call lands is a no-op.
- Restore path: put `CustomMinimumSize` back to `_preMinimizeMinSize`, show content + handle,
  restore `Size = _preMinimizeSize` (growing clamps fine synchronously).
- `SavePersistedGeometry` persists `_preMinimizeSize` instead of the live collapsed `Size` while
  minimized, so a `PersistId` window dragged while minimized still reopens full-height next
  session instead of stranded as a tiny bar.

## Acceptance Criteria
- [x] Clicking the minimize/collapse button hides the window content.
- [x] The outer window frame automatically shrinks vertically to only show the title bar / header.
- [x] Expanding the window restores both the content visibility and the previous outer frame size.
- [x] The fix is applied centrally in `SLNGWindow.cs` so all inheriting windows automatically benefit from it.

## Verification
- `dotnet build app/SLNG.App.csproj` — 0 errors, 0 warnings.
- `dotnet test SLNG.sln` — 322 tests green.
- `godot --headless --path app -- --selftest` — 24/24 (also fixed a pre-existing unrelated
  miss: `ui.preferences.theme_heading` was absent from `de-DE.json`).
- **Confirmed in-world 2026-08-27** — minimizing collapses the frame to the title bar and
  restores cleanly.
