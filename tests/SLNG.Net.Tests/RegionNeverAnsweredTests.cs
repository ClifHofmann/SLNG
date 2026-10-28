using System.Reflection;
using LibreMetaverse;
using SLNG.Core;
using Xunit;
using DisconnectType = LibreMetaverse.NetworkManager.DisconnectType;

namespace SLNG.Net.Tests;

/// <summary>
/// BUG-NET-36: a region that never sent its handshake -- the viewer's UDP is blocked, the user sees
/// only sky and water -- ends in a timeout like any other dropped connection. The session end has
/// to say which of the two it was, because only one of them is fixed on the user's own machine.
/// </summary>
public class RegionNeverAnsweredTests
{
    [Theory]
    [InlineData(DisconnectType.NetworkTimeout, true, SessionEndReason.RegionNeverAnswered)]
    [InlineData(DisconnectType.NetworkTimeout, false, SessionEndReason.NetworkTimeout)]
    // Only a timeout is re-read: a kick or a shutdown is what it says, answered or not.
    [InlineData(DisconnectType.SimShutdown, true, SessionEndReason.SimShutdown)]
    [InlineData(DisconnectType.ServerInitiated, true, SessionEndReason.ServerInitiated)]
    [InlineData(DisconnectType.ServerInitiated, false, SessionEndReason.ServerInitiated)]
    public void A_timeout_from_a_region_that_never_answered_gets_its_own_reason(
        DisconnectType reason, bool regionNeverAnswered, SessionEndReason expected)
    {
        Assert.Equal(expected, GridSession.SessionEndReasonFor(reason, regionNeverAnswered));
    }

    private static SessionEndedEvent? TimeOut(GridSession session, bool regionUnanswered)
    {
        typeof(GridSession).GetField("_currentRegionUnanswered", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(session, regionUnanswered);

        SessionEndedEvent? raised = null;
        session.SessionEnded += (_, e) => raised = e;
        typeof(GridSession).GetMethod("OnNetworkDisconnected", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(session, new object?[] { null, new DisconnectedEventArgs(DisconnectType.NetworkTimeout, "timed out") });
        return raised;
    }

    [Fact]
    public void The_session_end_says_the_region_never_answered()
    {
        using var session = new GridSession();

        var e = TimeOut(session, regionUnanswered: true);

        Assert.NotNull(e);
        Assert.Equal(SessionEndReason.RegionNeverAnswered, e!.Reason);
    }

    [Fact]
    public void A_region_that_answered_and_then_went_quiet_is_an_ordinary_timeout()
    {
        using var session = new GridSession();

        var e = TimeOut(session, regionUnanswered: false);

        Assert.NotNull(e);
        Assert.Equal(SessionEndReason.NetworkTimeout, e!.Reason);
    }
}
