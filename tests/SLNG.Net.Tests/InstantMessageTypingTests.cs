using System.Reflection;
using LibreMetaverse;
using SLNG.Core;
using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

public class InstantMessageTypingTests
{
    private static void Invoke(GridSession session, string handler, object args)
    {
        var m = typeof(GridSession).GetMethod(handler, BindingFlags.NonPublic | BindingFlags.Instance)!;
        m.Invoke(session, new object?[] { null, args });
    }

    private static InstantMessageEventArgs Im(
        InstantMessageDialog dialog, UUID sessionId, UUID fromAgent, string fromName, bool groupIM = false)
    {
        var im = new InstantMessage
        {
            Dialog = dialog,
            IMSessionID = sessionId,
            FromAgentID = fromAgent,
            FromAgentName = fromName,
            Message = "typing",
            GroupIM = groupIM,
            BinaryBucket = Array.Empty<byte>(),
        };
        return new InstantMessageEventArgs(im, null);
    }

    [Fact]
    public void StartTyping_with_zero_session_id_raises_InstantMessageTyping()
    {
        using var session = new GridSession();
        InstantMessageTypingEvent? raised = null;
        session.InstantMessageTyping += (s, e) => raised = e;

        var typist = UUID.Random();
        Invoke(session, "OnInstantMessage", Im(
            InstantMessageDialog.StartTyping, UUID.Zero, typist, "Alice Resident"));

        Assert.NotNull(raised);
        Assert.Equal(typist.Guid, raised!.FromAgentId);
        Assert.Equal("Alice Resident", raised.FromAgentName);
        Assert.True(raised.Typing);
    }

    [Fact]
    public void StartTyping_with_peer_to_peer_session_id_raises_InstantMessageTyping()
    {
        using var session = new GridSession();
        InstantMessageTypingEvent? raised = null;
        session.InstantMessageTyping += (s, e) => raised = e;

        var typist = UUID.Random();
        var p2p = SessionIds.PeerToPeer(Guid.Empty, typist.Guid); // client agentId is Guid.Empty before login
        Invoke(session, "OnInstantMessage", Im(
            InstantMessageDialog.StartTyping, new UUID(p2p), typist, "Bob Resident"));

        Assert.NotNull(raised);
        Assert.Equal(typist.Guid, raised!.FromAgentId);
        Assert.Equal("Bob Resident", raised.FromAgentName);
        Assert.True(raised.Typing);
    }

    [Fact]
    public void StartTyping_with_typist_id_as_session_id_raises_InstantMessageTyping()
    {
        using var session = new GridSession();
        InstantMessageTypingEvent? raised = null;
        session.InstantMessageTyping += (s, e) => raised = e;

        var typist = UUID.Random();
        Invoke(session, "OnInstantMessage", Im(
            InstantMessageDialog.StartTyping, typist, typist, "Charlie Resident"));

        Assert.NotNull(raised);
        Assert.Equal(typist.Guid, raised!.FromAgentId);
        Assert.True(raised.Typing);
    }

    [Fact]
    public void StopTyping_raises_InstantMessageTyping_with_Typing_false()
    {
        using var session = new GridSession();
        InstantMessageTypingEvent? raised = null;
        session.InstantMessageTyping += (s, e) => raised = e;

        var typist = UUID.Random();
        Invoke(session, "OnInstantMessage", Im(
            InstantMessageDialog.StopTyping, UUID.Zero, typist, "Alice Resident"));

        Assert.NotNull(raised);
        Assert.Equal(typist.Guid, raised!.FromAgentId);
        Assert.False(raised.Typing);
    }

    [Fact]
    public void StartTyping_with_empty_typist_is_ignored()
    {
        using var session = new GridSession();
        InstantMessageTypingEvent? raised = null;
        session.InstantMessageTyping += (s, e) => raised = e;

        Invoke(session, "OnInstantMessage", Im(
            InstantMessageDialog.StartTyping, UUID.Zero, UUID.Zero, "Ghost Resident"));

        Assert.Null(raised);
    }

    [Fact]
    public void StartTyping_with_group_flag_is_ignored()
    {
        using var session = new GridSession();
        InstantMessageTypingEvent? raised = null;
        session.InstantMessageTyping += (s, e) => raised = e;

        var typist = UUID.Random();
        Invoke(session, "OnInstantMessage", Im(
            InstantMessageDialog.StartTyping, UUID.Random(), typist, "Group Member", groupIM: true));

        Assert.Null(raised);
    }
}
