# [BUG-UI-29] Groups list "Show in profile" toggles initially inactive until toggled

- **Feature ID:** `BUG-UI-29`
- **Track:** `ui` / `net`
- **Status:** `✅ Done`
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
In the Groups tab of the communication window (`GroupsPanel`), the "show in profile" (👤) toggle button was initially displayed inactive for all groups. Only when the user toggled one group did all values update and reflect their real state on the grid.

### Root Cause
1. `GroupsPanel.Initialize(session)` called `_session.RequestGroups()`, but that occurred in `ChatWindow.BindSession(session)` before `LoginAsync`, while `_client.Network.Connected` was `false`. The request was dropped as a no-op.
2. The initial server-side `AgentGroupDataUpdate` message pushed via EventQueue on login omits `NewGroupData` (`ListInProfile` flags), causing LibreMetaverse to default all `ListInProfile` booleans to `false`.
3. The simulator only sends the complete `AgentGroupDataUpdate` with `NewGroupData` when the client requests current groups (`AgentDataUpdateRequestPacket`). Because `RequestGroups()` was never called once connected, this only happened after the user toggled a group flag via `SetGroupAcceptNotices`.

### Fix
- In `GridSession.Session.cs`: Call `RequestGroups()` in `OnEventQueueRunning` as soon as the EventQueue capability is confirmed running for the simulator.
- In `Boot.cs`: Call `_session.RequestGroups()` immediately following login alongside `RequestMuteList()`.
- In `ChatWindow.cs`: Call `_session?.RequestGroups()` when switching to the Groups tab (`OnOuterTabSelected`).
- In `GroupsPanel.cs`: Hook `VisibilityChanged` to request current groups when visible in tree.
- In `GroupInfoTests.cs`: Add test verifying `OnCurrentGroups` preserves `ListInProfile` flags and publishes them in `GroupsUpdated`.

## Acceptance Criteria
- [x] Initial group membership load accurately reflects `ListInProfile` for each group without requiring manual toggle.
- [x] Switching to the Groups tab requests updated groups if connected.
- [x] Unit test verifying `OnCurrentGroups` preserves and broadcasts `ListInProfile`.
- [x] `dotnet build SLNG.sln`, `dotnet build app/SLNG.App.csproj`, `dotnet test`, `godot --headless --path app -- --selftest` all pass clean.

## Technical Specs & Affected Files
- `src/SLNG.Net/GridSession.Session.cs`
- `app/scripts/Boot.cs`
- `app/scripts/UI/ChatWindow.cs`
- `app/scripts/UI/GroupsPanel.cs`
- `tests/SLNG.Net.Tests/GroupInfoTests.cs`
