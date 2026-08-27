using System.Reflection;
using LibreMetaverse;
using SLNG.Core;
using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>
/// M5-3 Phase 2: group membership + group chat routing.
///
/// The routing is the part worth pinning. Group chat does NOT arrive as
/// <c>InstantMessageDialog.MessageFromAgent</c> — it comes in as <c>SessionSend</c>, and its
/// <c>GroupIM</c> flag is only set on the first message of a session. The previous filter
/// (<c>Dialog != MessageFromAgent || GroupIM</c>) therefore dropped it twice over, which is why
/// group chat never appeared at all. These tests hold each half of that in place.
/// </summary>
public class GroupChatTests
{
    private static void Invoke(GridSession session, string handler, object args)
    {
        var m = typeof(GridSession).GetMethod(handler, BindingFlags.NonPublic | BindingFlags.Instance)!;
        m.Invoke(session, new object?[] { null, args });
    }

    // InstantMessageEventArgs.IM is a get-only property over a struct, so every field has to be
    // set before the args are constructed -- including the binary bucket.
    private static InstantMessageEventArgs Im(
        InstantMessageDialog dialog, UUID sessionId, UUID fromAgent, string fromName, string message,
        bool groupIM, byte[]? binaryBucket = null)
    {
        var im = new InstantMessage
        {
            Dialog = dialog,
            IMSessionID = sessionId,
            FromAgentID = fromAgent,
            FromAgentName = fromName,
            Message = message,
            GroupIM = groupIM,
            BinaryBucket = binaryBucket ?? Array.Empty<byte>(),
        };
        return new InstantMessageEventArgs(im, null);
    }

    [Fact]
    public void OnCurrentGroups_maps_to_neutral_entries_sorted_by_name()
    {
        using var session = new GridSession();
        GroupsUpdatedEvent? received = null;
        session.GroupsUpdated += (s, e) => received = e;

        var zebraId = UUID.Random();
        var alphaId = UUID.Random();
        var insignia = UUID.Random();
        var groups = new Dictionary<UUID, Group>
        {
            [zebraId] = new Group { ID = zebraId, Name = "Zebra Builders", MemberTitle = "Member", AcceptNotices = false },
            [alphaId] = new Group { ID = alphaId, Name = "Alpha Explorers", MemberTitle = "Founder", InsigniaID = insignia, AcceptNotices = true },
        };

        Invoke(session, "OnCurrentGroups", new CurrentGroupsEventArgs(groups));

        Assert.NotNull(received);
        Assert.Equal(2, received!.Groups.Count);
        // Sorted here so no consumer has to have an opinion about it.
        Assert.Equal("Alpha Explorers", received.Groups[0].Name);
        Assert.Equal("Zebra Builders", received.Groups[1].Name);

        var alpha = received.Groups[0];
        Assert.Equal(alphaId.Guid, alpha.Id);
        Assert.Equal("Founder", alpha.MemberTitle);
        Assert.Equal(insignia.Guid, alpha.InsigniaId);
        Assert.True(alpha.AcceptNotices);

        // The same snapshot must be readable without another round trip.
        Assert.Equal(2, session.GetGroups().Count);
        // ...and the group names land in the shared name cache, which is what lets an incoming
        // group message name its group (the IM itself names only the speaker).
        Assert.True(session.TryGetCachedName(alphaId.Guid, out var cached));
        Assert.Equal("Alpha Explorers", cached);
    }

    [Fact]
    public void GetGroups_is_empty_before_any_reply()
    {
        using var session = new GridSession();
        Assert.Empty(session.GetGroups());
    }

    // SessionSend + GroupIM=false is the shape a message from an ALREADY-OPEN session takes, and
    // is exactly what the old filter dropped. LibreMetaverse's IsGroupMessage recognises it via
    // its GroupChatSessions table; with no such session registered (no live connection here) it
    // falls back to the GroupIM flag, which is the case this asserts.
    [Fact]
    public void OnInstantMessage_routes_a_flagged_group_message_to_group_chat()
    {
        using var session = new GridSession();
        GroupChatMessageEvent? group = null;
        InstantMessageEvent? im = null;
        session.GroupChatMessageReceived += (s, e) => group = e;
        session.InstantMessageReceived += (s, e) => im = e;

        var groupId = UUID.Random();
        var speaker = UUID.Random();
        Invoke(session, "OnInstantMessage",
            Im(InstantMessageDialog.SessionSend, groupId, speaker, "Some Resident", "hello group", groupIM: true));

        Assert.Null(im); // must NOT land in the 1:1 IM path
        Assert.NotNull(group);
        Assert.Equal(groupId.Guid, group!.GroupId); // for group chat the session id IS the group id
        Assert.Equal(speaker.Guid, group.FromAgentId);
        Assert.Equal("Some Resident", group.FromAgentName);
        Assert.Equal("hello group", group.Message);
    }

    [Fact]
    public void OnInstantMessage_still_routes_a_plain_im()
    {
        using var session = new GridSession();
        GroupChatMessageEvent? group = null;
        InstantMessageEvent? im = null;
        session.GroupChatMessageReceived += (s, e) => group = e;
        session.InstantMessageReceived += (s, e) => im = e;

        var sender = UUID.Random();
        Invoke(session, "OnInstantMessage",
            Im(InstantMessageDialog.MessageFromAgent, UUID.Random(), sender, "Friend Resident", "hi", groupIM: false));

        Assert.Null(group);
        Assert.NotNull(im);
        Assert.Equal(sender.Guid, im!.FromAgentId);
        Assert.Equal("hi", im.Message);
    }

    // An empty group line is the same keep-alive/typing artefact local chat filters out; without
    // this it renders as a timestamped sender name with nothing after the colon.
    [Fact]
    public void OnInstantMessage_drops_an_empty_group_message()
    {
        using var session = new GridSession();
        GroupChatMessageEvent? group = null;
        session.GroupChatMessageReceived += (s, e) => group = e;

        Invoke(session, "OnInstantMessage",
            Im(InstantMessageDialog.SessionSend, UUID.Random(), UUID.Random(), "Some Resident", "", groupIM: true));

        Assert.Null(group);
    }

    [Fact]
    public void OnGroupChatJoined_maps_to_neutral_event()
    {
        using var session = new GridSession();
        GroupChatJoinedEvent? received = null;
        session.GroupChatJoined += (s, e) => received = e;

        var groupId = UUID.Random();
        Invoke(session, "OnGroupChatJoined",
            new GroupChatJoinedEventArgs(groupId, "Alpha Explorers", UUID.Zero, true));

        Assert.NotNull(received);
        Assert.Equal(groupId.Guid, received!.GroupId);
        Assert.Equal("Alpha Explorers", received.SessionName);
        Assert.True(received.Success);
    }

    // A group invitation is dialog 3 and matches neither branch below it, so before this it fell
    // through OnInstantMessage and vanished -- the reported "die Gruppeneinladung kam nicht an".
    [Fact]
    public void OnInstantMessage_routes_a_group_invitation()
    {
        using var session = new GridSession();
        GroupInvitationEvent? invite = null;
        InstantMessageEvent? im = null;
        GroupChatMessageEvent? chat = null;
        session.GroupInvitationReceived += (s, e) => invite = e;
        session.InstantMessageReceived += (s, e) => im = e;
        session.GroupChatMessageReceived += (s, e) => chat = e;

        var groupId = UUID.Random();   // an invite carries the GROUP id in FromAgentID
        var sessionId = UUID.Random(); // the viewer's transaction_id, echoed back in the reply
        // Binary bucket: { S32 membership_fee (network order); UUID role_id } -- 20 bytes.
        var bucket = new byte[20];
        bucket[0] = 0; bucket[1] = 0; bucket[2] = 0x01; bucket[3] = 0x2C; // 300
        var args = Im(InstantMessageDialog.GroupInvitation, sessionId, groupId,
            "Alpha Explorers", "Join Alpha Explorers?", groupIM: false, binaryBucket: bucket);

        Invoke(session, "OnInstantMessage", args);

        Assert.Null(im);
        Assert.Null(chat);
        Assert.NotNull(invite);
        Assert.Equal(groupId.Guid, invite!.GroupId);
        Assert.Equal(sessionId.Guid, invite.SessionId);
        Assert.Equal("Alpha Explorers", invite.FromName);
        Assert.Equal("Join Alpha Explorers?", invite.Message);
        Assert.Equal(300, invite.MembershipFee);
    }

    // A bucket of the wrong size means "unparseable", not "free" -- the invitation is still shown,
    // with the fee reported as 0, rather than being dropped like the viewer does.
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(19)]
    public void Group_invitation_with_a_malformed_bucket_still_arrives(int bucketSize)
    {
        using var session = new GridSession();
        GroupInvitationEvent? invite = null;
        session.GroupInvitationReceived += (s, e) => invite = e;

        var args = Im(InstantMessageDialog.GroupInvitation, UUID.Random(), UUID.Random(),
            "Some Group", "Join?", groupIM: false, binaryBucket: new byte[bucketSize]);

        Invoke(session, "OnInstantMessage", args);

        Assert.NotNull(invite);
        Assert.Equal(0, invite!.MembershipFee);
    }

    // Same "no-op gracefully while disconnected" contract as every other GridSession send path.
    [Fact]
    public void Group_send_paths_without_connection_do_not_throw()
    {
        using var session = new GridSession();
        var id = Guid.NewGuid();

        Assert.Null(Record.Exception(() => session.RequestGroups()));
        Assert.Null(Record.Exception(() => session.JoinGroupChat(id)));
        Assert.Null(Record.Exception(() => session.LeaveGroupChat(id)));
        Assert.Null(Record.Exception(() => session.SendGroupMessage(id, "hello")));
        Assert.Null(Record.Exception(() => session.SendGroupMessage(id, "")));
        Assert.Null(Record.Exception(() => session.SendGroupMessage(Guid.Empty, "hello")));
        Assert.Null(Record.Exception(() => session.RespondToGroupInvitation(id, Guid.NewGuid(), accept: true)));
        Assert.Null(Record.Exception(() => session.RespondToGroupInvitation(id, Guid.NewGuid(), accept: false)));
    }
}
