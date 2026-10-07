# [BUG-UI-33] Favorites bar lacks user and grid isolation

- **Feature ID:** `BUG-UI-33`
- **Track:** `ui`
- **Status:** `✅ Done`
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
The horizontal Favorites Bar (`FavoritesBar`) persisted favorites without grid scoping and fell back to a shared global section (`favorites_bar`), leaking landmarks across different residents and grids. Furthermore, the bar was initialized on startup before login (`Initialize(null)`) and was never cleared on disconnect or logout. Landmark assets and items only exist on their native grid; cross-grid teleports are invalid and different avatars must maintain completely independent favorites lists. The Favorites Bar must be strictly isolated per user and per grid (`favorites_bar_{grid}_{agentId}`).

### Root Causes
1. **Pre-login Initialization with Empty AgentId:** In `Boot.cs`, `_topMenu.FavoritesBar.Initialize` was called at line 3824 before `LoginWithPromptsAsync` had run. At that point, `_session.AgentId` was `00000000-0000-0000-0000-000000000000` (`Guid.Empty`), and was never re-initialized after login. Consequently, every avatar on the same grid was mapped to `favorites_bar_<grid>_00000000-0000-0000-0000-000000000000`, causing all users to share the exact same favorites list!
2. **No Grid Scoping in Storage:** `LandmarkFavoritesStore` keyed sections only by `agentId` (`favorites_bar_<agentId>`), completely ignoring the grid login URI / slug.
3. **Fallback to `GlobalSection`:** In `LandmarkFavoritesStore.Load`, if a user had no saved favorites yet, it fell back to `GlobalSection` (`favorites_bar`). Any initial entries or legacy test entries were automatically inherited by every newly logged in user on any grid.
4. **Premature Startup Initialization:** In `FavoritesBar._Ready()`, `Initialize(null)` was called at boot, loading `GlobalSection` and persisting unauthenticated edits to `GlobalSection`.
5. **No Session Cleanup on Logout:** `FavoritesBar` was never reset on disconnect or logout in `Boot.cs`. When switching accounts or grids, previous favorites remained visible in the bar.

### Fix
- In `app/scripts/UI/LandmarkFavoritesStore.cs`:
  - Removed `GlobalSection` and all global fallbacks.
  - `GetSection(gridSlug, agentId)` returns `null` if either `gridSlug` or `agentId` is null/empty or `agentId` parses to `Guid.Empty`.
  - Strictly keys sections as `$"favorites_bar_{gridSlug.Trim().ToLowerInvariant()}_{agentId.Trim().ToLowerInvariant()}"`.
  - Added automatic cleanup of legacy corrupted sections (`favorites_bar` and `*_00000000-0000-0000-0000-000000000000`) in `Load`.
  - Added in-memory `ConfigFile` methods (`LoadFromConfig`, `SaveToConfig`, `LoadVisibleFromConfig`, `SaveVisibleToConfig`) for hermetic testing without altering developer preferences.
- In `app/scripts/UI/FavoritesBar.cs`:
  - Tracks `_gridSlug` alongside `_agentId`.
  - In `_Ready()`: calls `Reset()` instead of `Initialize(null)`.
  - `Initialize(gridSlug, agentId)` loads only the section matching `(gridSlug, agentId)`.
  - Added `Reset()`: clears items, resets state, and sets `Visible = false`.
  - `AddFavorite` and `RemoveFavorite` reject operations if `_gridSlug` or `_agentId` is null or empty.
- In `app/scripts/Boot.cs`:
  - Removed pre-login initialization of `FavoritesBar` and `LandmarkDedupWindow`.
  - Initialized `FavoritesBar` and `LandmarkDedupWindow` in `if (result.Success)` after login completes, passing `_myAgentId.ToString()` and the resolved grid slug.
  - On disconnect / logout (`QuitGracefully`), calls `_topMenu.FavoritesBar.Reset()`.
  - Bumped `AppVersion` to `v0.26.99-alpha`.
- In `app/scripts/SelfTest.FavoritesBar.cs` & `app/scripts/SelfTest.cs`:
  - Added `CheckLandmarkFavoritesStore` check verifying:
    - Section naming and grid/user differentiation.
    - Rejection of `Guid.Empty` agent ids.
    - Zero leakage across grids and users.
    - Rejection of legacy global section.
    - Rejection of unauthenticated favorite additions and proper reset behavior.

## Acceptance Criteria
- [x] Favorites bar strictly scopes favorites by both grid and resident (`favorites_bar_{grid}_{agentId}`).
- [x] No fallback to shared or global section; new users or alternate grids start with an empty favorites bar.
- [x] Favorites bar resets and clears on disconnect and logout; no leftover landmarks visible between sessions.
- [x] Unauthenticated drops/additions are rejected.
- [x] SelfTest check `favorites bar user/grid isolation` passes (97/97 total checks pass).
- [x] `dotnet build SLNG.sln`, `dotnet build app/SLNG.App.csproj`, `dotnet test` (2843 passed), `dotnet format` clean.

## Technical Specs & Affected Files
- `app/scripts/UI/LandmarkFavoritesStore.cs`
- `app/scripts/UI/FavoritesBar.cs`
- `app/scripts/Boot.cs`
- `app/scripts/SelfTest.FavoritesBar.cs`
- `app/scripts/SelfTest.cs`
