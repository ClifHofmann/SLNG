# Feature: FEAT-UI-15 (Conversation History & Chat Logging)

## Context
For social interaction, users need to see who they have recently spoken with (Recent Conversations) and be able to scroll back through past Instant Messages (IMs) or Local Chat.

## Requirements
1. **Local Chat Logging:**
   - Implement an automated logging system that saves all incoming and outgoing IMs to the local disk (e.g., `user://logs/chat/<AvatarName>.txt` or a database).
   - Include timestamps and sender names.
2. **Recent Conversations UI:**
   - Within the Tabbed Chat & Friends window (`M5-3`), add a "Recent" or "History" view that lists avatars the user has recently interacted with.
3. **History Restoration:**
   - When a user opens an IM session with an avatar, the client should parse the local log file and automatically populate the chat window with the last N messages (e.g., the last 50 lines) to provide conversation context.
4. **Privacy Settings:**
   - Add a toggle in the Preferences window (`FEAT-UI-10`) to enable/disable chat logging completely, and a button to clear all existing logs.

## Acceptance Criteria
- [ ] IMs are securely saved to local files per conversation partner.
- [ ] The UI displays a list of recent conversation partners.
- [ ] Opening a new IM tab automatically loads the recent history from disk.
- [ ] Users can disable chat logging entirely via settings.
