using System.Reflection;
using LibreMetaverse;
using SLNG.Core;
using SLNG.Core.Presence;
using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>FEAT-UI-75: another viewer's automatic answer is shown as a line of the conversation.</summary>
public class AutoresponseIncomingTests
{
    private static List<InstantMessageEvent> Receive(InstantMessageDialog dialog, string message)
    {
        using var session = new GridSession();
        var received = new List<InstantMessageEvent>();
        session.InstantMessageReceived += (s, e) => received.Add(e);
        // Do Not Disturb on: an answer to the answer would be the bug this guards against.
        session.Presence = new PresenceConfig(DoNotDisturb: true);

        var from = UUID.Random();
        var im = new InstantMessage
        {
            Dialog = dialog,
            IMSessionID = from,
            FromAgentID = from,
            FromAgentName = "Denise Resident",
            Message = message,
            Offline = InstantMessageOnline.Online,
            Timestamp = DateTime.UtcNow,
            BinaryBucket = Array.Empty<byte>(),
        };
        typeof(GridSession).GetMethod("OnInstantMessage", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(session, new object?[] { null, new InstantMessageEventArgs(im, null) });
        return received;
    }

    [Fact]
    public void A_do_not_disturb_answer_from_another_viewer_is_shown()
    {
        var e = Assert.Single(Receive(InstantMessageDialog.BusyAutoResponse, "I am busy."));
        Assert.Equal("I am busy.", e.Message);
        Assert.Equal("Denise Resident", e.FromAgentName);
    }

    [Fact]
    public void An_empty_automatic_answer_is_dropped()
    {
        Assert.Empty(Receive(InstantMessageDialog.BusyAutoResponse, ""));
    }

    [Fact]
    public void A_plain_message_is_still_shown_while_do_not_disturb_is_on()
    {
        var e = Assert.Single(Receive(InstantMessageDialog.MessageFromAgent, "hello"));
        Assert.Equal("hello", e.Message);
    }
}
