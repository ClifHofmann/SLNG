# [BUG-UI-38] Peer typing indicator steals tab focus and interrupts group/IM views

- **Feature ID:** `BUG-UI-38`
- **Track:** `ui`
- **Status:** `✅ Done`
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](../ROADMAP.md)

## Overview & Goal

When another avatar starts typing an IM to us while `ChatWindow` is open, the user was forcibly pulled into the typing avatar's conversation tab. This interrupted active typing in another IM tab and pulled the user out of the Groups (`_groupsPanel`) or Friends list view.

## Root Cause

In `app/scripts/UI/ChatWindow.cs`, `DrainPeerTyping` unconditionally called:
```csharp
if (!Visible)
{
    Visible = true;
    EnsureOnScreen();
}
if (IsMinimized) Unminimize();
BringToFront();
SelectOuterTab(_chatPageControl);
SelectChatTab(tab);
```
Even if `ChatWindow` was already open on the Groups page or another IM conversation was actively selected, every incoming `InstantMessageTypingEvent` (`Typing = true`) forced `SelectOuterTab(_chatPageControl)` and `SelectChatTab(tab)`, unminimized the window and brought it to front.

## Fix

1. In `app/scripts/UI/ChatWindow.cs`:
   - Updated `DrainPeerTyping` so `SelectOuterTab(_chatPageControl)`, `SelectChatTab(tab)`, `BringToFront()`, and `EnsureOnScreen()` only execute when `ChatWindow` was completely closed (`!Visible`).
   - When `ChatWindow` is already visible, the tab is created and its typing indicator mark (`…`) is set on the tab header, but existing outer tab page selections (e.g. Groups, Friends) and active chat tabs (e.g. other IMs, Main chat) are preserved without focus stealing.
   - Added `ActiveChatTargetAgentId` property for automated selftest verification.
2. In `app/scripts/SelfTest.cs`:
   - Extended `CheckChatWindowTypingIndicatorOpensTab` to assert that incoming peer typing does not switch away from an open Groups page or active chat tab, while still popping up and focusing the conversation when the window was closed.
3. In `app/scripts/Boot.cs`:
   - Bumped `AppVersion` to `v0.27.19-alpha`.

## Acceptance Criteria

- [x] Incoming peer typing indicator does not switch outer page away from Groups or Friends.
- [x] Incoming peer typing indicator does not switch active chat tab away from an ongoing IM or Main chat conversation.
- [x] When `ChatWindow` is closed (`Visible == false`), incoming peer typing opens the window and selects the conversation.
- [x] SelfTest `CheckChatWindowTypingIndicatorOpensTab` passes with both assertions.
