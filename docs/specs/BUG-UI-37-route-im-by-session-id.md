# BUG-UI-37 — Route incoming IMs by foreign session ID

**Status:** ✅ Done
**Date:** 2026-10-09
**Milestone:** Post-M5
**AppVersion:** v0.27.21-alpha

## Context & Problem

In v0.27.19, group chat lines from certain Second Life groups (such as "LAQ updates") arrived as:
`dialog=MessageFromAgent`, `groupFlag=False`, `session=<group UUID>` (`groupMember=True`), `bucketName="LAQ updates"`, `lmvKnowsSession=False`.

Previously, `GridSession.OnInstantMessage` only recognized group or conference messages if `dialog` was `SessionSend`/`SessionAdd`/`SessionGroupStart`, `GroupIM` was true, or LibreMetaverse already had the session registered in `GroupChatSessions`. Because none of these held, the incoming message fell through to `InstantMessageReceived`.

`Boot.OnInstantMessageReceived` then dropped the `IMSessionID` and called `ChatWindow.AppendInstantMessage`, causing `ChatWindow` to open a 1:1 tab for each individual speaker instead of routing to the group tab. Similarly, foreign typing indicator packets into unknown sessions opened 1:1 tabs for the typist.

## Reference Viewer Parity

In the Linden Lab reference viewer (`llimview.cpp:4233-4296`):
1. A session is keyed strictly by its `session_id`.
2. A foreign session ID (`SessionIds.IsForeign`) that matches a group the agent is a member of routes to that group's chat session.
3. Any other foreign session ID routes to an ad-hoc conference session named by the binary bucket (`bucketName`).
4. Incoming sessions not yet active are accepted the way `ChatterBoxInvitation` is handled: registered in `GroupChatSessions` and accepted via `ChatSessionRequest` capability (`_client.Self.ChatterBoxAcceptInviteAsync(session_id)`).
5. 1:1 conversations use the peer-to-peer XOR ID (`SessionIds.PeerToPeer`), zero, or agent IDs (`!SessionIds.IsForeign`) and remain completely unchanged.

## Implementation Details

1. **Typing Indicators (`GridSession.Chat.cs`):**
   - Check `SessionIds.IsForeign(imSession, self, typist)`.
   - If true, treat as `isConferenceOrGroup = true` so foreign typing packets do not open 1:1 tabs.

2. **1:1 XOR ID Path (`GridSession.Chat.cs`):**
   - If `!isForeign && !e.IM.GroupIM`:
     - Keep 1:1 path unchanged (`InstantMessageReceived`).

3. **Foreign Sessions (`GridSession.Chat.cs`):**
   - Check group mute first: if `GroupChatIgnored?.Invoke(sessionId) == true`, silently consume if text is present and leave session.
   - Auto-accept/register in LibreMetaverse: if `!lmvKnowsSession`, register in `GroupChatSessions` and call `_client.Self.ChatterBoxAcceptInviteAsync(e.IM.IMSessionID)`.
   - Determine group membership: `inMembership || e.IM.GroupIM || (membership == null && lmvKnowsSession)`.
   - If group: route to `GroupChatMessageReceived` with group name resolved from membership/bucket/cache.
   - If conference: route to `ConferenceChatMessageReceived` with session name decoded from binary bucket.

4. **Dialog recognition (`GroupChatSessionLogic.cs`):**
   - Include `InstantMessageDialog.MessageFromAgent` in `IsSessionLineDialog` so group messages arriving with dialog `MessageFromAgent` are recognized and muted appropriately when ignored.

## Verification

- Added unit tests in `GroupChatNamingAndMuteTests.cs`:
  - `A_foreign_session_id_that_is_in_group_membership_opens_group_tab_even_with_MessageFromAgent`
  - `A_foreign_session_id_that_is_not_a_group_opens_conference_tab_named_by_bucket`
- Updated `IsGroupSession` and `ShouldConsumeAsIgnored` test matrices to include `MessageFromAgent`.
- Verified `dotnet test`: 2972 tests pass cleanly across all projects.
- Verified `godot --headless --path app -- --selftest`: passes with exit code 0.
