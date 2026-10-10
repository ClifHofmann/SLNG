# [BUG-GRID-02] Complete per-avatar and per-grid isolation: group mutes, avatar hover height, local profile notes, caches & notifications

- **Feature ID:** `BUG-GRID-02`
- **Track:** `ui/core/net`
- **Status:** `✅ Done`
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
Following `BUG-UI-41` (friend categories grid/avatar isolation), an audit of all persisted configurations and in-memory caches identified several resources that were still shared globally or leaked across logins:
1. **Group Chat Mute Settings (`GroupMuteSettings`):** Saved under un-scoped `[group_mute]` in `user://preferences.cfg` and kept in static in-memory state across session resets. Muted groups leaked across distinct users and grids.
2. **Avatar Hover Height (`AvatarHoverSettings`):** Saved under un-scoped `[avatar] hover_height` in `user://preferences.cfg`. Loaded globally pre-login, applying one avatar's rig/shoe offset to all subsequent accounts.
3. **Local Avatar Profile Notes (`UserProfileWindow`):** Read and written under un-scoped `[avatar_notes]` and `[avatar_notes_imported]` in `user://preferences.cfg`, keyed only by target profile GUID. Notes written by one user leaked to other accounts viewing the same profile.
4. **Inventory & Display Name Caches (`Boot.cs`):** Hardcoded to global `user://cache/inventory` and `user://cache/displaynames`, failing to isolate caches by grid slug like `ObjectCacheDirectory`.
5. **In-Memory Notification History (`NotificationStore`):** Notifications in `_notifications` were never purged on logout or session reset, leaking private notices, transactions, group announcements, and system alerts to subsequent logins.
6. **Open Profile, Group Info, Object Edit & Landmark Windows:** Profile, group info, and object edit windows and landmark lists were not closed or reset on disconnect/logout, leaving previous account state standing behind the login screen.

## Fix
- In `app/scripts/GridData.cs`:
  - Added `InventoryCacheKind = "inventory"` and `DisplayNameCacheKind = "displaynames"`.
  - Added `InventoryCacheDirectory(string? gridUri)` and `DisplayNameCacheDirectory(string? gridUri)` resolving under `user://grids/<slug>/cache/...`.
- In `app/scripts/UI/GroupMuteSettings.cs`:
  - Scoped sections to `group_mute_{gridSlug}_{agentId}` via `GetSection(gridSlug, agentId)`.
  - Added `Initialize(gridSlug, agentId)` and `Reset()`.
  - Added one-time migration from legacy un-scoped `[group_mute]` and cleaned up corrupted empty-guid sections.
  - Guarded persistent writes with active session section and `Persist` flag.
- In `app/scripts/UI/AvatarHoverSettings.cs`:
  - Scoped sections to `avatar_hover_{gridSlug}_{agentId}` via `GetSection(gridSlug, agentId)`.
  - Added `Load(gridSlug, agentId)` and `Reset()`.
  - Added one-time migration from legacy un-scoped `[avatar]` `hover_height` and cleaned up corrupted empty-guid sections.
  - Guarded persistent writes with active session section and `Persist` flag.
- In `app/scripts/UI/UserProfileWindow.cs`:
  - Scoped notes sections to `avatar_notes_{gridSlug}_{agentId}` and import markers to `avatar_notes_imported_{gridSlug}_{agentId}` via `GetNotesSection` and `GetNotesImportedSection`.
  - Cleaned up corrupted empty-guid sections.
  - Guarded writes with `Persist` flag.
- In `app/scripts/UI/LandmarksWindow.cs` & `app/scripts/UI/LandmarkDedupWindow.cs`:
  - Added `Reset()` methods to clear session references, cached items, and duplicate groups upon logout.
- In `app/scripts/Boot.cs`:
  - Routed inventory and display name cache directories to `GridData.InventoryCacheDirectory(_sessionGridUri)` and `GridData.DisplayNameCacheDirectory(_sessionGridUri)`.
  - In `StartSessionAsync` and `QuitGracefully`:
    - Purged notification history via `_notifications.DismissAll()`, cleared `_notificationActions`, `_groupInviteActionKeys`, and `_inventoryOfferActionKeys`.
    - Closed all `_userProfileWindows`, `_groupInfoWindows`, and `_objectEditWindow`.
    - Reset `_chatWindow` (`ResetForNewSession()`), `_landmarksWindow` (`Reset()`), `_landmarkDedupWindow` (`Reset()`).
    - Reset `GroupMuteSettings.Reset()`, `_avatarHoverSettings.Reset()`, and refreshed `_avatarHoverWindow`.
  - Upon login success:
    - Initialized `GroupMuteSettings.Initialize(gridSlug, _myAgentId.ToString())`.
    - Loaded `_avatarHoverSettings.Load(gridSlug, _myAgentId.ToString())` and refreshed `_avatarHoverWindow`.
  - Bumped `AppVersion` to `v0.27.36-alpha`.
- In `app/scripts/SelfTest.PerAvatarIsolation.cs` & `app/scripts/SelfTest.cs`:
  - Added `CheckPerAvatarDataIsolation` verifying:
    - Section naming, rejection of nulls/whitespace and `Guid.Empty` across GroupMute, AvatarHover, and ProfileNotes.
    - Memory isolation and reset transitions for GroupMuteSettings and AvatarHoverSettings.
    - GridData per-grid cache paths for inventory and display names.
    - NotificationStore `DismissAll()` total purge.

## Acceptance Criteria
- [x] Group mutes, hover heights, profile notes, caches, and notifications strictly isolated per avatar and grid.
- [x] Logout completely clears in-memory state, notifications, and open inspection windows.
- [x] SelfTest check `per-avatar data isolation` passes (128/128 total checks pass).
- [x] `dotnet build SLNG.sln`, `dotnet build app/SLNG.App.csproj`, `dotnet test` (3015 passed), `dotnet format` clean.

## Technical Specs & Affected Files
- `app/scripts/GridData.cs`
- `app/scripts/UI/GroupMuteSettings.cs`
- `app/scripts/UI/AvatarHoverSettings.cs`
- `app/scripts/UI/UserProfileWindow.cs`
- `app/scripts/UI/LandmarksWindow.cs`
- `app/scripts/UI/LandmarkDedupWindow.cs`
- `app/scripts/Boot.cs`
- `app/scripts/SelfTest.PerAvatarIsolation.cs`
- `app/scripts/SelfTest.cs`
