using System;
using System.Numerics;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

// The pure rules behind the radar's map overlays (FEAT-UI-39 phase 5): height markers, zoom
// presets, chat rings, the persisted view settings, the camera wedge and the pan.
public class RadarMapOverlayTests
{
    // ---- Height markers (llworldmapview.cpp: +-7 m) ------------------------------------------

    [Theory]
    [InlineData(0f, HeightMarker.Level)]
    [InlineData(6.99f, HeightMarker.Level)]
    [InlineData(7f, HeightMarker.Level)]      // exactly the threshold is still level
    [InlineData(-7f, HeightMarker.Level)]
    [InlineData(7.01f, HeightMarker.Above)]
    [InlineData(150f, HeightMarker.Above)]
    [InlineData(-7.01f, HeightMarker.Below)]
    [InlineData(-300f, HeightMarker.Below)]
    public void A_known_height_is_level_above_or_below_by_seven_metres(float relativeZ, HeightMarker expected)
    {
        Assert.Equal(expected, RadarHeight.MarkerFor(relativeZ, heightKnown: true));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(500f)]
    [InlineData(-500f)]
    public void An_unknown_height_is_unknown_whatever_the_number_says(float relativeZ)
    {
        Assert.Equal(HeightMarker.Unknown, RadarHeight.MarkerFor(relativeZ, heightKnown: false));
    }

    [Fact]
    public void The_threshold_is_seven_metres()
    {
        Assert.Equal(7f, RadarHeight.MarkerThresholdMetres);
    }

    // ---- Zoom ----------------------------------------------------------------------------------

    [Fact]
    public void The_zoom_presets_are_very_close_close_medium_far()
    {
        Assert.Equal(new[] { 32f, 64f, 128f, 256f }, RadarZoom.PresetsMetres);
        Assert.Equal(64f, RadarZoom.DefaultMetres);
        Assert.Equal(16f, RadarZoom.MinMetres);
        Assert.Equal(512f, RadarZoom.MaxMetres);
    }

    [Theory]
    [InlineData(1f, 16f)]
    [InlineData(16f, 16f)]
    [InlineData(100f, 100f)]
    [InlineData(512f, 512f)]
    [InlineData(9000f, 512f)]
    public void Clamp_keeps_the_range_between_min_and_max(float input, float expected)
    {
        Assert.Equal(expected, RadarZoom.Clamp(input));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Clamp_answers_the_default_for_a_value_that_is_no_number(float input)
    {
        // An infinity is not a zoom either: a hand-edited preferences file must not wedge the map.
        Assert.Equal(RadarZoom.DefaultMetres, RadarZoom.Clamp(input));
    }

    [Theory]
    [InlineData(16f, 0)]
    [InlineData(32f, 0)]
    [InlineData(64f, 1)]
    [InlineData(80f, 1)]    // wheel steps land between presets
    [InlineData(100f, 2)]
    [InlineData(128f, 2)]
    [InlineData(256f, 3)]
    [InlineData(512f, 3)]
    [InlineData(0f, 1)]     // no usable value: the default's preset
    [InlineData(float.NaN, 1)]
    public void NearestPresetIndex_picks_the_closest_preset_on_a_ratio_scale(float metres, int expected)
    {
        Assert.Equal(expected, RadarZoom.NearestPresetIndex(metres));
    }

    // ---- Chat rings ------------------------------------------------------------------------------

    [Fact]
    public void The_chat_ring_radii_are_whisper_say_shout()
    {
        Assert.Equal(10f, RadarChatRings.WhisperMetres);
        Assert.Equal(20f, RadarChatRings.SayMetres);
        Assert.Equal(100f, RadarChatRings.ShoutMetres);
    }

    // ---- View settings ----------------------------------------------------------------------------

    [Fact]
    public void View_settings_default_to_all_rings_north_up_auto_center_and_the_default_zoom()
    {
        var s = new RadarViewSettings();

        Assert.True(s.ChatRings);
        Assert.True(s.WhisperRing);
        Assert.True(s.SayRing);
        Assert.True(s.ShoutRing);
        Assert.False(s.CameraUp);
        Assert.True(s.AutoCenter);
        Assert.Equal(RadarZoom.DefaultMetres, s.VisibleRangeMetres);
    }

    [Theory]
    [InlineData(2f, 16f)]
    [InlineData(48f, 48f)]
    [InlineData(5000f, 512f)]
    [InlineData(float.NaN, 64f)]
    public void The_visible_range_setter_clamps(float input, float expected)
    {
        var s = new RadarViewSettings { VisibleRangeMetres = input };

        Assert.Equal(expected, s.VisibleRangeMetres);
    }

    [Fact]
    public void Each_toggle_holds_its_own_value()
    {
        var s = new RadarViewSettings { ChatRings = false, SayRing = false, CameraUp = true, AutoCenter = false };

        Assert.False(s.ChatRings);
        Assert.True(s.WhisperRing);
        Assert.False(s.SayRing);
        Assert.True(s.ShoutRing);
        Assert.True(s.CameraUp);
        Assert.False(s.AutoCenter);
    }

    // ---- Camera wedge -------------------------------------------------------------------------------

    [Fact]
    public void A_square_viewport_has_the_same_horizontal_and_vertical_fov()
    {
        Assert.Equal(1.0f, RadarCamera.HorizontalFov(1.0f, 1f), 4);
    }

    [Fact]
    public void A_wide_viewport_widens_the_horizontal_fov_by_the_aspect_ratio_of_the_tangents()
    {
        float vertical = 60f * MathF.PI / 180f;

        float h = RadarCamera.HorizontalFov(vertical, 16f / 9f);

        // 2 * atan(tan(30 deg) * 16/9) = 91.5 deg
        Assert.Equal(91.5f * MathF.PI / 180f, h, 2);
        Assert.True(h > vertical);
    }

    [Fact]
    public void A_nonsense_aspect_ratio_still_gives_a_sane_fov()
    {
        float vertical = 1.0f;

        Assert.Equal(vertical, RadarCamera.HorizontalFov(vertical, 0f), 4);
        Assert.Equal(vertical, RadarCamera.HorizontalFov(vertical, float.NaN), 4);
        Assert.InRange(RadarCamera.HorizontalFov(vertical, 1000f), 0f, MathF.PI);
    }

    [Theory]
    [InlineData(1f, 0f, 0f)]                 // east
    [InlineData(0f, 1f, MathF.PI / 2f)]      // north
    [InlineData(-1f, 0f, MathF.PI)]          // west
    [InlineData(0f, -1f, -MathF.PI / 2f)]    // south
    public void TryHeading_is_counter_clockwise_from_east(float x, float y, float expected)
    {
        Assert.True(RadarCamera.TryHeading(new Vector2(x, y), out float heading));
        Assert.Equal(expected, heading, 4);
    }

    [Fact]
    public void A_camera_looking_straight_up_or_down_has_no_heading()
    {
        Assert.False(RadarCamera.TryHeading(Vector2.Zero, out _));
        Assert.False(RadarCamera.TryHeading(new Vector2(1e-6f, -1e-6f), out _));
    }

    // ---- Pan --------------------------------------------------------------------------------------------

    [Fact]
    public void A_pan_is_clamped_to_about_one_region_either_way()
    {
        var clamped = RadarPan.Clamp(new Vector2(900f, -900f));

        Assert.Equal(RadarPan.MaxMetres, clamped.X);
        Assert.Equal(-RadarPan.MaxMetres, clamped.Y);
        Assert.Equal(new Vector2(10f, -20f), RadarPan.Clamp(new Vector2(10f, -20f)));
    }

    [Fact]
    public void Easing_back_moves_towards_zero_without_overshooting()
    {
        var pan = new Vector2(100f, -60f);

        var next = RadarPan.EaseToZero(pan, 1f / 60f);

        Assert.True(next.X is > 0f and < 100f);
        Assert.True(next.Y is < 0f and > -60f);
        // Same direction, only shorter.
        Assert.Equal(pan.X / pan.Y, next.X / next.Y, 4);
    }

    [Fact]
    public void Easing_does_not_depend_on_the_frame_rate()
    {
        var start = new Vector2(120f, 80f);

        var oneStep = RadarPan.EaseToZero(start, 0.1f);
        var many = start;
        for (int i = 0; i < 10; i++) many = RadarPan.EaseToZero(many, 0.01f);

        Assert.Equal(oneStep.X, many.X, 2);
        Assert.Equal(oneStep.Y, many.Y, 2);
    }

    [Fact]
    public void Easing_settles_on_exactly_zero_within_a_second()
    {
        var pan = new Vector2(256f, 256f);
        for (int i = 0; i < 60; i++) pan = RadarPan.EaseToZero(pan, 1f / 60f);

        Assert.Equal(Vector2.Zero, pan);
    }

    [Fact]
    public void A_zero_or_negative_frame_time_changes_nothing()
    {
        var pan = new Vector2(10f, 5f);

        Assert.Equal(pan, RadarPan.EaseToZero(pan, 0f));
        Assert.Equal(pan, RadarPan.EaseToZero(pan, -1f));
    }

    // ---- Tooltip distance ------------------------------------------------------------------------------

    [Fact]
    public void The_range_text_of_a_known_height_is_the_distance_to_two_decimals()
    {
        Assert.Equal("39.78", RadarTable.FormatRange(39.7831f, heightKnown: true, farClipMetres: 96f));
    }

    [Fact]
    public void The_range_text_of_an_unknown_height_is_a_lower_bound_of_at_least_the_far_clip()
    {
        Assert.Equal(">96.00", RadarTable.FormatRange(12f, heightKnown: false, farClipMetres: 96f));
        Assert.Equal(">150.50", RadarTable.FormatRange(150.5f, heightKnown: false, farClipMetres: 96f));
    }
}
