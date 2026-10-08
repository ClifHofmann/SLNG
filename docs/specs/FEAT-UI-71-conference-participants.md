# [FEAT-UI-71] Conference participant list

- **Feature ID:** `FEAT-UI-71`
- **Track:** `ui`
- **Status:** `🧪 Review`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
An ad-hoc conference tab can show who is in the conversation, like the participant list of the Second Life
viewer's conversation window. Asked for 2026-10-08 while testing BUG-NET-32.

## Acceptance Criteria
- [x] A conference tab has a people button in the action row; it is hidden on every other tab.
- [x] The button shows or hides a member list beside the log, per tab, off by default.
- [x] The list shows each member's Display Name (legacy name until it is known), sorted, with the count in the header; we ourselves are marked.
- [x] Right-click on a member: Show profile, Instant message (disabled for ourselves).
- [x] The list follows joins, leaves and first-time speakers without reopening the tab.
- [x] Unit tests for the member bookkeeping.
- [x] Verified live on Second Life (2026-10-08).

## Technical Specs & Affected Files
- `GridSession.GetConferenceMembers(sessionId)`: LibreMetaverse's `GroupChatSessions` (filled by the grid's
  agent-list updates) united with everybody who spoke and ourselves. The grid's initial member list is the reply to
  the accept request, which LibreMetaverse discards, so speakers are the fallback for people who never speak up.
- `GridSession.ConferenceMembersChanged` (`ConferenceMembersChangedEvent`, `SLNG.Core`): raised on a network thread by
  `ChatSessionMemberAdded/Left` and by a first-time speaker; `Boot` marshals it to `ChatWindow.OnConferenceMembersChanged`.
- `app/scripts/UI/ChatWindow.cs`: `BuildParticipantsPanel`, `RefreshParticipants`; i18n keys `ui.chat.participant*`.
- Not done: listing people who have joined but never spoken when the grid sends no agent-list update; moderation, mute and "mention" entries of the viewer's menu.
