using LibreMetaverse;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>
/// BUG-NET-23: SLNG sends AgentThrottle itself. LibreMetaverse's own send ends in
/// <c>UdpThrottle.Update</c>, which disposes the rate limiters the outgoing packet loop may be
/// holding; one badly timed region connect then stopped every outgoing packet for the rest of the
/// session. These pin both halves: the library's send stays off, and ours is the same packet --
/// carrying FEAT-NET-05's rates, which LibreMetaverse's own AgentThrottle would have capped.
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
    public void The_packet_carries_the_rates_as_LibreMetaverse_lays_them_out()
    {
        // Distinct values under LibreMetaverse's per-category caps, so its decoder reads them back
        // unchanged and a field in the wrong slot cannot pass by accident.
        var rates = new ThrottleRates(100_000f, 80_000f, 20_000f, 15_000f, 300_000f, 400_000f, 150_000f);
        var agent = UUID.Random();
        var sessionId = UUID.Random();
        const uint circuit = 123_456u;

        var packet = GridSession.BuildAgentThrottlePacket(agent, sessionId, circuit, rates);

        Assert.Equal(agent, packet.AgentData.AgentID);
        Assert.Equal(sessionId, packet.AgentData.SessionID);
        Assert.Equal(circuit, packet.AgentData.CircuitCode);
        // AgentThrottle.Set sends 0 too; the grids have taken it that way for years.
        Assert.Equal(0u, packet.Throttle.GenCounter);
        Assert.Equal(rates.ToBytes(), packet.Throttle.Throttles);

        // Seven little-endian floats, in the order the simulator reads them.
        var decoded = new AgentThrottle(packet.Throttle.Throttles, 0);
        Assert.Equal(rates.Resend, decoded.Resend);
        Assert.Equal(rates.Land, decoded.Land);
        Assert.Equal(rates.Wind, decoded.Wind);
        Assert.Equal(rates.Cloud, decoded.Cloud);
        Assert.Equal(rates.Task, decoded.Task);
        Assert.Equal(rates.Texture, decoded.Texture);
        Assert.Equal(rates.Asset, decoded.Asset);
    }

    [Fact]
    public void The_packet_is_not_capped_by_LibreMetaverses_category_limits()
    {
        // FEAT-NET-05: AgentThrottle's setters cap Texture at 446 000 bit/s, Land at 170 000 and so
        // on. The viewer's default asks for 1528 kbps of Texture -- going through _client.Throttle
        // would have sent a third of it without a word.
        var rates = ViewerThrottlePresets.ForMaxBandwidth(ViewerThrottlePresets.DefaultBandwidthKbps);

        var packet = GridSession.BuildAgentThrottlePacket(UUID.Random(), UUID.Random(), 1u, rates);

        Assert.True(rates.Texture > 446_000f);
        Assert.Equal(rates.ToBytes(), packet.Throttle.Throttles);
    }

    [Fact]
    public void A_new_session_asks_for_the_viewers_default_bandwidth()
    {
        using var session = new GridSession();

        Assert.Equal(ViewerThrottlePresets.DefaultBandwidthKbps, session.MaxBandwidthKbps);
        Assert.Equal(ViewerThrottlePresets.ForMaxBandwidth(ViewerThrottlePresets.DefaultBandwidthKbps), session.ThrottleRates);
    }

    [Fact]
    public void Changing_the_bandwidth_before_login_clamps_it_and_changes_the_rates()
    {
        using var session = new GridSession();

        session.MaxBandwidthKbps = 50_000f; // not connected: nothing to send, nothing to throw

        Assert.Equal(ViewerThrottlePresets.MaxBandwidthKbps, session.MaxBandwidthKbps);
        Assert.Equal(ViewerThrottlePresets.ForMaxBandwidth(ViewerThrottlePresets.MaxBandwidthKbps), session.ThrottleRates);
    }
}
