# [BUG-UI-30] Profile displays (unknown avatar) and IM from profile shows empty history

- **Feature ID:** `BUG-UI-30`
- **Track:** `ui` / `net`
- **Status:** `✅ Done`
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
Clicking on an avatar link in chat (e.g. when an avatar comes online or speaks) opened their profile window displaying `(unknown avatar)` and their UUID. Clicking the "IM" button on that profile window subsequently opened an empty IM chat tab, even when opening the chat for that same avatar via the Friends list displayed the full conversation history.

### Root Causes
1. **Link Click lacked Name:** `ChatWindow.OnLogMetaClicked` parsed `avatar:<guid>` and invoked `OnOpenProfileRequested?.Invoke(id, "")`, passing an empty string as the name.
2. **Missing Cache Lookup on Open:** `UserProfileWindow.Initialize` set `_agentName = ""` and called `_session.RequestAvatarName(id)`. Because the avatar was already in cache or friends list, `HasCachedName` returned true and dropped the request without firing `NameResolved`. `UserProfileWindow.OnNameResolved` was never called, leaving `_nameLabel` as `(unknown avatar)`.
3. **Empty Name broke Log Lookup:** Clicking "IM" in `UserProfileWindow` called `OnOpenImRequested` with the empty name. `ChatWindow.GetOrCreateImTab` passed the empty name to `NameDisplay.LegacyFor`, which returned `""`. Because `tab.LogName` was set to `""`, `_logger.GetTail` looked for an empty log name on disk rather than the resident's actual log file (`<legacy name>.txt`), failing to preload any history.
4. **Cached Names in Network Check:** `GridSession.RequestAvatarName` / `RequestGroupName` checked `!_client.Network.Connected` before evaluating `HasCachedName`, and did not invoke `NameResolved` if the name was already present in the local cache or friend list.

### Fix
- In `src/SLNG.Net/GridSession.Chat.cs`:
  - `TryGetCachedName`: check `_client.Friends.FriendList` in addition to `_nameCache`, cache the name, and return true.
  - `HasCachedName`: consider friend names in `FriendList` as cached.
  - `RequestAvatarName` and `RequestGroupName`: if already cached, dispatch `NameResolved` immediately without network roundtrip and without requiring active connection.
  - `GetFriends`: populate `_nameCache` when iterating friends list.
- In `app/scripts/UI/NameDisplay.cs`:
  - `For`: fallback to cached name or friends list if legacyName is empty.
  - `LegacyFor`: robust resolution from cached name and friend list when name is empty or passed as display name.
- In `app/scripts/UI/UserProfileWindow.cs`:
  - In `Initialize`: resolve name from session display name / cache / friends list immediately if initialName is empty; also request display name.
  - In `UpdateNameHeader`: format header using `NameDisplay.For` and cached fallback.
  - In `OnImPressed` / `OnPayPressed`: resolve avatar name before invoking request callbacks.
- In `app/scripts/UI/ChatWindow.cs`:
  - `OnLogMetaClicked`: lookup avatar name from session before raising profile request.
  - `GetOrCreateImTab`: repair empty/stale `LogName` on existing tabs and preload history if not previously loaded.
  - `RefreshNames`: repair empty `LogName` and preload history when name is resolved.
- In `app/scripts/Boot.cs`:
  - `OpenUserProfileWindow`: resolve avatar name from session if passed name is empty.
  - Bump `AppVersion` to `v0.26.95-alpha`.
- In `tests/SLNG.Net.Tests/FriendPermissionsTests.cs`:
  - Add tests for `TryGetCachedName_ResolvesNameFromFriendsList` and `RequestAvatarName_RaisesNameResolvedWhenAlreadyInFriendsList`.

## Acceptance Criteria
- [x] Clicking avatar links in chat resolves the resident's name immediately in the profile window.
- [x] Profile header and window title display the avatar name instead of `(unknown avatar)`.
- [x] Opening IM from profile window resolves the legacy name and preloads chat history from disk.
- [x] Opening IM via Friends list and via profile produce identical tabs with full history.
- [x] Unit tests pass (2842 passed), `godot --headless --path app -- --selftest` passes clean.

## Technical Specs & Affected Files
- `src/SLNG.Net/GridSession.Chat.cs`
- `app/scripts/Boot.cs`
- `app/scripts/UI/ChatWindow.cs`
- `app/scripts/UI/NameDisplay.cs`
- `app/scripts/UI/UserProfileWindow.cs`
- `tests/SLNG.Net.Tests/FriendPermissionsTests.cs`
