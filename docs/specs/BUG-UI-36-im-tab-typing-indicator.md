# [BUG-UI-36] An IM tab does not open when the other person starts typing

- **Feature ID:** `BUG-UI-36`
- **Track:** `ui/net`
- **Status:** `✅ Done`
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](../ROADMAP.md)

## Overview & Goal

When another avatar starts typing an IM to us in-world, no conversation tab opened in `ChatWindow`.
FEAT-UI-60 promised that an IM conversation opens on `IM_TYPING_START` (dialog 41), titled with the sender's name (or avatar ID pending name resolution), before any message text is sent.

## Root Cause

1. In `src/SLNG.Net/GridSession.Chat.cs`, incoming `StartTyping` and `StopTyping` packets were gated behind:
   ```csharp
   if (typist != Guid.Empty && typist != self && SessionIds.IsPeerToPeer(e.IM.IMSessionID.Guid, self, typist))
   ```
2. Inspection of the reference viewer and Firestorm source code (`indra/newview/llimprocessing.cpp:649` and `indra/newview/llimview.cpp:4022-4039`) showed:
   - The reference viewer handles `IM_TYPING_START` via `gIMMgr->processIMTypingStart(from_id, dialog)` where `from_id` is the sender's `FromAgentID`.
   - The packet's wire `IMSessionID` is completely ignored by the reference viewer on receipt. On live Second Life and OpenSim grids, the simulator or sender viewer frequently sets `IMSessionID` to `UUID.Zero` (`Guid.Empty`), the sender's ID, or another session token rather than the bytewise XOR of the two agent IDs.
   - Because `SessionIds.IsPeerToPeer` strictly demanded `e.IM.IMSessionID.Guid == SessionIds.PeerToPeer(self, typist)`, all such incoming typing packets were silently dropped.

## Fix

1. In `src/SLNG.Net/GridSession.Chat.cs`:
   - Updated `StartTyping`/`StopTyping` packet handler to process typing events by sender ID (`typist`), matching viewer behavior.
   - Filter out typing events only if they belong to a known group or tracked conference session (`_conferenceSessions`, `GroupChatSessions`, or `GroupIM`), so conference typing does not open 1:1 tabs.
2. In `app/scripts/UI/ChatWindow.cs`:
   - Added `HasImTab(Guid agentId)` and `DrainPeerTypingForSelfTest` for automated verification.
   - Updated `DrainPeerTyping` to actively show `ChatWindow` (`Visible = true`, `EnsureOnScreen()`, `Unminimize()`), switch outer tab to the Chat page, and select the IM tab so the conversation actively opens on screen.
   - Updated `OpenOrFocusImTab` to ensure window is visible and unminimized.
   - Fixed thread safety violation in `OnNearbyAvatarsUpdated` via `CallDeferred(nameof(UpdateActiveTabHeader))`.
3. In `tests/SLNG.Net.Tests/InstantMessageTypingTests.cs`:
   - Added unit tests verifying `StartTyping` and `StopTyping` handling and LibreMetaverse packet dispatch for `ImprovedInstantMessagePacket`.
4. In `app/scripts/SelfTest.cs`:
   - Added `CheckChatWindowTypingIndicatorOpensTab` to verify that a peer typing event opens an IM tab for the sender.
5. In `app/scripts/Boot.cs`:
   - Bumped `AppVersion` to `v0.26.129-alpha`.

## Acceptance Criteria

- [x] Incoming 1:1 `StartTyping` indicator raises `InstantMessageTyping` even when wire `IMSessionID` is `UUID.Zero` or sender ID.
- [x] Peer typing event opens an IM tab in `ChatWindow` before the first message text arrives and ensures the window and tab are visible on screen.
- [x] Unit tests in `InstantMessageTypingTests` pass.
- [x] SelfTest `CheckChatWindowTypingIndicatorOpensTab` passes.
- [x] Verified in-world by the user (2026-10-08): peer typing in an IM opens the tab, brings the window to front, and displays the typing indicator.
