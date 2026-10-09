# [BUG-UI-40] Sim and object IMs route to Main chat tab instead of opening IM/conference tab

- **Feature ID:** `BUG-UI-40`
- **Track:** `net/ui`
- **Status:** `✅ Done`
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

When a scripted in-world object (such as a visitor scanner or vendor) or simulator/system notification sends an instant message (e.g. `llInstantMessage`, `IM_FROM_TASK`, `IM_CONSOLE_AND_CHAT_HISTORY`), the Second Life / Firestorm reference viewer routes it directly to Nearby Chat (`nearby_chat`, the "Main" tab in SLNG) rather than opening an IM session or conference (`llimprocessing.cpp:1007-1065`: *"Note: lie to Nearby Chat, pretending that this is NOT an IM, because IMs from objects don't open IM sessions"*).

Following BUG-UI-37's foreign session ID routing, scripted object messages (such as `Azure Haven` reporting visitor detections at coordinates `Millenium/124/129/24`) had non-P2P transaction session IDs and binary buckets containing the object coordinates. Because they were not groups, they fell into the conference path, opening an ad-hoc conference tab titled after the object coordinates (`Millenium/124/129/24`) instead of appearing in the Main chat tab.

## Acceptance Criteria

- [x] Object IMs (`InstantMessageDialog.MessageFromObject` / 19, `FromTaskAsAlert` / 31) route to `ChatMessageReceived` (Main tab) with `FromAgent: false`.
- [x] Sim notifications (`ConsoleAndChatHistory` / 21, `MessageBox` / 1, or sender ID `Guid.Empty`) route to `ChatMessageReceived` (Main tab) with `FromAgent: false`.
- [x] No IM tab or conference tab is opened for object/sim messages.
- [x] Conference routing is guarded by `GroupChatSessionLogic.IsSessionLineDialog` so non-session dialogs never open conference tabs.
- [x] Unit tests in `GroupChatNamingAndMuteTests` verify object and system message routing.

## Technical Specs & Affected Files

- `src/SLNG.Net/GridSession.Chat.cs`: Route object and system dialogs to `ChatMessageReceived` before session routing; guard conference creation with `IsSessionLineDialog`.
- `tests/SLNG.Net.Tests/GroupChatNamingAndMuteTests.cs`: Add unit tests for `MessageFromObject`, `FromTaskAsAlert`, `ConsoleAndChatHistory`, `MessageBox`, and `FromAgentID == UUID.Zero`.
- `app/scripts/Boot.cs`: Bump `AppVersion` to `v0.27.22-alpha`.
