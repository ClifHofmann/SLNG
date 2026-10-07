using System.Reflection;
using LibreMetaverse;
using SLNG.Core;
using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>
/// FEAT-UI-54: the group info window's data (<c>GroupProfileReply</c> + the membership flags) and the
/// "receive group chat" switch at the session level. No grid involved: the mapping is pure, and the
/// session is the unconnected one the other group tests use.
/// </summary>
public class GroupInfoTests
{
    private static void Invoke(GridSession session, string handler, object args)
    {
        var m = typeof(GridSession).GetMethod(handler, BindingFlags.NonPublic | BindingFlags.Instance)!;
        m.Invoke(session, new object?[] { null, args });
    }

    private static InstantMessageEventArgs GroupLine(UUID groupId, string message)
    {
        var im = new InstantMessage
        {
            Dialog = InstantMessageDialog.SessionSend,
            IMSessionID = groupId,
            FromAgentID = UUID.Random(),
            FromAgentName = "Some Resident",
            Message = message,
            GroupIM = true,
            BinaryBucket = Array.Empty<byte>(),
        };
        return new InstantMessageEventArgs(im, null);
    }

    // ---- mapping -----------------------------------------------------------------------------

    [Fact]
    public void ToProfile_carries_every_field_of_a_GroupProfileReply()
    {
        var id = UUID.Random();
        var founder = UUID.Random();
        var insignia = UUID.Random();
        var group = new Group
        {
            ID = id,
            Name = "Alpha Explorers",
            Charter = "We explore.\nOn Sundays.",
            FounderID = founder,
            InsigniaID = insignia,
            MembershipFee = 150,
            OpenEnrollment = true,
            ShowInList = true,
            AllowPublish = true,
            MaturePublish = true,
            GroupMembershipCount = 42,
            GroupRolesCount = 3,
            MemberTitle = "Scout",
            Powers = (GroupPowers)0x1_0000_0001UL,
        };

        var p = GroupProfileMapper.ToProfile(group);

        Assert.Equal(id.Guid, p.Id);
        Assert.Equal("Alpha Explorers", p.Name);
        Assert.Equal("We explore.\nOn Sundays.", p.Charter);
        Assert.Equal(founder.Guid, p.FounderId);
        Assert.Equal(insignia.Guid, p.InsigniaId);
        Assert.Equal(150, p.MembershipFee);
        Assert.True(p.OpenEnrollment);
        Assert.True(p.ShowInList);
        Assert.True(p.AllowPublish);
        Assert.True(p.Mature);
        Assert.Equal(42, p.MemberCount);
        Assert.Equal(3, p.RoleCount);
        Assert.Equal("Scout", p.MemberTitle);
        Assert.Equal(0x1_0000_0001UL, p.Powers);
    }

    [Fact]
    public void ToProfile_turns_missing_text_into_empty_strings_not_nulls()
    {
        var p = GroupProfileMapper.ToProfile(new Group { ID = UUID.Random() });

        Assert.Equal(string.Empty, p.Name);
        Assert.Equal(string.Empty, p.Charter);
        Assert.Equal(string.Empty, p.MemberTitle);
        Assert.Equal(Guid.Empty, p.FounderId);
        Assert.Equal(0, p.MembershipFee);
        Assert.False(p.Mature);
    }

    [Fact]
    public void ToEntry_carries_both_server_side_switches()
    {
        var id = UUID.Random();

        var entry = GroupProfileMapper.ToEntry(new Group { ID = id, Name = "G", AcceptNotices = true, ListInProfile = false });
        Assert.True(entry.AcceptNotices);
        Assert.False(entry.ListInProfile);

        var other = GroupProfileMapper.ToEntry(new Group { ID = id, Name = "G", AcceptNotices = false, ListInProfile = true });
        Assert.False(other.AcceptNotices);
        Assert.True(other.ListInProfile);
    }

    [Fact]
    public void OnGroupProfile_raises_the_neutral_event()
    {
        using var session = new GridSession();
        GroupProfileEvent? received = null;
        session.GroupProfileReceived += (s, e) => received = e;

        var id = UUID.Random();
        Invoke(session, "OnGroupProfile", new GroupProfileEventArgs(
            new Group { ID = id, Name = "Zebra", Charter = "Stripes", GroupMembershipCount = 7 }));

        Assert.NotNull(received);
        Assert.Equal(id.Guid, received!.Profile.Id);
        Assert.Equal("Zebra", received.Profile.Name);
        Assert.Equal("Stripes", received.Profile.Charter);
        Assert.Equal(7, received.Profile.MemberCount);
    }

    [Fact]
    public void RequestGroupProfile_sends_nothing_when_not_connected()
    {
        using var session = new GridSession();
        Assert.False(session.RequestGroupProfile(Guid.NewGuid()));
        Assert.False(session.RequestGroupProfile(Guid.Empty));
    }

    // ---- the two server-side switches --------------------------------------------------------

    [Fact]
    public void SetGroupAcceptNotices_returns_false_and_changes_nothing_when_not_connected()
    {
        using var session = new GridSession();
        var id = UUID.Random();
        Invoke(session, "OnCurrentGroups", new CurrentGroupsEventArgs(new Dictionary<UUID, Group>
        {
            [id] = new Group { ID = id, Name = "G", AcceptNotices = true },
        }));

        Assert.False(session.SetGroupAcceptNotices(id.Guid, false, true));
        Assert.True(session.GetGroups()[0].AcceptNotices);
    }

    [Fact]
    public void OnCurrentGroups_preserves_ListInProfile_flags()
    {
        using var session = new GridSession();
        var a = UUID.Random();
        var b = UUID.Random();
        GroupsUpdatedEvent? raised = null;
        session.GroupsUpdated += (s, e) => raised = e;

        Invoke(session, "OnCurrentGroups", new CurrentGroupsEventArgs(new Dictionary<UUID, Group>
        {
            [a] = new Group { ID = a, Name = "Alpha", AcceptNotices = true, ListInProfile = true },
            [b] = new Group { ID = b, Name = "Beta", AcceptNotices = false, ListInProfile = false },
        }));

        var groups = session.GetGroups();
        Assert.Equal(2, groups.Count);
        var alpha = groups.Single(g => g.Id == a.Guid);
        Assert.True(alpha.AcceptNotices);
        Assert.True(alpha.ListInProfile);
        var beta = groups.Single(g => g.Id == b.Guid);
        Assert.False(beta.AcceptNotices);
        Assert.False(beta.ListInProfile);

        Assert.NotNull(raised);
        Assert.Equal(2, raised!.Groups.Count);
        Assert.True(raised.Groups.Single(g => g.Id == a.Guid).ListInProfile);
        Assert.False(raised.Groups.Single(g => g.Id == b.Guid).ListInProfile);
    }

    [Fact]
    public void ApplyLocalGroupFlags_updates_the_snapshot_and_raises_GroupsUpdated()
    {
        using var session = new GridSession();
        var a = UUID.Random();
        var b = UUID.Random();
        Invoke(session, "OnCurrentGroups", new CurrentGroupsEventArgs(new Dictionary<UUID, Group>
        {
            [a] = new Group { ID = a, Name = "Alpha", AcceptNotices = true, ListInProfile = true },
            [b] = new Group { ID = b, Name = "Beta", AcceptNotices = true, ListInProfile = true },
        }));
        GroupsUpdatedEvent? raised = null;
        session.GroupsUpdated += (s, e) => raised = e;

        Assert.True(session.ApplyLocalGroupFlags(a.Guid, acceptNotices: false, listInProfile: false));

        var alpha = session.GetGroups().Single(g => g.Id == a.Guid);
        Assert.False(alpha.AcceptNotices);
        Assert.False(alpha.ListInProfile);
        var beta = session.GetGroups().Single(g => g.Id == b.Guid);
        Assert.True(beta.AcceptNotices); // the other group is untouched
        Assert.True(beta.ListInProfile);
        Assert.NotNull(raised);
        Assert.Equal(2, raised!.Groups.Count);
    }

    [Fact]
    public void ApplyLocalGroupFlags_rejects_a_group_the_agent_is_not_in()
    {
        using var session = new GridSession();
        Assert.False(session.ApplyLocalGroupFlags(Guid.NewGuid(), true, true)); // no snapshot yet

        var a = UUID.Random();
        Invoke(session, "OnCurrentGroups", new CurrentGroupsEventArgs(new Dictionary<UUID, Group>
        {
            [a] = new Group { ID = a, Name = "Alpha" },
        }));
        Assert.False(session.ApplyLocalGroupFlags(Guid.NewGuid(), true, true));
    }

    // ---- "receive group chat" ----------------------------------------------------------------

    [Fact]
    public void An_ignored_group_line_never_reaches_the_app()
    {
        using var session = new GridSession();
        var groupId = UUID.Random();
        session.GroupChatIgnored = id => id == groupId.Guid;
        var lines = new List<GroupChatMessageEvent>();
        InstantMessageEvent? im = null;
        session.GroupChatMessageReceived += (s, e) => lines.Add(e);
        session.InstantMessageReceived += (s, e) => im = e;

        Invoke(session, "OnInstantMessage", GroupLine(groupId, "buy my stuff"));

        Assert.Empty(lines);
        Assert.Null(im); // and it must not fall through into the 1:1 IM path either
    }

    [Fact]
    public void Another_group_is_not_affected_by_the_ignore()
    {
        using var session = new GridSession();
        var ignored = UUID.Random();
        var other = UUID.Random();
        session.GroupChatIgnored = id => id == ignored.Guid;
        var lines = new List<GroupChatMessageEvent>();
        session.GroupChatMessageReceived += (s, e) => lines.Add(e);

        Invoke(session, "OnInstantMessage", GroupLine(other, "hello"));

        Assert.Single(lines);
        Assert.Equal(other.Guid, lines[0].GroupId);
    }

    [Fact]
    public void Switching_the_ignore_off_lets_lines_through_again()
    {
        using var session = new GridSession();
        var groupId = UUID.Random();
        bool ignore = true;
        session.GroupChatIgnored = id => ignore && id == groupId.Guid;
        var lines = new List<GroupChatMessageEvent>();
        session.GroupChatMessageReceived += (s, e) => lines.Add(e);

        Invoke(session, "OnInstantMessage", GroupLine(groupId, "one"));
        ignore = false;
        Invoke(session, "OnInstantMessage", GroupLine(groupId, "two"));

        Assert.Single(lines);
        Assert.Equal("two", lines[0].Message);
    }

    [Fact]
    public void No_ignore_predicate_means_everything_arrives()
    {
        using var session = new GridSession();
        var lines = new List<GroupChatMessageEvent>();
        session.GroupChatMessageReceived += (s, e) => lines.Add(e);

        Invoke(session, "OnInstantMessage", GroupLine(UUID.Random(), "hello"));

        Assert.Single(lines);
    }

    // ---- when to leave the chat session of an ignored group ---------------------------------

    [Fact]
    public void The_gate_leaves_at_once_and_then_only_after_the_interval()
    {
        var gate = new IgnoredGroupSessionGate();
        var group = Guid.NewGuid();
        long t0 = 1_000_000_000_000;

        Assert.True(gate.ShouldLeave(group, t0));
        Assert.False(gate.ShouldLeave(group, t0 + 1));
        Assert.False(gate.ShouldLeave(group, t0 + IgnoredGroupSessionGate.MinInterval.Ticks - 1));
        // LibreMetaverse re-joins on every invitation, so a line after the interval means we are back in.
        Assert.True(gate.ShouldLeave(group, t0 + IgnoredGroupSessionGate.MinInterval.Ticks));
    }

    [Fact]
    public void The_gate_keeps_groups_apart_and_Reset_forgets_one()
    {
        var gate = new IgnoredGroupSessionGate();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        long t = 5_000_000_000;

        Assert.True(gate.ShouldLeave(a, t));
        Assert.True(gate.ShouldLeave(b, t)); // a different group has its own clock
        Assert.False(gate.ShouldLeave(a, t + 10));

        gate.Reset(a); // chat switched back on, then off again
        Assert.True(gate.ShouldLeave(a, t + 20));
        Assert.False(gate.ShouldLeave(b, t + 20));
    }
}
