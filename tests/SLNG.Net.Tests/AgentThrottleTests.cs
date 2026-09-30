using LibreMetaverse;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>
/// BUG-NET-23: SLNG sends AgentThrottle itself. LibreMetaverse's own send ends in
/// <c>UdpThrottle.Update</c>, which disposes the rate limiters the outgoing packet loop may be
/// holding; one badly timed region connect then stopped every outgoing packet for the rest of the
/// session. These pin both halves: the library's send stays off, and ours is the same packet.
/// </summary>
public class AgentThrottleTests
{
    [Fact]
    public void A_session_switches_the_librarys_own_throttle_send_off()
    {
        using var session = new GridSession();

        Assert.False(session.LibrarySendsAgentThrottle);
    }

    [Fact]
    public void The_packet_is_the_one_LibreMetaverse_would_have_sent()
    {
        var client = new GridClient();
        // Distinct values, so a field in the wrong slot cannot pass by accident.
        client.Throttle.Resend = 100_000f;
        client.Throttle.Land = 80_000f;
        client.Throttle.Wind = 20_000f;
        client.Throttle.Cloud = 15_000f;
        client.Throttle.Task = 300_000f;
        client.Throttle.Texture = 400_000f;
        client.Throttle.Asset = 150_000f;
        var agent = UUID.Random();
        var sessionId = UUID.Random();
        const uint circuit = 123_456u;

        var packet = GridSession.BuildAgentThrottlePacket(agent, sessionId, circuit, client.Throttle);

        Assert.Equal(agent, packet.AgentData.AgentID);
        Assert.Equal(sessionId, packet.AgentData.SessionID);
        Assert.Equal(circuit, packet.AgentData.CircuitCode);
        // AgentThrottle.Set sends 0 too; the grids have taken it that way for years.
        Assert.Equal(0u, packet.Throttle.GenCounter);
        Assert.Equal(client.Throttle.ToBytes(), packet.Throttle.Throttles);

        // Seven little-endian floats, in the order the simulator reads them.
        var decoded = new AgentThrottle(packet.Throttle.Throttles, 0);
        Assert.Equal(client.Throttle.Resend, decoded.Resend);
        Assert.Equal(client.Throttle.Land, decoded.Land);
        Assert.Equal(client.Throttle.Wind, decoded.Wind);
        Assert.Equal(client.Throttle.Cloud, decoded.Cloud);
        Assert.Equal(client.Throttle.Task, decoded.Task);
        Assert.Equal(client.Throttle.Texture, decoded.Texture);
        Assert.Equal(client.Throttle.Asset, decoded.Asset);
    }
}
