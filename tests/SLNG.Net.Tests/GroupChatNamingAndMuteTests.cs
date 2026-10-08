using System.Reflection;
using System.Text;
using LibreMetaverse;
using SLNG.Core;
using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>
/// BUG-UI-23: a group chat that opens by itself on Second Life (an invitation over the EventQueue, which
/// LibreMetaverse has already accepted) must be titled with the group's name, and a group the user switched off
/// must stay silent on every path. The decisions are pure (<see cref="GroupChatSessionLogic"/>) and are pinned
/// first; the wiring through <c>GridSession.OnInstantMessage</c> follows.
/// </summary>
public class GroupChatNamingAndMuteTests
{
    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    // ---- pure: session name from the bucket --------------------------------------------------------

    [Fact]
    public void DecodeSessionName_reads_up_to_the_first_nul_like_the_viewer()
    {
        Assert.Equal("Alpha Explorers", GroupChatSessionLogic.DecodeSessionName(Bytes("Alpha Explorers\0")));
        // The viewer reads std::string((char*)&bin_bucket[0]): everything after the first NUL is not the name.
        // The old decoder rejected this bucket as "containing a control character".
        Assert.Equal("Alpha Explorers", GroupChatSessionLogic.DecodeSessionName(Bytes("Alpha Explorers\0\u0001junk\0")));
        Assert.Equal("Café Böhm", GroupChatSessionLogic.DecodeSessionName(Bytes("Café Böhm\0")));
        Assert.Equal("No terminator", GroupChatSessionLogic.DecodeSessionName(Bytes("No terminator")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0 })]
    [InlineData(new byte[] { (byte)'x', 0 })]       // one character: the viewer ignores a name of size <= 1
    [InlineData(new byte[] { 1, 2, 3, 4 })]         // not text
    public void DecodeSessionName_is_empty_when_there_is_no_name(byte[]? bucket)
    {
        Assert.Equal(string.Empty, GroupChatSessionLogic.DecodeSessionName(bucket));
    }

    [Fact]
    public void DecodeSessionName_rejects_a_bucket_that_is_a_payload_not_a_name()
    {
        Assert.Equal(string.Empty, GroupChatSessionLogic.DecodeSessionName(new byte[GroupChatSessionLogic.MaxSessionNameBytes + 1]));
        var big = Bytes(new string('a', GroupChatSessionLogic.MaxSessionNameBytes + 1));
        Assert.Equal(string.Empty, GroupChatSessionLogic.DecodeSessionName(big));
    }

    // ---- pure: name selection ----------------------------------------------------------------------

    [Fact]
    public void ChooseName_prefers_membership_then_bucket_then_cache()
    {
        Assert.Equal(("From List", GroupNameSource.Membership), GroupChatSessionLogic.ChooseName("From List", "From Bucket", "From Cache"));
        Assert.Equal(("From Bucket", GroupNameSource.Bucket), GroupChatSessionLogic.ChooseName(null, "From Bucket", "From Cache"));
        Assert.Equal(("From Bucket", GroupNameSource.Bucket), GroupChatSessionLogic.ChooseName("", "From Bucket", "From Cache"));
        Assert.Equal(("From Cache", GroupNameSource.NameCache), GroupChatSessionLogic.ChooseName(null, "", "From Cache"));
        Assert.Equal((string.Empty, GroupNameSource.None), GroupChatSessionLogic.ChooseName(null, null, null));
    }

    [Fact]
    public void ChooseName_never_takes_the_unknown_group_placeholder_for_a_name()
    {
        // A blank name reply leaves "(unknown group)" in the cache. It must not count as known, or the group is
        // never asked for again and the tab keeps a placeholder instead of the real name.
        Assert.False(GroupChatSessionLogic.IsRealName(GroupChatSessionLogic.UnknownGroupName));
        Assert.Equal(("From Bucket", GroupNameSource.Bucket),
            GroupChatSessionLogic.ChooseName(GroupChatSessionLogic.UnknownGroupName, "From Bucket", GroupChatSessionLogic.UnknownGroupName));
        Assert.Equal((string.Empty, GroupNameSource.None),
            GroupChatSessionLogic.ChooseName(null, null, GroupChatSessionLogic.UnknownGroupName));
    }

    // ---- pure: is it group chat --------------------------------------------------------------------

    [Theory]
    // group flag always wins
    [InlineData(InstantMessageDialog.SessionSend, true, false, true, false, true)]
    // LibreMetaverse tracks it and the list has not arrived: nothing can be ruled out
    [InlineData(InstantMessageDialog.SessionSend, false, true, false, false, true)]
    // tracked, list loaded, a group of ours
    [InlineData(InstantMessageDialog.SessionSend, false, true, true, true, true)]
    // tracked but the list says it is no group of ours: an ad-hoc conference
    [InlineData(InstantMessageDialog.SessionSend, false, true, true, false, false)]
    // untracked, unflagged, not in the list: a conference
    [InlineData(InstantMessageDialog.SessionSend, false, false, true, false, false)]
    // BUG-UI-23: one of our groups but LibreMetaverse forgot it (we left it): still group chat
    [InlineData(InstantMessageDialog.SessionSend, false, false, true, true, true)]
    [InlineData(InstantMessageDialog.SessionAdd, false, false, true, true, true)]
    // ...but only for a session dialog; a plain IM never becomes group chat by its session id
    [InlineData(InstantMessageDialog.MessageFromAgent, false, false, true, true, false)]
    public void IsGroupSession_matrix(
        InstantMessageDialog dialog, bool flag, bool tracked, bool loaded, bool member, bool expected)
    {
        Assert.Equal(expected, GroupChatSessionLogic.IsGroupSession(dialog, flag, tracked, loaded, member));
    }

    // ---- pure: mute decision -----------------------------------------------------------------------

    [Theory]
    [InlineData(InstantMessageDialog.SessionAdd, true, true, true)]      // the invitation that opens the session
    [InlineData(InstantMessageDialog.SessionSend, true, true, true)]     // a line over UDP
    [InlineData(InstantMessageDialog.SessionGroupStart, true, true, true)]
    [InlineData(InstantMessageDialog.SessionSend, true, false, false)]   // not muted
    [InlineData(InstantMessageDialog.SessionSend, false, true, false)]   // nothing to consume (keep-alive)
    [InlineData(InstantMessageDialog.MessageFromAgent, true, true, false)] // a 1:1 IM is never the group's
    [InlineData(InstantMessageDialog.StartTyping, true, true, false)]
    public void ShouldConsumeAsIgnored_matrix(InstantMessageDialog dialog, bool hasText, bool muted, bool expected)
    {
        Assert.Equal(expected, GroupChatSessionLogic.ShouldConsumeAsIgnored(dialog, hasText, muted));
    }

    [Fact]
    public void GroupNameRequestGate_asks_once_per_interval_per_group()
    {
        var gate = new GroupNameRequestGate();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        long t0 = 1_000_000;

        Assert.True(gate.ShouldRequest(a, t0));
        Assert.False(gate.ShouldRequest(a, t0 + 1));
        Assert.True(gate.ShouldRequest(b, t0 + 1)); // another group is independent
        Assert.False(gate.ShouldRequest(a, t0 + GroupNameRequestGate.MinInterval.Ticks - 1));
        Assert.True(gate.ShouldRequest(a, t0 + GroupNameRequestGate.MinInterval.Ticks)); // a lost answer is retried
    }

    // ---- wiring through GridSession ----------------------------------------------------------------

    private static void Invoke(GridSession session, string handler, object args)
    {
        var m = typeof(GridSession).GetMethod(handler, BindingFlags.NonPublic | BindingFlags.Instance)!;
        m.Invoke(session, new object?[] { null, args });
    }

    private static InstantMessageEventArgs Im(
        InstantMessageDialog dialog, UUID sessionId, string message, bool groupIM = false, byte[]? bucket = null,
        string fromName = "Some Resident")
    {
        var im = new InstantMessage
        {
            Dialog = dialog,
            IMSessionID = sessionId,
            FromAgentID = UUID.Random(),
            FromAgentName = fromName,
            Message = message,
            GroupIM = groupIM,
            BinaryBucket = bucket ?? Array.Empty<byte>(),
        };
        return new InstantMessageEventArgs(im, null);
    }

    private static void SetMembership(GridSession session, UUID id, string name)
    {
        Invoke(session, "OnCurrentGroups", new CurrentGroupsEventArgs(
            new Dictionary<UUID, Group> { [id] = new Group { ID = id, Name = name } }));
    }

    private sealed class Collected
    {
        public readonly List<GroupChatMessageEvent> Groups = new();
        public readonly List<ConferenceChatMessageEvent> Conferences = new();
        public readonly List<InstantMessageEvent> Ims = new();
        public int Total => Groups.Count + Conferences.Count + Ims.Count;
    }

    private static Collected Listen(GridSession session)
    {
        var c = new Collected();
        session.GroupChatMessageReceived += (s, e) => c.Groups.Add(e);
        session.ConferenceChatMessageReceived += (s, e) => c.Conferences.Add(e);
        session.InstantMessageReceived += (s, e) => c.Ims.Add(e);
        return c;
    }

    [Fact]
    public void An_invitation_names_the_tab_from_the_bucket_even_without_the_membership_list()
    {
        using var session = new GridSession();
        var c = Listen(session);
        var group = UUID.Random();

        // What ChatterBoxInvitation turns into: SessionAdd, the speaker in FromAgentName, the group in the bucket.
        Invoke(session, "OnInstantMessage",
            Im(InstantMessageDialog.SessionAdd, group, "hi all", groupIM: true, bucket: Bytes("Alpha Explorers\0")));

        var line = Assert.Single(c.Groups);
        Assert.Equal("Alpha Explorers", line.GroupName);   // NOT "Some Resident", the speaker
        Assert.Equal("Some Resident", line.FromAgentName);
        // Remembered, so later lookups (and a tab opened afterwards) resolve without the bucket.
        Assert.True(session.TryGetGroupName(group.Guid, out var name));
        Assert.Equal("Alpha Explorers", name);
    }

    [Fact]
    public void A_group_notice_is_a_notice_and_never_a_chat_tab()
    {
        using var session = new GridSession();
        var c = Listen(session);
        GroupNoticeEvent? notice = null;
        session.GroupNoticeReceived += (_, e) => notice = e;

        Invoke(session, "OnInstantMessage",
            Im(InstantMessageDialog.GroupNotice, UUID.Random(), "September results |Well done winners", groupIM: true));

        Assert.Empty(c.Groups);
        Assert.NotNull(notice);
        Assert.Equal("September results", notice!.Subject);
        Assert.Equal("Well done winners", notice.Body);
    }

    [Fact]
    public void A_group_notice_decodes_group_id_and_attachment_from_binary_bucket()
    {
        using var session = new GridSession();
        var c = Listen(session);
        GroupNoticeEvent? notice = null;
        session.GroupNoticeReceived += (_, e) => notice = e;

        var groupId = UUID.Random();
        var bucket = new byte[18 + 15];
        bucket[0] = 1; // hasInventory
        bucket[1] = (byte)AssetType.Object;
        Buffer.BlockCopy(groupId.GetBytes(), 0, bucket, 2, 16);
        var nameBytes = System.Text.Encoding.UTF8.GetBytes("October Gift\0");
        Buffer.BlockCopy(nameBytes, 0, bucket, 18, nameBytes.Length);

        Invoke(session, "OnInstantMessage",
            Im(InstantMessageDialog.GroupNotice, UUID.Random(), "MELODY OCTOBER GROUP GIFT!!! |Gift is in the box", groupIM: true, bucket: bucket));

        Assert.NotNull(notice);
        Assert.Equal(groupId.Guid, notice!.GroupId);
        Assert.Equal("MELODY OCTOBER GROUP GIFT!!!", notice.Subject);
        Assert.Equal("Gift is in the box", notice.Body);
        Assert.True(notice.HasInventory);
        Assert.Equal((int)AssetType.Object, notice.AssetType);
        Assert.Equal("October Gift", notice.ItemName);
    }

    [Fact]
    public void A_group_notice_resolves_group_name_from_membership_list()
    {
        using var session = new GridSession();
        var c = Listen(session);
        var group = UUID.Random();
        SetMembership(session, group, "MELODY");

        GroupNoticeEvent? notice = null;
        session.GroupNoticeReceived += (_, e) => notice = e;

        var bucket = new byte[18];
        Buffer.BlockCopy(group.GetBytes(), 0, bucket, 2, 16);

        Invoke(session, "OnInstantMessage",
            Im(InstantMessageDialog.GroupNotice, UUID.Random(), "OCTOBER GIFT |Enjoy", groupIM: true, bucket: bucket));

        Assert.NotNull(notice);
        Assert.Equal(group.Guid, notice!.GroupId);
        Assert.Equal("MELODY", notice.GroupName);
    }

    [Fact]
    public void TryParseGroupNoticeBucket_handles_truncated_and_invalid_buckets()
    {
        Assert.False(GridSession.TryParseGroupNoticeBucket(null, out _, out _, out _, out _));
        Assert.False(GridSession.TryParseGroupNoticeBucket(new byte[17], out _, out _, out _, out _));

        var id = UUID.Random();
        var bucket = new byte[18];
        Buffer.BlockCopy(id.GetBytes(), 0, bucket, 2, 16);
        Assert.True(GridSession.TryParseGroupNoticeBucket(bucket, out var parsedId, out var hasInv, out var assetType, out var item));
        Assert.Equal(id.Guid, parsedId);
        Assert.False(hasInv);
        Assert.Equal(0, assetType);
        Assert.Empty(item);
    }

    [Fact]
    public void The_membership_list_name_beats_the_bucket()
    {
        using var session = new GridSession();
        var c = Listen(session);
        var group = UUID.Random();
        SetMembership(session, group, "Alpha Explorers");

        Invoke(session, "OnInstantMessage",
            Im(InstantMessageDialog.SessionSend, group, "hello", groupIM: false, bucket: Bytes("Garbled\0")));

        Assert.Equal("Alpha Explorers", Assert.Single(c.Groups).GroupName);
    }

    [Fact]
    public void A_line_of_a_known_group_is_group_chat_even_when_the_library_does_not_track_the_session()
    {
        // The session is in the membership list but carries no group flag and LibreMetaverse's table has no entry
        // (it forgets one when we leave it). It used to fall to the conference path and open a "Conference" tab.
        using var session = new GridSession();
        var c = Listen(session);
        var group = UUID.Random();
        SetMembership(session, group, "Alpha Explorers");

        Invoke(session, "OnInstantMessage", Im(InstantMessageDialog.SessionSend, group, "still here", groupIM: false));

        Assert.Empty(c.Conferences);
        Assert.Equal("Alpha Explorers", Assert.Single(c.Groups).GroupName);
    }

    [Fact]
    public void An_unrelated_session_is_still_a_conference()
    {
        using var session = new GridSession();
        var c = Listen(session);
        SetMembership(session, UUID.Random(), "Alpha Explorers");

        Invoke(session, "OnInstantMessage",
            Im(InstantMessageDialog.SessionSend, UUID.Random(), "meeting", groupIM: false, bucket: Bytes("Our Conference\0")));

        Assert.Empty(c.Groups);
        Assert.Equal("Our Conference", Assert.Single(c.Conferences).SessionName);
    }

    // ---- BUG-NET-32: a conference invitation is a line of the conference, not a 1:1 IM -------------------

    private static InstantMessageEventArgs InvitationIm(UUID sessionId, UUID from, string message)
    {
        var im = new InstantMessage
        {
            Dialog = InstantMessageDialog.MessageFromAgent,
            IMSessionID = sessionId,
            FromAgentID = from,
            FromAgentName = "Inviter Resident",
            Message = message,
            BinaryBucket = Array.Empty<byte>(),
        };
        return new InstantMessageEventArgs(im, null);
    }

    private static void LibreMetaverseRegistersSession(GridSession session, UUID id)
    {
        var client = (GridClient)typeof(GridSession).GetField("_client", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(session)!;
        client.Self.GroupChatSessions.TryAdd(id, new List<ChatSessionMember>());
    }

    [Fact]
    public void An_invitation_that_arrives_as_a_plain_im_opens_the_conference_not_a_one_to_one_tab()
    {
        using var session = new GridSession();
        var c = Listen(session);
        var conference = UUID.Random();
        var inviter = UUID.Random();
        SetMembership(session, UUID.Random(), "Some Group"); // membership loaded: before it, a tracked session counts as group chat
        LibreMetaverseRegistersSession(session, conference); // what ChatterBoxInvitation does before it raises the event

        Invoke(session, "OnInstantMessage", InvitationIm(conference, inviter, "Somebody was invited to the conversation."));

        var line = Assert.Single(c.Conferences);
        Assert.Equal(conference.Guid, line.SessionId);
        Assert.Equal(inviter.Guid, line.FromAgentId);
        Assert.Empty(c.Ims);
    }

    [Fact]
    public void A_one_to_one_im_stays_one_to_one_even_when_the_library_tracks_other_sessions()
    {
        using var session = new GridSession();
        var c = Listen(session);
        var from = UUID.Random();
        LibreMetaverseRegistersSession(session, UUID.Random());

        // The 1:1 session id is the two ids xor-ed; self is the zero id in a session that never logged in.
        Invoke(session, "OnInstantMessage", InvitationIm(from, from, "hello"));

        Assert.Single(c.Ims);
        Assert.Empty(c.Conferences);
    }

    [Fact]
    public void An_im_with_a_foreign_session_id_that_the_library_never_registered_stays_an_im()
    {
        // Only an invitation is registered by the library; a message that merely has an odd session id is not one.
        using var session = new GridSession();
        var c = Listen(session);

        Invoke(session, "OnInstantMessage", InvitationIm(UUID.Random(), UUID.Random(), "hello"));

        Assert.Single(c.Ims);
        Assert.Empty(c.Conferences);
    }

    [Fact]
    public void A_group_nobody_can_name_is_reported_unnamed_so_the_app_asks_and_re_titles()
    {
        using var session = new GridSession();
        var c = Listen(session);
        var group = UUID.Random();
        Guid? resolved = null;
        session.NameResolved += (s, e) => resolved = e.Id;

        Invoke(session, "OnInstantMessage", Im(InstantMessageDialog.SessionSend, group, "who am I", groupIM: true));

        Assert.Equal(string.Empty, Assert.Single(c.Groups).GroupName);
        Assert.False(session.TryGetGroupName(group.Guid, out _));

        // The grid answers with a blank name first (the placeholder), which must not count as known...
        Invoke(session, "OnGroupNamesReply", new GroupNamesEventArgs(new Dictionary<UUID, string> { [group] = "" }));
        Assert.False(session.TryGetGroupName(group.Guid, out _));

        // ...so a later line carrying the bucket still names it, and the listeners are told.
        resolved = null;
        Invoke(session, "OnInstantMessage",
            Im(InstantMessageDialog.SessionSend, group, "second", groupIM: true, bucket: Bytes("Alpha Explorers\0")));
        Assert.Equal("Alpha Explorers", c.Groups[1].GroupName);
        Assert.Equal(group.Guid, resolved);
        Assert.True(session.TryGetGroupName(group.Guid, out var name));
        Assert.Equal("Alpha Explorers", name);
    }

    [Fact]
    public void A_real_name_reply_re_titles_the_group_and_an_empty_one_does_not_erase_it()
    {
        using var session = new GridSession();
        var group = UUID.Random();
        var resolved = new List<string>();
        session.NameResolved += (s, e) => resolved.Add(e.Name);

        Invoke(session, "OnGroupNamesReply", new GroupNamesEventArgs(new Dictionary<UUID, string> { [group] = "Alpha Explorers" }));
        Invoke(session, "OnGroupNamesReply", new GroupNamesEventArgs(new Dictionary<UUID, string> { [group] = "" }));

        Assert.Equal(new[] { "Alpha Explorers" }, resolved);
        Assert.True(session.TryGetGroupName(group.Guid, out var name));
        Assert.Equal("Alpha Explorers", name);
    }

    [Fact]
    public void Joining_a_group_session_remembers_the_name_the_grid_gives_it_but_a_conferences_is_not_a_group_name()
    {
        using var session = new GridSession();
        var group = UUID.Random();
        var conference = UUID.Random();
        SetMembership(session, UUID.Random(), "Other Group"); // a list is loaded; neither id is in it... except below

        // Not in the list: ignored.
        Invoke(session, "OnGroupChatJoined", new GroupChatJoinedEventArgs(conference, "Some Conference", UUID.Zero, true));
        Assert.False(session.TryGetGroupName(conference.Guid, out _));

        SetMembership(session, group, ""); // member, but the list gave no name
        Invoke(session, "OnGroupChatJoined", new GroupChatJoinedEventArgs(group, "Alpha Explorers", UUID.Zero, true));
        Assert.True(session.TryGetGroupName(group.Guid, out var name));
        Assert.Equal("Alpha Explorers", name);
    }

    // ---- mute ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(InstantMessageDialog.SessionAdd, true)]    // the invitation that opens the session
    [InlineData(InstantMessageDialog.SessionSend, true)]   // a UDP line, group flag set
    [InlineData(InstantMessageDialog.SessionSend, false)]  // a UDP line with no flag (the session was forgotten)
    [InlineData(InstantMessageDialog.SessionGroupStart, false)]
    public void A_muted_group_is_silent_on_every_path(InstantMessageDialog dialog, bool flagged)
    {
        using var session = new GridSession();
        var c = Listen(session);
        var group = UUID.Random();
        // Both a loaded membership list and none: the mute must not depend on either.
        session.GroupChatIgnored = id => id == group.Guid;

        Invoke(session, "OnInstantMessage", Im(dialog, group, "line 1", flagged, Bytes("Alpha Explorers\0")));
        SetMembership(session, group, "Alpha Explorers");
        Invoke(session, "OnInstantMessage", Im(dialog, group, "line 2", flagged, Bytes("Alpha Explorers\0")));
        Invoke(session, "OnInstantMessage", Im(InstantMessageDialog.SessionSend, group, "stray line after the leave", groupIM: false));

        Assert.Equal(0, c.Total);
    }

    [Fact]
    public void Muting_one_group_leaves_the_others_and_conferences_alone()
    {
        using var session = new GridSession();
        var c = Listen(session);
        var muted = UUID.Random();
        var other = UUID.Random();
        session.GroupChatIgnored = id => id == muted.Guid;

        Invoke(session, "OnInstantMessage", Im(InstantMessageDialog.SessionSend, muted, "no", groupIM: true));
        Invoke(session, "OnInstantMessage", Im(InstantMessageDialog.SessionSend, other, "yes", groupIM: true));
        Invoke(session, "OnInstantMessage", Im(InstantMessageDialog.SessionSend, UUID.Random(), "conf", groupIM: false));

        Assert.Equal("yes", Assert.Single(c.Groups).Message);
        Assert.Equal("conf", Assert.Single(c.Conferences).Message);
    }

    [Fact]
    public void Switching_the_mute_off_again_lets_the_next_line_through()
    {
        using var session = new GridSession();
        var c = Listen(session);
        var group = UUID.Random();
        bool muted = true;
        session.GroupChatIgnored = id => muted && id == group.Guid;

        Invoke(session, "OnInstantMessage", Im(InstantMessageDialog.SessionSend, group, "muted", groupIM: true));
        muted = false;
        Invoke(session, "OnInstantMessage", Im(InstantMessageDialog.SessionSend, group, "audible", groupIM: true));

        Assert.Equal("audible", Assert.Single(c.Groups).Message);
    }
}
