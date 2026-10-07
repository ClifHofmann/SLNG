# [BUG-UI-31] Windows occluded behind TopMenu bar and Favorites bar

- **Feature ID:** `BUG-UI-31`
- **Track:** `ui`
- **Status:** `✅ Done`
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
Windows like the Chat Log History window (`ChatHistoryWindow`) and newly spawned dialogs were opening occluded behind the top menu bar (`TopMenu`) and favorites bar (`FavoritesBar`) with their title bars unreachable under Layer 100 menu controls. Window positioning and boundary enforcement must be clean and consistent for ALL floating windows across the viewer.

### Root Causes
1. **RestorePersistedGeometry skipped non-persisted windows:** In `SLNGWindow.cs`, `RestorePersistedGeometry()` checked `if (string.IsNullOrEmpty(PersistId)) return;` before calling `ClampToViewport()`. Windows without a `PersistId` (such as `ChatHistoryWindow`, `ConfirmWindow`, `TextPromptWindow`, etc.) never clamped to the viewport or `TopInset` upon opening.
2. **Default Position (0, 0) under top bars:** Newly instantiated Controls default to `Position = Vector2.Zero`. Without an explicit placement or viewport clamping, windows opened flush at (0, 0), which placed their headers directly underneath the top menu bar and favorites bar.
3. **No dynamic re-clamp on TopInset change:** `SLNGWindow.TopInset` had a static field without notifications. When `TopMenu` became visible on login or when `FavoritesBar` toggled on/off, open windows were never re-clamped against the new inset.
4. **Resizing by Top Edge could bypass TopInset:** `ApplyResize` allowed resizing from the top edge upwards into `y < TopInset`.
5. **Dialog placement formulas lacked TopInset floor:** Modal dialogs and cascade handlers used `Mathf.Max(0, (viewport.Y - Size.Y) / 3)` rather than `Mathf.Max(TopInset + 10f, ...)`.

### Fix
- In `app/scripts/UI/SLNGWindow.cs`:
  - `TopInset`: add setter that fires `OnTopInsetChanged()`, iterating all windows in `WindowGroupName` ("slng_windows") and invoking `win.ClampToViewport()`.
  - `RestorePersistedGeometry`: run unconditionally for all windows. If not restored from config and `Position == Vector2.Zero`, compute default placement centered horizontally and below `TopInset` (`posY = Mathf.Max(TopInset + 10f, (vp.Y - Size.Y) / 3f)`). Unconditionally call `ClampToViewport()`.
  - `ClampToViewport`: make `public`, ensure `minY = Mathf.Max(TopInset, 0f)`.
  - `ApplyResize`: clamp top-edge resize against `TopInset` so top edge cannot be resized behind the top menu bar; call `ClampToViewport()` upon completion.
- In `app/scripts/UI/TopMenu.cs`:
  - Robust `ApplyTopInsetDeferred()` and `TotalHeight`: use `Mathf.Max(_panel.Size.Y, _panel.GetCombinedMinimumSize().Y)` with a minimum floor of 54 px when `_favoritesBar.Visible` or 28 px when hidden.
  - Updating `SLNGWindow.TopInset` immediately triggers re-clamping of all active windows.
- In `app/scripts/UI/ChatWindow.cs`:
  - `OpenHistoryFor` and `OnHistoryPressed`: spawn `ChatHistoryWindow` offset from `ChatWindow` with `Y >= TopInset + 10f` and call `EnsureOnScreen()`.
- Across all dialog and cascade windows:
  - `ConfirmWindow`, `TextPromptWindow`, `MfaPromptWindow`, `GroupInvitationWindow`, `GroupInfoWindow`, `FriendshipOfferWindow`, `FriendshipRequestWindow`, `InventoryOfferWindow`, `TeleportOfferWindow`, `ScriptPermissionWindow`, `ScriptDialogWindow`, `RegionRestartWindow`, `TermsOfServiceWindow`, `PayWindowBase`, `UserProfileWindow`, `ObjectEditWindow`:
  - Use `Mathf.Max(TopInset + 10f, ...)` and call `ClampToViewport()`.
- In `app/scripts/SelfTest.cs`:
  - Add `CheckWindowTopInsetClamping` verifying that windows clamp `Y >= TopInset`, never open under top menus, and dynamically re-clamp on `TopInset` updates.
- In `app/scripts/Boot.cs`:
  - Bump `AppVersion` to `v0.26.96-alpha`.

## Acceptance Criteria
- [x] `ChatHistoryWindow` opens visibly below the top menu bar and favorites bar, never at (0, 0).
- [x] All floating windows inherit `SLNGWindow` clamp against `TopInset` on initial show, whether `PersistId` is set or not.
- [x] Windows cannot be dragged or resized above `TopInset`.
- [x] Toggling FavoritesBar or logging in dynamically re-clamps all currently open windows to stay visible.
- [x] All 96 SelfTest checks pass (`CheckWindowTopInsetClamping`), `dotnet test` (2842 passed), `dotnet format` clean.

## Technical Specs & Affected Files
- `app/scripts/UI/SLNGWindow.cs`
- `app/scripts/UI/TopMenu.cs`
- `app/scripts/UI/ChatWindow.cs`
- `app/scripts/UI/ConfirmWindow.cs`
- `app/scripts/UI/TextPromptWindow.cs`
- `app/scripts/UI/MfaPromptWindow.cs`
- `app/scripts/UI/GroupInvitationWindow.cs`
- `app/scripts/UI/GroupInfoWindow.cs`
- `app/scripts/UI/FriendshipOfferWindow.cs`
- `app/scripts/UI/FriendshipRequestWindow.cs`
- `app/scripts/UI/InventoryOfferWindow.cs`
- `app/scripts/UI/TeleportOfferWindow.cs`
- `app/scripts/UI/ScriptPermissionWindow.cs`
- `app/scripts/UI/ScriptDialogWindow.cs`
- `app/scripts/UI/RegionRestartWindow.cs`
- `app/scripts/UI/TermsOfServiceWindow.cs`
- `app/scripts/UI/PayWindowBase.cs`
- `app/scripts/UI/UserProfileWindow.cs`
- `app/scripts/UI/ObjectEditWindow.cs`
- `app/scripts/SelfTest.cs`
- `app/scripts/Boot.cs`
