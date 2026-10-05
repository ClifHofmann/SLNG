using LibreMetaverse;
using SLNG.Core;

namespace SLNG.Net;

// GridSession, group info part (FEAT-UI-54).
//
// The group's public profile (GroupProfileRequest / GroupProfileReply), the agent's two server-side
// per-group switches (SetGroupAcceptNotices), and the switch that really turns a group's chat off.
//
// The chat switch is deliberately NOT a server flag: no such flag exists. Second Life and OpenSim have
// only AcceptNotices (notices) and ListInProfile; "ignore this group's chat" is a per-viewer thing --
// Firestorm keeps it in the viewer's own mute list / a local file (docs/specs/FEAT-UI-54-group-info.md).
// What the server DOES offer is leaving the chat session, which is what stops it sending the chat
// (Firestorm and the Linden viewer both send IM_SESSION_LEAVE, llimview.cpp sendLeaveSession).
public sealed partial class GridSession
{
    /// <summary>A group's profile arrived, the answer to <see cref="RequestGroupProfile"/>. Raised on
    /// a LibreMetaverse network thread -- marshal before touching the UI. May also fire for a profile
    /// requested by somebody else (the library raises it for every reply); match on
    /// <see cref="GroupProfileInfo.Id"/>.</summary>
    public event EventHandler<GroupProfileEvent>? GroupProfileReceived;

    /// <summary>Asks the simulator for one group's profile (<c>GroupProfileRequest</c>, the message the
    /// reference viewer sends in <c>LLGroupMgr::sendGroupPropertiesRequest</c>, llgroupmgr.cpp:1605).
    /// The answer arrives on <see cref="GroupProfileReceived"/>; nothing arrives if the simulator does
    /// not answer, so the caller needs its own timeout. Returns false when nothing was sent.</summary>
    public bool RequestGroupProfile(Guid groupId)
    {
        if (groupId == Guid.Empty || !_client.Network.Connected) return false;
        _client.Groups.RequestGroupProfile(new UUID(groupId));
        return true;
    }

    private void OnGroupProfile(object? sender, GroupProfileEventArgs e)
    {
        // A profile names its group too (BUG-UI-23): a free source for a chat tab still titled with the id.
        RememberGroupName(e.Group.ID.Guid, e.Group.Name ?? string.Empty);
        GroupProfileReceived?.Invoke(this, new GroupProfileEvent(GroupProfileMapper.ToProfile(e.Group)));
    }

    /// <summary>Sets the agent's two server-side per-group switches: whether this group's notices are
    /// received, and whether the group is listed in the agent's profile. One <c>SetGroupAcceptNotices</c>
    /// message carries both (<c>LLAgent::setUserGroupFlags</c>, llagent.cpp:3210-3232), so both values are
    /// always passed. Like the reference viewer this also updates the local membership snapshot at once
    /// (<see cref="GetGroups"/>, <see cref="GroupsUpdated"/>) and then asks the simulator for the real
    /// membership data, which replaces the guess if the simulator disagreed.
    /// Returns false (and sends nothing) when not connected or the agent is not in the group.</summary>
    public bool SetGroupAcceptNotices(Guid groupId, bool acceptNotices, bool listInProfile)
    {
        if (groupId == Guid.Empty || !_client.Network.Connected) return false;
        if (!ApplyLocalGroupFlags(groupId, acceptNotices, listInProfile)) return false;
        _client.Groups.SetGroupAcceptNotices(new UUID(groupId), acceptNotices, listInProfile);
        _client.Groups.RequestCurrentGroups();
        return true;
    }

    /// <summary>Replaces one group's flags in the membership snapshot and raises
    /// <see cref="GroupsUpdated"/>. False when the group is not in the snapshot.</summary>
    internal bool ApplyLocalGroupFlags(Guid groupId, bool acceptNotices, bool listInProfile)
    {
        var snapshot = _groups;
        if (snapshot == null) return false;

        var list = new List<GroupEntry>(snapshot.Count);
        bool found = false;
        foreach (var g in snapshot)
        {
            if (g.Id == groupId)
            {
                found = true;
                list.Add(g with { AcceptNotices = acceptNotices, ListInProfile = listInProfile });
            }
            else
            {
                list.Add(g);
            }
        }
        if (!found) return false;

        _groups = list;
        GroupsUpdated?.Invoke(this, new GroupsUpdatedEvent(list));
        return true;
    }

    // ---- "Receive group chat" -----------------------------------------------------------------

    /// <summary>Asked for every incoming group chat line: true = the user switched this group's chat off.
    /// Set by the app from its own store (<c>GroupMuteSettings</c>); null means nothing is ignored.
    /// Called on a LibreMetaverse network thread, so it must be thread-safe and quick. An ignored line
    /// is dropped here -- it never reaches <see cref="GroupChatMessageReceived"/>, so nothing in the app
    /// can show it, count it or log it -- and the group's chat session is left so the simulator stops
    /// sending it (see <see cref="IgnoredGroupSessionGate"/>).</summary>
    public Func<Guid, bool>? GroupChatIgnored { get; set; }

    private readonly IgnoredGroupSessionGate _ignoredGroupGate = new();

    /// <summary>True when the incoming line belongs to an ignored group and was therefore consumed.</summary>
    private bool TryConsumeIgnoredGroupChat(Guid groupId)
    {
        if (GroupChatIgnored?.Invoke(groupId) != true) return false;
        if (_ignoredGroupGate.ShouldLeave(groupId, DateTime.UtcNow.Ticks)) LeaveGroupChat(groupId);
        return true;
    }

    /// <summary>Turns one group's chat on or off at the session level. Off: leaves the chat session so the
    /// simulator stops sending it. On: joins it again (the simulator would also re-invite on the next
    /// line, but joining now makes "turn it on" take effect at once). Nothing is remembered here -- the
    /// switch itself is the app's, via <see cref="GroupChatIgnored"/>.</summary>
    public void SetGroupChatReceiving(Guid groupId, bool receive)
    {
        if (groupId == Guid.Empty) return;
        if (receive)
        {
            _ignoredGroupGate.Reset(groupId);
            JoinGroupChat(groupId);
        }
        else
        {
            // Recorded so the first ignored line after this does not send a second, identical leave.
            _ignoredGroupGate.ShouldLeave(groupId, DateTime.UtcNow.Ticks);
            LeaveGroupChat(groupId);
        }
    }
}
