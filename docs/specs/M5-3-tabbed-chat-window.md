# [M5-3] Tabbed Chat, Friends & Groups Window

- **Feature ID:** `M5-3`
- **Track:** `ui`
- **Status:** `🚧 In Progress`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
Implement a unified communication and social hub window inheriting from `SLNGWindow`. The window combines Local Region Chat, Direct Messaging (IMs), Friends List, and Group Memberships using a dual-axis (vertical + horizontal) tabbed layout.

## Functional Requirements

### 1. Window Architecture & Styling
- Must derive from `SLNG.App.UI.SLNGWindow` to maintain SLNG glassmorphism aesthetics, dragging, and title bar behavior.
- **Vertical Navigation Tabs (Left Sidebar):**
  - `Chat`: Primary chat interface.
  - `Friends`: Social contact management.
  - `Groups`: Joined group listing and management.

### 2. Vertical Tab: `Chat`
- **Horizontal Tabs (Top Bar inside Chat view):**
  - **`Main`**: Fixed, non-closeable tab representing Local Region Chat (`ChatFromSimulator` packets). Always present as the first horizontal tab.
  - **Dynamic User IM Tabs**: Individual horizontal tabs created dynamically per active direct message conversation with another avatar. Each tab displays the avatar name and an option to close `(x)`.
- **Chat Body:**
  - Rich text message log (timestamps, sender names, message content, system notices).
  - Message input text box with `Send` button (or `Enter` key trigger).
  - Unread message indicators on inactive horizontal tabs.

### 3. Vertical Tab: `Friends`
- Displays the user's friend list retrieved via `GridSession` / LibreMetaverse `FriendsManager`.
- **Status Indicators:**
  - Clear visual badges/icons marking each friend as `Online` (green indicator / highlighted text) or `Offline` (greyed out).
- **Interactions:**
  - Double-clicking a friend or selecting "IM" opens a dynamic user chat tab in the `Chat` vertical view and switches to it.

### 4. Vertical Tab: `Groups`
- Displays all Second Life / OpenSim groups in which the avatar holds membership (`GroupManager`).
- Displays group name, group badge/icon placeholder, and role/status.
- Allows opening group chat sessions or viewing group profiles.
- **Group Chat Mute / Ignore (Phase 2):** Toggle option per group (or in group chat tab header) to mute/ignore incoming group chat messages and prevent unread notifications or tab auto-opening.

### 5. Chat Logging & History (Original Viewer Parity)
- **SL Log Format Parity:** Appends messages asynchronously using standard viewer format: `[YYYY/MM/DD HH:MM:SS] Sender Name: Message`.
- **File Structure:** Separate log files per session:
  - Local Chat: `chat.txt`
  - IMs: `<Avatar_Name>.txt`
  - Group Chats: `<Group_Name>.txt`
- **Default Directory (User Space):**
  - Fallback default in OS user application data folder (e.g. `%APPDATA%\SLNG\logs\chat\` on Windows, `~/.config/slng/logs/chat/` on Linux).
- **Preferences Integration & Extensibility:**
  - Global toggle: `Enable Chat Logging` (default: On).
  - Path selector setting for custom log directory.
  - Extensible settings data structure for future preferences (timestamp format, per-chat logging flags, log retention/rotation).

## Acceptance Criteria
- [ ] Window inherits from `SLNGWindow` and opens via shortcut or UI button.
- [ ] Vertical tabs (`Chat`, `Friends`, `Groups`) switch active panel cleanly.
- [ ] Horizontal tabs under `Chat` show `Main` as static default, with new IM tabs opening on message receipt or manual IM initiate.
- [ ] Friends list correctly renders online vs. offline status indicators.
- [ ] Group list displays user's joined groups.
- [ ] Group chat mute/ignore toggle supported per group (prevents notifications/auto-tab popups).
- [ ] Chat logging writes incoming/outgoing messages to text files in original SL viewer format (`[YYYY/MM/DD HH:MM:SS]`).
- [ ] Custom chat log folder configurable in Preferences, defaulting to OS user space.
- [ ] All network callbacks from LibreMetaverse buffer events and update UI components on the Godot main thread without UI freezes.
- [ ] Unit / UI integration tests pass for tab management, message dispatching, and file logger output.

## Technical Specs & Affected Files
- `app/scripts/UI/ChatWindow.cs` — Main UI window class inheriting from `SLNGWindow`.
- `app/scripts/UI/FriendsPanel.cs` — Friends list sub-component.
- `app/scripts/UI/GroupsPanel.cs` — Group list sub-component.
- `src/SLNG.Core/Services/ChatLogger.cs` — Async file logging service matching SL format.
- `src/SLNG.Net/GridSession.cs` — Event exposed for incoming IMs, friend status changes, and group updates.
- `docs/specs/M5-3-tabbed-chat-window.md` — Feature specification.

## Sub-tasks / Progress
- [ ] Create `M5-3` spec & update `ROADMAP.md`
- [ ] Implement `ChatWindow` UI shell with vertical tab container
- [ ] Implement `Main` and dynamic horizontal IM tabs in `Chat` view
- [ ] Implement `Friends` panel with status indicators
- [ ] Implement `Groups` panel
- [ ] Add Group Chat Mute/Ignore toggle per group (suppresses notifications & tab focus)
- [ ] Implement `ChatLogger` service (SL format, user-space default path, async append)
- [ ] Connect Chat Logger & Chat Window options to Preferences
- [ ] Connect `SLNG.Net` events (buffered to Godot thread)

