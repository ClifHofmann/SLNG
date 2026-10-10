# [BUG-UI-41] Friend categories lack user and grid isolation

- **Feature ID:** `BUG-UI-41`
- **Track:** `ui`
- **Status:** `✅ Done`
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
The Friends panel (`FriendsPanel` and `FriendCategoryStore`) persisted custom categories, assignments, fold states, and view preferences without grid scoping and initialized `_agentId` prior to login when `session.AgentId` was `00000000-0000-0000-0000-000000000000` (`Guid.Empty`). As a result, all accounts across all grids and servers shared the single section `friend_categories_00000000-0000-0000-0000-000000000000`, leaking friend categories between different residents and grids. Custom categories and preferences must be strictly isolated per user and per grid (`friend_categories_{grid}_{agentId}`).

### Root Causes
1. **Pre-login Initialization with Empty AgentId:** In `ChatWindow.BindSession(session)`, `_friendsPanel.Initialize(session)` was invoked before `LoginWithPromptsAsync` completed. At that moment, `session.AgentId` was `Guid.Empty` (`00000000-0000-0000-0000-000000000000`), and `FriendsPanel` was never re-initialized or bound to the resolved account upon successful login.
2. **No Grid Scoping in Storage:** `FriendCategoryStore` keyed sections as `friend_categories_<agentId>`, omitting the grid login URI / slug.
3. **No Session Cleanup on Logout:** `FriendsPanel` was never reset on disconnect or logout. When switching accounts or grids, in-memory category state persisted into subsequent sessions.

### Fix
- In `src/SLNG.Net/GridSession.cs` & `src/SLNG.Net/GridSession.Session.cs`:
  - Exposed `GridLoginUri` and `GridSlug` on `GridSession`, recorded at the start of `LoginAsync`.
- In `app/scripts/UI/FriendCategoryStore.cs`:
  - `GetSection(gridSlug, agentId)` returns `null` if either `gridSlug` or `agentId` is null/empty or `agentId` parses to `Guid.Empty`.
  - Strictly keys sections as `$"friend_categories_{gridSlug.Trim().ToLowerInvariant()}_{agentId.Trim().ToLowerInvariant()}"`.
  - Added automatic cleanup of legacy corrupted empty-guid sections (`friend_categories_00000000-0000-0000-0000-000000000000`) and migration of legacy un-scoped valid agent sections.
  - Provided in-memory `ConfigFile` methods (`LoadFromConfig`, `SaveToConfig`, `LoadOnlyOnlineFromConfig`, `SaveOnlyOnlineToConfig`, `LoadShowCategoriesFromConfig`, `SaveShowCategoriesToConfig`) for hermetic testing.
- In `app/scripts/UI/FriendsPanel.cs`:
  - Added `_gridSlug` and `_agentId` tracking.
  - Added `InitializeAccount(gridSlug, agentId)` to load categories and view settings for a specific user and grid.
  - Added `Reset()` to wipe in-memory categories and reset view switches on logout/session change.
  - Auto-initializes on `OnFriendListChanged` if `_agentId` was not yet set but the session has authenticated.
  - Guarded saves against missing or empty `gridSlug`/`agentId`.
- In `app/scripts/UI/ChatWindow.cs`:
  - Added `InitializeFriends(gridSlug, agentId)` and `ResetFriends()`.
  - Calls `ResetFriends()` in `ResetForNewSession()`.
- In `app/scripts/Boot.cs`:
  - Calls `_chatWindow.InitializeFriends(gridSlug, _myAgentId.ToString())` upon successful login.
  - Calls `_chatWindow?.ResetFriends()` upon disconnect / `QuitGracefully`.
  - Bumped `AppVersion` to `v0.27.35-alpha`.
- In `app/scripts/SelfTest.FriendCategories.cs` & `app/scripts/SelfTest.cs`:
  - Added `CheckFriendCategoryStore` verifying:
    - Section naming, rejection of nulls/whitespace and `Guid.Empty`.
    - Strict isolation of categories, `only_online`, and `show_categories` across distinct grids and users.
    - Zero leakage from empty-guid corrupted sections.

## Acceptance Criteria
- [x] Friend categories and view toggles are strictly scoped by grid and agent ID (`friend_categories_{grid}_{agentId}`).
- [x] Corrupted empty-guid sections (`00000000-0000-0000-0000-000000000000`) are rejected and cleaned up.
- [x] Friends panel resets on logout and reconnects cleanly on new logins without state leakage.
- [x] SelfTest check `friend categories user/grid isolation` passes (127/127 total checks pass).
- [x] `dotnet build SLNG.sln`, `dotnet build app/SLNG.App.csproj`, `dotnet test` (3015 passed), `dotnet format` clean.

## Technical Specs & Affected Files
- `src/SLNG.Net/GridSession.cs`
- `src/SLNG.Net/GridSession.Session.cs`
- `app/scripts/UI/FriendCategoryStore.cs`
- `app/scripts/UI/FriendsPanel.cs`
- `app/scripts/UI/ChatWindow.cs`
- `app/scripts/Boot.cs`
- `app/scripts/SelfTest.FriendCategories.cs`
- `app/scripts/SelfTest.cs`
