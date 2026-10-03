# [FEAT-UI-54] Group info window with "My settings" (receive group chat, notices, list in profile)

- **Feature ID:** `FEAT-UI-54`
- **Track:** `ui` / `net`
- **Status:** `🧪 Review`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
Asked for 2026-10-03, urgent: a group's chat must be ignorable, "as in Firestorm, in the group info". Until
now the Groups tab had a local *Mute* button (only stopped a closed tab from opening) and placeholder
*Profile* / *Leave* buttons; there was no group info window. Goal: a **Group Info** window (read-only profile
plus the three **My settings** checkboxes) and ONE per-group "Receive group chat" setting that really turns
the chat off in this viewer, shared with the Mute button.

## Facts (established from source, not guessed)

Sources: Firestorm `FirestormViewer/phoenix-firestorm` at `master` b3c157df (read 2026-10-03, `indra/newview/...`);
the vendored Linden viewer `scratch/slviewer` (shallow clone, `indra/newview/...`); LibreMetaverse **3.1.6**
decompiled from the NuGet assembly (`scratch/libremetaverse_src` is v3.0.2 and stale, not used).

### (a) Firestorm's "Receive group chat" is per VIEWER, not server-side
It is not a grid flag. It is an `<exodus>` addition (the checkbox is `name="receive_chat"`, label "Receive group
instant messages", `skins/default/xui/en/panel_group_general.xml:243-253`, next to the two real server-side boxes
`receive_notices` / `list_groups_in_profile`). The panel reads it as `!exoGroupMuteList::instance().isMuted(id)`
(`llpanelgroupgeneral.cpp:211-221`) and applies it with `exoGroupMuteList::add/remove` next to the server-side
`gAgent.setUserGroupFlags(...)` (`llpanelgroupgeneral.cpp:462-481`). Where `exoGroupMuteList` keeps it
(`exogroupmutelist.cpp`):

- **Second Life:** in the viewer's own mute list as a *by-name* entry `"Group:" + <uuid>`
  (`exogroupmutelist.cpp:52`, `:80`, `:95`, `:199-202`). A mute-list entry is stored on the grid
  (`LLMuteList::add` -> `updateAdd` -> `UpdateMuteListEntry`, Linden `llmutelist.cpp:370-391`, `:464-480`), so the
  setting is shared with other **Firestorm** sessions on the account; the Linden viewer does not interpret it
  (it would list an odd by-name block entry).
- **OpenSim:** a local per-account file `muted_groups.xml` (`exogroupmutelist.cpp:36-41`, `:46-50`, `:142-145`).

What "on/off" does in Firestorm: turning it off calls `LLGroupActions::endIM(group)` (kills the open conversation,
`exogroupmutelist.cpp:68`); an incoming group session for a muted group is **left** with `sendLeaveSession`
(`llimview.cpp:3590-3603`; also `exogroupmutelist.cpp:190`); a message for a not-open session of a muted group is
dropped (`llimprocessing.cpp:1858`); and explicitly opening a group's chat un-mutes it
(`llgroupactions.cpp:655-658`).

### (b) The two server-side checkboxes (Linden viewer)
- `LLAgent::setUserGroupFlags` (`llagent.cpp:3210-3232`): updates the local `mGroups[i]` copy at once, then sends
  UDP `SetGroupAcceptNotices` { AgentData, Data{GroupID, AcceptNotices}, NewData{ListInProfile} } - **one message
  carries both flags**, so both are always sent.
- The panel reads both flags from `gAgent.getGroupData` (the membership list, `AgentGroupDataUpdate`), **not**
  from the profile reply (`llpanelgroupgeneral.cpp:188-196`).
- `GroupProfileRequest` {AgentData, GroupData{GroupID}} (`llgroupmgr.cpp:1591-1612`) -> `GroupProfileReply`
  (`llgroupmgr.cpp:1041-1135`): GroupID, Name, Charter, ShowInList, MemberTitle, InsigniaID, FounderID,
  PowersMask, MembershipFee, OpenEnrollment, GroupMembershipCount, GroupRolesCount, Money, AllowPublish,
  MaturePublish, OwnerRole. There is no "accept notices" in it.

### (c) How SLNG joined group chat BEFORE this change
Lazily, **never at login** - login only asks for the memberships (`GroupsPanel.Initialize` -> `RequestGroups`).
Three ways a session comes to exist:
1. **Second Life:** the sim invites the agent (`ChatterBoxInvitation`) when somebody speaks; LibreMetaverse
   **accepts every invitation itself** (`AgentManager.ChatterBoxInvitationEventHandler` ->
   `ChatterBoxAcceptInviteAsync`) *before* any SLNG code sees the line.
2. **OpenSim:** the line arrives as `SessionSend`; `IsGroupMessage` registers the session in `GroupChatSessions`.
3. **Explicit:** `ChatWindow.OpenOrFocusGroupTab` -> `GridSession.JoinGroupChat` (`RequestJoinGroupChat`, IM dialog 15).
   Closing a group tab -> `LeaveGroupChat` (dialog 18, `ChatWindow.CloseChatTab`).
Lines then flow `GridSession.OnInstantMessage` -> `GroupChatMessageReceived` -> `Boot` -> `ChatWindow.AppendGroupChatMessage`.
The old mute only dropped a line `if (!tabOpen && IsMuted)` there - the session stayed joined and an open tab still received.

### (d) LibreMetaverse 3.1.6
- `GroupManager.RequestGroupProfile(UUID)` -> event `GroupProfile` (`GroupProfileEventArgs.Group`). The reply
  handler fills only the **profile half** of the `Group` struct (name, charter, founder, insignia, fee, enrolment,
  maturity, member/role count, powers, **MemberTitle**).
- Membership is **not** a separate `GroupMembershipData` type in 3.1.6: it is the same `Group` struct from
  `GroupManager.CurrentGroups` (`AgentGroupDataUpdate` handler: ID, InsigniaID, Name, Contribution, AcceptNotices,
  Powers, ListInProfile - and **no MemberTitle**, so `GroupEntry.MemberTitle` is empty on that path).
  `RequestCurrentGroups()` (UDP `AgentDataUpdateRequest`) re-asks.
- `GroupManager.SetGroupAcceptNotices(UUID, bool acceptNotices, bool listInProfile)` = the UDP message above.
- `AgentManager.RequestJoinGroupChat(UUID)` (IM dialog 15), `RequestLeaveGroupChat(UUID)` (dialog 18, also removes
  it from `GroupChatSessions`), event `GroupChatJoined`.
- The Group info "ActiveGroup" is `Self.ActiveGroup` (already `GridSession.ActiveGroupId` / `ActiveGroupChanged`).

## Decisions

1. **"Receive group chat" = SLNG's local switch, `GroupMuteSettings` is the single store.** Checked = receive
   (muted is the inverse). The Mute button, the group info checkbox and the Groups list marker are the same
   setting and follow each other through `GroupMuteSettings.MuteChanged`. Persisted in `preferences.cfg`
   section `group_mute` (unchanged, so existing mutes carry over).
2. **Not written to the server.** No server flag for chat exists; `AcceptNotices` is about notices only. Firestorm
   itself keeps it per viewer. SLNG does **not** mirror Firestorm's `Group:<uuid>` mute-list entry: it would put an
   odd entry into the user's real grid mute list that other viewers show, for interop nobody asked for. If wanted
   later, `GridSession.Profiles.cs` already has the mute-list plumbing (`UpdateMuteListEntry`; LMV supports
   by-name mutes). Consequence to know: a mute set in Firestorm does **not** show in SLNG, and vice versa.
3. **What "off" does** (verified by unit tests and the selftest, not on a grid):
   - the network thread drops every line of that group before anything sees it (`GridSession.GroupChatIgnored`,
     fed by `GroupMuteSettings.IsMuted`): no tab, no unread, no notification entry, nothing shown;
   - **nothing is logged** for it (a log line is only written from `AppendMessageToTab`, which never runs). Decision:
     an ignored group must not fill the log. The log already on disk stays;
   - turning it off **closes the open tab** and **leaves the chat session** (`SetGroupChatReceiving(false)`), so the
     sim stops sending (Firestorm's behaviour, `llimview.cpp:3590-3603`);
   - because LibreMetaverse re-accepts every sim invitation (see (c)), a later line re-joins the session; the
     leave is therefore repeated at most once per 20 s per group while lines keep arriving
     (`IgnoredGroupSessionGate`); the lines themselves are always dropped;
   - turning it **on** joins the session again (`SetGroupChatReceiving(true)` -> `JoinGroupChat`); nothing is
     replayed (the sim does not store group chat for an agent that was not in the session);
   - explicitly opening the group's chat (double-click / *Group Chat* button) turns it back on first, as Firestorm
     does (`llgroupactions.cpp:655-658`).
   Old behaviour "an already-open tab still receives" is gone: turning the switch off closes the tab.
4. **No eager join exists**, so nothing changes at login: SLNG never joined every group's session at login.
5. **Group info window `GroupInfoWindow : SLNGWindow`**, opened from the Groups tab's **Profile** button (one window
   per group; double-click stays "open chat"). Read-only: name, charter (read-only multi-line), founder (name via the
   shared name lookup, "Loading…" until it resolves), member count, membership fee, open/by-invitation enrolment,
   maturity (Moderate/General = `MaturePublish`), your title, active-group yes/no, insignia **as a selectable UUID** -
   the picture needs the asset/GPU-cache pinning that only `UserProfileWindow` has privately; no asset plumbing was built.
   "Your title": the membership row's title when it has one (it never does on the AgentGroupDataUpdate path, see (d)),
   else the profile reply's `MemberTitle` - that is what the sim sends, probably the Everyone-role title; the agent's
   real active title would need `GroupTitlesRequest`, not requested.
6. **My settings:** *Receive group notices* and *List group in my profile* are applied at once (no Apply button) via
   `GridSession.SetGroupAcceptNotices` (both values every time), shown from the membership data, disabled when the
   group is not in the membership list; a refused send puts the boxes back and says so. *Receive group chat* is the
   local switch.
7. **Request / failure:** the profile is requested once on open; the reply arrives on the network thread and is
   marshalled through `Boot`'s main-thread queue. Loading, loaded and **failed** are distinct states: no reply in
   10 s (or nothing could be sent) shows a message and a Retry button instead of empty fields; a late reply recovers.
8. **Net layer, neutral:** `SLNG.Core.GroupProfileInfo` (+ `GroupProfileEvent`, `GroupEntry.ListInProfile`);
   `GridSession.GroupInfo.cs`; internal pure `GroupProfileMapper` and `IgnoredGroupSessionGate`. No LibreMetaverse
   type crosses the boundary.

## Acceptance Criteria
- [x] (1) Profile button opens Group Info: name, insignia id, charter, founder, member count, fee, open enrolment,
      maturity, your title, active group - read-only.
- [x] (2) My settings has the three checkboxes; Receive group chat really turns the chat off (no tab, unread,
      notification, log; session left; join again on re-enable). *Unverified on a grid*, see below.
- [x] (3) Mute button and checkbox are one setting; the Groups list keeps its muted marker.
- [x] (4) Firestorm's storage established from its source and written down (above).
- [x] (5) *Leave* stays a placeholder (FEAT-UI-38).
- [x] Unit tests (`tests/SLNG.Net.Tests/GroupInfoTests.cs`) and selftest (`SelfTest.GroupInfo.cs`, plus the window in
      the insets / 900 px height check).

## Technical Specs & Affected Files
- `src/SLNG.Core/GroupProfileInfo.cs`, `GridEvents.cs` (`GroupProfileEvent`), `GroupEntry.cs` (`ListInProfile`)
- `src/SLNG.Net/GridSession.GroupInfo.cs`, `GroupProfileMapper.cs`, `IgnoredGroupSessionGate.cs`,
  `GridSession.cs` (subscribe `GroupProfile`), `GridSession.Chat.cs` (ignore + mapper)
- `app/scripts/UI/GroupInfoWindow.cs`, `GroupMuteSettings.cs` (thread-safe, `Persist`, `Prime`), `GroupsPanel.cs`,
  `ChatWindow.cs`, `Boot.cs`, `SelfTest.cs`, `SelfTest.GroupInfo.cs`; `app/i18n/*.json` (`ui.group_info.*`)

## Not verified (needs a login)
That a real sim answers `GroupProfileRequest` as parsed, that `SetGroupAcceptNotices` is accepted and echoed by the
next `AgentGroupDataUpdate`, that leaving the session really silences the group on Second Life and OpenSim (and that
the sim does not re-invite faster than the 20 s gate), and that turning the chat back on re-joins cleanly.
