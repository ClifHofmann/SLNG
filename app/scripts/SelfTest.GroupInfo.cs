using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using SLNG.Core;

namespace SLNG.App;

/// <summary>
/// FEAT-UI-54 checks: the group info window and the one "Receive group chat" setting behind it.
///
/// Everything runs in memory: <see cref="UI.GroupMuteSettings.Persist"/> is switched off for the duration
/// (the boot path of a selftest runs on the maintainer's real <c>user://</c>, and a mute written to
/// preferences.cfg would outlive the run), the chat logger points at a throwaway temp folder, and the
/// window is fed through its delegates instead of a <c>GridSession</c>.
///
/// What this cannot show: that a real simulator answers <c>GroupProfileRequest</c>, that
/// <c>SetGroupAcceptNotices</c> is accepted, or that leaving the chat session really stops the sim
/// sending it. Those need a login.
/// </summary>
public static partial class SelfTest
{
    private static GroupProfileInfo FakeGroupProfile(Guid groupId, Guid founderId) => new(
        groupId, "Test Explorers", "We explore.\nOn Sundays.\nBring snacks.", founderId, Guid.NewGuid(),
        MembershipFee: 150, OpenEnrollment: true, ShowInList: true, AllowPublish: true, Mature: true,
        MemberCount: 42, RoleCount: 3, MemberTitle: "Scout", Powers: 0);

    private static Check CheckGroupInfoWindow(SceneTree tree)
    {
        const string Name = "group info window";
        var groupId = Guid.NewGuid();
        var founderId = Guid.NewGuid();
        bool restorePersist = UI.GroupMuteSettings.Persist;
        UI.GroupMuteSettings.Persist = false;
        var created = new List<Node>();
        var problems = new List<string>();
        void Expect(string what, bool ok) { if (!ok) problems.Add(what); }

        try
        {
            var sent = new List<(bool Notices, bool List)>();
            var namesAsked = new List<Guid>();
            bool sendOk = true;
            bool requestOk = true;
            int requests = 0;

            UI.GroupInfoWindow Build()
            {
                var w = new UI.GroupInfoWindow
                {
                    RequestProfile = () => { requests++; return requestOk; },
                    SetNoticeFlags = (n, l) => { sent.Add((n, l)); return sendOk; },
                    TryGetName = _ => null,
                    RequestName = id => namesAsked.Add(id),
                };
                created.Add(w);
                tree.Root.AddChild(w);
                w.Initialize(groupId, "Test Explorers");
                return w;
            }

            var win = Build();
            Expect("starts in the loading state", win.State == UI.GroupInfoWindow.ProfileState.Loading && win.StateText == UI.L10n.Tr("ui.group_info.loading"));
            Expect("requested the profile once on open", requests == 1);
            Expect("server-side boxes are off until the membership is known", !win.ReceiveNoticesEnabled);

            // Membership first (it is already known when the window opens), then the profile.
            win.ApplyMembership(new GroupEntry(groupId, "Test Explorers", "Officer", Guid.Empty, AcceptNotices: true, ListInProfile: false));
            win.ApplyProfile(FakeGroupProfile(Guid.NewGuid(), founderId)); // another group's reply: ignored
            Expect("a reply for another group is ignored", win.State == UI.GroupInfoWindow.ProfileState.Loading);

            var profile = FakeGroupProfile(groupId, founderId);
            win.ApplyProfile(profile);
            Expect("loaded state hides the status line", win.State == UI.GroupInfoWindow.ProfileState.Loaded && win.StateText == "");
            Expect("member count", win.MembersText == "42");
            Expect("membership fee", win.FeeText == UI.L10n.TrFormat("ui.group_info.fee_value", 150));
            Expect("open enrolment", win.EnrolmentText == UI.L10n.Tr("ui.group_info.enrolment_open"));
            Expect("moderate maturity", win.MaturityText == UI.L10n.Tr("ui.group_info.maturity_moderate"));
            Expect("title comes from the membership row", win.TitleText == "Officer");
            Expect("not the active group", win.ActiveText == UI.L10n.Tr("ui.group_info.no"));
            Expect("insignia id shown", win.InsigniaText == profile.InsigniaId.ToString());
            Expect("charter shown, read-only", win.CharterText == profile.Charter && !win.CharterEditable);
            Expect("founder name asked for and pending", namesAsked.Contains(founderId) && win.FounderText == UI.L10n.Tr("ui.group_info.loading_name"));
            win.OnNameResolved(Guid.NewGuid(), "Somebody Else");
            Expect("another agent's name does not touch the founder", win.FounderText == UI.L10n.Tr("ui.group_info.loading_name"));
            win.OnNameResolved(founderId, "Group Founder");
            Expect("founder name fills in", win.FounderText == "Group Founder");
            win.ApplyActiveGroup(groupId);
            Expect("active group", win.ActiveText == UI.L10n.Tr("ui.group_info.yes"));

            // The three checkboxes show their state...
            Expect("receive notices shows the membership", win.ReceiveNoticesEnabled && win.ReceiveNoticesChecked);
            Expect("list in profile shows the membership", !win.ListInProfileChecked);
            Expect("receive chat is on for a group that is not muted", win.ReceiveChatChecked);

            // ...and the two server-side ones send both values every time.
            win.PressReceiveNotices(false);
            Expect("unchecking notices sends (false, false)", sent.Count == 1 && sent[0] == (false, false));
            win.PressListInProfile(true);
            Expect("checking list sends (false, true)", sent.Count == 2 && sent[1] == (false, true));

            // A send that did not go out puts the boxes back.
            sendOk = false;
            win.PressReceiveNotices(true);
            Expect("a failed send restores the box", !win.ReceiveNoticesChecked && win.SettingsStatusText != "");
            sendOk = true;

            // The membership data corrects the boxes (what the sim says wins).
            win.ApplyMembership(new GroupEntry(groupId, "Test Explorers", "Officer", Guid.Empty, AcceptNotices: true, ListInProfile: true));
            Expect("membership update refreshes the boxes", win.ReceiveNoticesChecked && win.ListInProfileChecked && win.SettingsStatusText == "");
            win.ApplyMembership(null);
            Expect("not a member disables the server-side boxes", !win.ReceiveNoticesEnabled);

            // Failed is not empty.
            requestOk = false;
            var failing = Build();
            Expect("an unsendable request fails at once, with a retry", failing.State == UI.GroupInfoWindow.ProfileState.Failed && failing.RetryVisible && failing.StateText == UI.L10n.Tr("ui.group_info.failed"));
            requestOk = true;
            failing.PressRetry();
            Expect("retry goes back to loading", failing.State == UI.GroupInfoWindow.ProfileState.Loading && !failing.RetryVisible);
            failing._Process(UI.GroupInfoWindow.ProfileTimeoutSeconds - 1);
            Expect("still loading before the timeout", failing.State == UI.GroupInfoWindow.ProfileState.Loading);
            failing._Process(2);
            Expect("no answer by the timeout is a failure", failing.State == UI.GroupInfoWindow.ProfileState.Failed && failing.RetryVisible);
            failing.ApplyProfile(profile);
            Expect("a late answer recovers", failing.State == UI.GroupInfoWindow.ProfileState.Loaded && !failing.RetryVisible);

            // ONE setting: the window's checkbox and the Groups tab's Mute button.
            var panel = new UI.GroupsPanel();
            created.Add(panel);
            tree.Root.AddChild(panel);
            string muteText = panel.MuteButtonText; // "Mute chat" with nothing selected
            panel.SelectForSelfTest(groupId, "Test Explorers");
            Expect("profile button enabled once a group is selected", panel.ProfileButtonEnabled);
            Guid? opened = null;
            panel.OnOpenGroupInfoRequested = (id, _) => opened = id;
            panel.PressProfileForSelfTest();
            Expect("profile button opens the group info", opened == groupId);

            win.PressReceiveChat(false);
            Expect("unchecking Receive group chat mutes the group", UI.GroupMuteSettings.IsMuted(groupId));
            Expect("...and the Mute button follows", panel.MuteButtonText == UI.L10n.Tr("ui.groups.action_unmute"));
            panel.PressMuteForSelfTest();
            Expect("the Mute button un-mutes", !UI.GroupMuteSettings.IsMuted(groupId) && panel.MuteButtonText == muteText);
            Expect("...and the checkbox follows", win.ReceiveChatChecked && failing.ReceiveChatChecked);
            panel.PressMuteForSelfTest();
            Expect("the Mute button mutes", UI.GroupMuteSettings.IsMuted(groupId) && !win.ReceiveChatChecked && !failing.ReceiveChatChecked);
            win.PressReceiveChat(true);
            Expect("checking it again re-enables", !UI.GroupMuteSettings.IsMuted(groupId));

            // The chat itself: a group with its chat off gets no tab, no unread, no log.
            string logDir = Path.Combine(Path.GetTempPath(), "slng-selftest-groupchat-" + Guid.NewGuid().ToString("N"));
            try
            {
                var chat = new UI.ChatWindow();
                created.Add(chat);
                tree.Root.AddChild(chat);
                chat.Initialize(new SLNG.Core.Services.ChatLogger(logDir));
                var speaker = Guid.NewGuid();

                chat.AppendGroupChatMessage(groupId, "Test Explorers", speaker, "Bob", "hello");
                Expect("chat on: a group line opens the tab", chat.HasGroupTab(groupId));

                UI.GroupMuteSettings.SetMuted(groupId, true);
                Expect("chat off: the open tab is closed", !chat.HasGroupTab(groupId));
                int unreadBefore = chat.TotalUnread;
                chat.AppendGroupChatMessage(groupId, "Test Explorers", speaker, "Bob", "are you there?");
                Expect("chat off: a line opens no tab and counts no unread", !chat.HasGroupTab(groupId) && chat.TotalUnread == unreadBefore);

                var otherGroup = Guid.NewGuid();
                chat.AppendGroupChatMessage(otherGroup, "Other", speaker, "Bob", "hi");
                Expect("another group is not affected", chat.HasGroupTab(otherGroup));

                UI.GroupMuteSettings.SetMuted(groupId, false);
                chat.AppendGroupChatMessage(groupId, "Test Explorers", speaker, "Bob", "back");
                Expect("chat on again: lines arrive again", chat.HasGroupTab(groupId));
            }
            finally
            {
                // The logger appends on a worker; let a late write fail quietly rather than fail the run.
                try { if (Directory.Exists(logDir)) Directory.Delete(logDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }

            return problems.Count == 0
                ? new Check(Name, true, "profile fields, the three checkboxes, loading/failed/timeout/retry, Mute button and checkbox as one setting, and no tab/unread for a group with chat off")
                : new Check(Name, false, string.Join("; ", problems));
        }
        catch (Exception ex)
        {
            return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            UI.GroupMuteSettings.SetMuted(groupId, false);
            UI.GroupMuteSettings.Persist = restorePersist;
            foreach (var n in created)
                if (GodotObject.IsInstanceValid(n)) n.QueueFree();
        }
    }
}
