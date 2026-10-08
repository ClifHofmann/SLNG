using System;
using System.Buffers.Binary;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>
/// FEAT-NET-05: the bandwidth the reference viewer asks a simulator for, per category. The table and
/// the interpolation are llviewerthrottle.cpp's (BW_PRESET_* :73-76, getThrottleGroup :262-300); the
/// 1.5 headroom is resetDynamicThrottle's MAX_FRACTIONAL, which is what the viewer sends at login.
/// Values in the viewer are kbps; the packet carries kbps * 1024 (sendToSim, "sim wants BPS").
/// </summary>
public class ViewerThrottlePresetsTests
{
    private static ThrottleRates Kbps(float resend, float land, float wind, float cloud, float task, float texture, float asset)
        => new(resend * 1024f, land * 1024f, wind * 1024f, cloud * 1024f, task * 1024f, texture * 1024f, asset * 1024f);

    [Fact]
    public void Three_thousand_kbps_is_the_viewers_extrapolated_group()
    {
        // 1000 row + 4 x (1000 row - 500 row): the numbers the viewer's preset table gives for its default.
        Assert.Equal(Kbps(300, 220, 44, 44, 1006, 1006, 380), ViewerThrottlePresets.GetThrottleGroup(3000f));
    }

    [Theory]
    [InlineData(50f, 5, 10, 3, 3, 10, 10, 9)]
    [InlineData(300f, 30, 40, 9, 9, 86, 86, 40)]
    [InlineData(500f, 50, 70, 14, 14, 136, 136, 80)]
    [InlineData(1000f, 100, 100, 20, 20, 310, 310, 140)]
    public void A_preset_bandwidth_is_its_row(float kbps, float resend, float land, float wind, float cloud, float task, float texture, float asset)
    {
        Assert.Equal(Kbps(resend, land, wind, cloud, task, texture, asset), ViewerThrottlePresets.GetThrottleGroup(kbps));
    }

    [Fact]
    public void Between_two_rows_it_interpolates()
    {
        // Half way from the 300 row to the 500 row.
        Assert.Equal(Kbps(40, 55, 11.5f, 11.5f, 111, 111, 60), ViewerThrottlePresets.GetThrottleGroup(400f));
    }

    [Theory]
    [InlineData(50f)]
    [InlineData(150f)]
    [InlineData(400f)]
    [InlineData(777f)]
    [InlineData(3000f)]
    [InlineData(4500f)]
    [InlineData(6000f)]
    public void The_categories_add_up_to_the_bandwidth_asked_for(float kbps)
    {
        // Every row's total is its own name, so interpolating and extrapolating keep the total exact.
        Assert.Equal(kbps * 1024f, ViewerThrottlePresets.GetThrottleGroup(kbps).Total, 0.5f);
    }

    [Fact]
    public void The_group_is_clamped_to_fifty_and_six_thousand_kbps()
    {
        // llviewerthrottle.cpp MIN_BANDWIDTH / MAX_BANDWIDTH at file scope (50 / 6000).
        Assert.Equal(ViewerThrottlePresets.GetThrottleGroup(50f), ViewerThrottlePresets.GetThrottleGroup(10f));
        Assert.Equal(Kbps(600, 400, 80, 80, 2050, 2050, 740), ViewerThrottlePresets.GetThrottleGroup(6000f));
        Assert.Equal(ViewerThrottlePresets.GetThrottleGroup(6000f), ViewerThrottlePresets.GetThrottleGroup(9000f));
    }

    [Fact]
    public void The_default_setting_sends_what_the_viewer_sends_at_login()
    {
        // resetDynamicThrottle: getThrottleGroup(ThrottleBandwidthKBPS * MAX_FRACTIONAL) = the 4500 group.
        Assert.Equal(3000f, ViewerThrottlePresets.DefaultBandwidthKbps);
        Assert.Equal(Kbps(450, 310, 62, 62, 1528, 1528, 560),
            ViewerThrottlePresets.ForMaxBandwidth(ViewerThrottlePresets.DefaultBandwidthKbps));
    }

    [Fact]
    public void A_setting_of_one_thousand_asks_for_the_fifteen_hundred_group()
    {
        Assert.Equal(ViewerThrottlePresets.GetThrottleGroup(1500f), ViewerThrottlePresets.ForMaxBandwidth(1000f));
    }

    [Theory]
    [InlineData(20f, 100f)]
    [InlineData(100f, 100f)]
    [InlineData(10000f, 10000f)]
    [InlineData(50000f, 10000f)]
    public void The_setting_is_clamped_like_getMaxBandwidthKbps(float asked, float clamped)
    {
        Assert.Equal(clamped, ViewerThrottlePresets.ClampBandwidth(asked));
        Assert.Equal(ViewerThrottlePresets.GetThrottleGroup(clamped * 1.5f), ViewerThrottlePresets.ForMaxBandwidth(asked));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void A_setting_that_is_not_a_number_is_the_default(float asked)
    {
        Assert.Equal(ViewerThrottlePresets.DefaultBandwidthKbps, ViewerThrottlePresets.ClampBandwidth(asked));
    }

    [Fact]
    public void The_rates_go_on_the_wire_as_seven_little_endian_floats_in_category_order()
    {
        var rates = new ThrottleRates(1f, 2f, 3f, 4f, 5f, 6f, 7f);

        byte[] bytes = rates.ToBytes();

        Assert.Equal(28, bytes.Length);
        for (int i = 0; i < 7; i++)
            Assert.Equal(i + 1f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * 4, 4)));
    }
}
