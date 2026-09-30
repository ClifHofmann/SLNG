using System.Net;
using System.Reflection;
using LibreMetaverse;
using SLNG.Core;
using Xunit;
using DisconnectType = LibreMetaverse.NetworkManager.DisconnectType;

namespace SLNG.Net.Tests;

/// <summary>
/// BUG-NET-24: telling a region that goes with the whole session apart from one that drops out of
/// a session that carries on. The first stays on screen behind the "logged out" message, the
/// second is unloaded as before.
///
/// <para>LibreMetaverse 3.1.6 raises <c>SimDisconnected</c> from exactly three places, and that is
/// what the rule reads: <c>DisconnectSim</c> (one region; always <c>NetworkTimeout</c>), and
/// <c>ShutdownAsync</c> for the neighbours and then the current region (the shutdown's own reason).
/// So <c>ServerInitiated</c> and <c>SimShutdown</c> only ever arrive with the whole session ending;
/// <c>NetworkTimeout</c> needs a second look, because one neighbour dropping out reports it
/// too.</para>
/// </summary>
public class SessionEndRegionTests
{
    [Theory]
    // A kick ("logged in from another location") and the last region going away: always a shutdown.
    [InlineData(DisconnectType.ServerInitiated, false, true, false, true)]
    [InlineData(DisconnectType.ServerInitiated, true, true, false, true)]
    [InlineData(DisconnectType.SimShutdown, true, true, false, true)]
    // The session timing out as a whole: LibreMetaverse clears Connected before it shuts down.
    [InlineData(DisconnectType.NetworkTimeout, false, false, false, true)]
    [InlineData(DisconnectType.NetworkTimeout, true, false, false, true)]
    // The region we stand in going away ends the session, whatever happens to the others.
    [InlineData(DisconnectType.NetworkTimeout, true, true, false, true)]
    // A neighbour dropping out of a live session, or the old region after a teleport: unload.
    [InlineData(DisconnectType.NetworkTimeout, false, true, false, false)]
    // Our own logout: the client is on its way to the login screen anyway.
    [InlineData(DisconnectType.ClientInitiated, true, true, false, false)]
    // ...unless the session had already been declared over, which is how the dead-event-queue
    // path works: SessionEnded first, then SLNG logs out by itself.
    [InlineData(DisconnectType.ClientInitiated, true, true, true, true)]
    [InlineData(DisconnectType.NetworkTimeout, false, true, true, true)]
    public void A_region_goes_with_the_session_only_when_the_whole_session_ends(
        DisconnectType reason, bool isCurrentRegion, bool networkConnected, bool sessionAlreadyEnded, bool expected)
    {
        Assert.Equal(expected, GridSession.IsPartOfSessionEnd(reason, isCurrentRegion, networkConnected, sessionAlreadyEnded));
    }

    private static RegionDisconnectedEvent? Disconnect(GridSession session, DisconnectType reason)
    {
        var client = (GridClient)typeof(GridSession)
            .GetField("_client", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(session)!;
        var sim = new Simulator(client, new IPEndPoint(IPAddress.Loopback, 9000), 1234UL);

        RegionDisconnectedEvent? raised = null;
        session.RegionDisconnectedReceived += (_, e) => raised = e;
        typeof(GridSession).GetMethod("OnSimDisconnected", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(session, new object?[] { null, new SimDisconnectedEventArgs(sim, reason, true) });
        return raised;
    }

    // The flag has to reach the world model: that is where the unload is decided.
    [Fact]
    public void A_kick_tells_the_world_the_region_went_with_the_session()
    {
        using var session = new GridSession();

        var e = Disconnect(session, DisconnectType.ServerInitiated);

        Assert.NotNull(e);
        Assert.Equal(1234UL, e!.RegionHandle);
        Assert.True(e.SessionEnded);
    }

    [Fact]
    public void Our_own_logout_still_unloads_the_region()
    {
        using var session = new GridSession();

        var e = Disconnect(session, DisconnectType.ClientInitiated);

        Assert.NotNull(e);
        Assert.False(e!.SessionEnded);
    }
}
