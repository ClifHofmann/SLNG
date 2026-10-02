using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

public class UiScalePolicyTests
{
    [Theory]
    [InlineData(96, 1.0f)]
    [InlineData(120, 1.25f)]
    [InlineData(144, 1.5f)]
    [InlineData(168, 1.75f)]
    [InlineData(192, 2.0f)]
    [InlineData(288, 3.0f)]
    [InlineData(336, 3.5f)]
    public void Windows_scales_are_read_from_the_dpi_because_the_engine_reports_1(int dpi, float expected)
    {
        // Godot answers 1.0 for the screen scale on Windows at every setting; the DPI is the signal.
        Assert.Equal(expected, UiScalePolicy.DetectOsScale(1.0f, dpi, dpiIsTrustworthy: true), 3);
    }

    [Fact]
    public void A_reported_scale_wins_where_the_platform_implements_it()
    {
        Assert.Equal(2.0f, UiScalePolicy.DetectOsScale(2.0f, 72, dpiIsTrustworthy: false));
        Assert.Equal(1.25f, UiScalePolicy.DetectOsScale(1.25f, 0, dpiIsTrustworthy: false), 3);
        // ... even over a DPI that would say something else.
        Assert.Equal(2.0f, UiScalePolicy.DetectOsScale(2.0f, 96, dpiIsTrustworthy: true));
    }

    [Fact]
    public void A_dpi_that_is_a_panel_density_not_a_setting_is_ignored()
    {
        // X11: 157 dpi is how dense the panel is, not how the user scaled the desktop.
        Assert.Equal(1.0f, UiScalePolicy.DetectOsScale(1.0f, 157, dpiIsTrustworthy: false));
    }

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(-1f, -1)]
    [InlineData(float.NaN, 0)]
    [InlineData(float.PositiveInfinity, 0)]
    public void An_unknown_display_is_100_percent(float reported, int dpi)
    {
        Assert.Equal(1.0f, UiScalePolicy.DetectOsScale(reported, dpi, dpiIsTrustworthy: true));
    }

    [Fact]
    public void An_odd_dpi_is_rounded_to_the_slider_step()
    {
        // 110 % in Windows = 105.6 dpi.
        Assert.Equal(1.1f, UiScalePolicy.DetectOsScale(1.0f, 106, dpiIsTrustworthy: true), 3);
    }

    [Fact]
    public void An_os_scale_beyond_the_range_is_clamped_not_dropped()
    {
        Assert.Equal(UiScalePolicy.Max, UiScalePolicy.DetectOsScale(1.0f, 960, dpiIsTrustworthy: true));
        Assert.Equal(UiScalePolicy.Min, UiScalePolicy.DetectOsScale(1.0f, 60, dpiIsTrustworthy: true)); // 0.625
    }

    [Fact]
    public void Range_reaches_at_least_300_percent()
    {
        Assert.True(UiScalePolicy.Max >= 3.0f);
        Assert.Equal(3.0f, UiScalePolicy.Clamp(3.0f));
    }

    [Theory]
    [InlineData(0.1f, 0.8f)]
    [InlineData(1.25f, 1.25f)]
    [InlineData(99f, 4.0f)]
    [InlineData(0f, 1.0f)]
    [InlineData(-3f, 1.0f)]
    [InlineData(float.NaN, 1.0f)]
    public void Clamp_keeps_the_scale_in_range_and_unusable_numbers_at_1(float input, float expected)
    {
        Assert.Equal(expected, UiScalePolicy.Clamp(input));
    }

    [Fact]
    public void A_fresh_install_is_automatic()
    {
        Assert.True(UiScalePolicy.IsAutomatic(savedAutomatic: null, hasSavedScale: false));
    }

    [Fact]
    public void A_scale_saved_before_automatic_existed_stays_manual()
    {
        // The migration: a ui_scale of 1.25 with no flag beside it is somebody's choice.
        Assert.False(UiScalePolicy.IsAutomatic(savedAutomatic: null, hasSavedScale: true));
    }

    [Fact]
    public void An_explicit_flag_wins_over_the_presence_of_a_scale()
    {
        Assert.True(UiScalePolicy.IsAutomatic(savedAutomatic: true, hasSavedScale: true));
        Assert.False(UiScalePolicy.IsAutomatic(savedAutomatic: false, hasSavedScale: false));
    }

    [Fact]
    public void Automatic_follows_the_display_and_manual_ignores_it()
    {
        Assert.Equal(2.0f, UiScalePolicy.Resolve(automatic: true, manualScale: 1.25f, osScale: 2.0f));
        Assert.Equal(1.25f, UiScalePolicy.Resolve(automatic: false, manualScale: 1.25f, osScale: 2.0f));
    }

    [Fact]
    public void The_resolved_scale_is_always_in_range()
    {
        Assert.Equal(UiScalePolicy.Max, UiScalePolicy.Resolve(true, 1f, 9f));
        Assert.Equal(UiScalePolicy.Min, UiScalePolicy.Resolve(false, 0.2f, 2f));
        Assert.Equal(1.0f, UiScalePolicy.Resolve(true, 1.5f, float.NaN));
    }
}
