using System.Numerics;
using System.Text;
using SLNG.Core;
using Xunit;

namespace SLNG.Net.Tests;

public class ProtocolLoggerTests
{
    [Fact]
    public void ProtocolLogger_FormatsChatMessagesCorrectly()
    {
        using var session = new GridSession();
        var sb = new StringBuilder();

        using var logger = new ProtocolLogger(session, log => sb.AppendLine(log));

        var chatEvent = new ChatMessageEvent("System", "Welcome to OpenSim", 1);
        session.RaiseChatMessage(chatEvent);

        var output = sb.ToString().Trim();
        Assert.Equal("[CHAT] From: 'System', Type: 1, Msg: \"Welcome to OpenSim\"", output);
    }

    [Fact]
    public void ProtocolLogger_FormatsObjectUpdatesCorrectly()
    {
        using var session = new GridSession();
        var sb = new StringBuilder();

        using var logger = new ProtocolLogger(session, log => sb.AppendLine(log));

        var objEvent = new ObjectUpdateEvent(123ul, 42, new Vector3(1, 2, 3), Quaternion.Identity, new Vector3(1, 1, 1), 1, false, Guid.Empty, Guid.Empty);
        session.RaiseObjectUpdate(objEvent);

        var output = sb.ToString().Trim();
        var expectedPos = new Vector3(1, 2, 3).ToString();
        var expectedRot = Quaternion.Identity.ToString();
        Assert.Equal($"[OBJECT] Update LocalID: 42, Pos: {expectedPos}, Rot: {expectedRot}", output);
    }
}
