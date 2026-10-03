using System.Numerics;
using SLNG.Core.Camera;
using Xunit;

namespace SLNG.Core.Tests.Camera;

public class CameraZoomTests
{
    [Fact]
    public void WheelStep_one_notch_in_is_two_to_minus_a_quarter()
        => Assert.Equal(4f * MathF.Pow(2f, -0.25f), CameraZoom.WheelStep(4f, -1), 4);

    [Fact]
    public void WheelStep_one_notch_out_is_two_to_a_quarter()
        => Assert.Equal(4f * MathF.Pow(2f, 0.25f), CameraZoom.WheelStep(4f, 1), 4);

    [Fact]
    public void WheelStep_four_notches_halve_or_double()
    {
        Assert.Equal(2f, CameraZoom.WheelStep(4f, -4), 4);
        Assert.Equal(8f, CameraZoom.WheelStep(4f, 4), 4);
    }

    [Fact]
    public void WheelStep_in_then_out_returns_to_start()
        => Assert.Equal(5f, CameraZoom.WheelStep(CameraZoom.WheelStep(5f, -3), 3), 4);

    [Fact]
    public void WheelStep_clamps_to_the_limits()
    {
        Assert.Equal(CameraZoom.MinZoom, CameraZoom.WheelStep(0.6f, -8));
        Assert.Equal(CameraZoom.MaxZoom, CameraZoom.WheelStep(190f, 8));
        Assert.Equal(1f, CameraZoom.WheelStep(0.2f, 0, 1f, 10f));
    }

    [Fact]
    public void WheelStep_zero_notches_keeps_the_value()
        => Assert.Equal(4f, CameraZoom.WheelStep(4f, 0));

    [Fact]
    public void EaseToward_halves_the_remaining_distance_in_one_half_life()
        => Assert.Equal(5f, CameraZoom.EaseToward(0f, 10f, 0.07f, 0.07f), 4);

    [Fact]
    public void EaseToward_is_frame_rate_independent()
    {
        // 1 s of simulated time at 10, 60 and 240 fps must land on the same value.
        float Run(int frames)
        {
            float v = 0f;
            for (int i = 0; i < frames; i++) v = CameraZoom.EaseToward(v, 10f, 1f / frames);
            return v;
        }

        float a = Run(10), b = Run(60), c = Run(240);
        Assert.Equal(a, b, 3);
        Assert.Equal(b, c, 3);
    }

    [Fact]
    public void EaseToward_snaps_when_within_a_millimetre()
    {
        Assert.Equal(4f, CameraZoom.EaseToward(4.0005f, 4f, 0.001f));
        Assert.Equal(4f, CameraZoom.EaseToward(3.9995f, 4f, 0.001f));
    }

    [Fact]
    public void EaseToward_reaches_the_target_eventually_and_never_overshoots()
    {
        float v = 1f;
        for (int i = 0; i < 600; i++)
        {
            v = CameraZoom.EaseToward(v, 3f, 1f / 60f);
            Assert.InRange(v, 1f, 3f);
        }
        Assert.Equal(3f, v);
    }

    [Fact]
    public void EaseToward_with_no_time_does_not_move()
        => Assert.Equal(1f, CameraZoom.EaseToward(1f, 3f, 0f));

    [Fact]
    public void CursorPanShift_is_zero_when_the_zoom_does_not_change()
        => Assert.Equal(Vector2.Zero, CameraZoom.CursorPanShift(0.7f, -0.3f, 60f, 4f, 4f, 16f / 9f));

    [Fact]
    public void CursorPanShift_is_zero_at_the_screen_centre()
        => Assert.Equal(Vector2.Zero, CameraZoom.CursorPanShift(0f, 0f, 60f, 4f, 2f, 16f / 9f));

    [Fact]
    public void CursorPanShift_matches_the_frustum_math()
    {
        // Zoom 4 -> 2 halves the distance: the shift is half the cursor's offset in the pivot plane.
        float halfHeight = 4f * MathF.Tan(MathF.PI / 6f); // fov 60 deg
        var s = CameraZoom.CursorPanShift(1f, 1f, 60f, 4f, 2f, 2f);
        Assert.Equal(halfHeight * 2f * 0.5f, s.X, 4);
        Assert.Equal(halfHeight * 0.5f, s.Y, 4);
    }

    [Fact]
    public void CursorPanShift_flips_sign_when_zooming_out()
    {
        var inShift = CameraZoom.CursorPanShift(0.5f, 0.5f, 60f, 4f, 2f);
        var outShift = CameraZoom.CursorPanShift(0.5f, 0.5f, 60f, 4f, 8f);
        Assert.True(inShift.X > 0 && outShift.X < 0);
        Assert.True(inShift.Y > 0 && outShift.Y < 0);
    }

    [Fact]
    public void CursorPanShift_with_no_old_distance_is_zero()
        => Assert.Equal(Vector2.Zero, CameraZoom.CursorPanShift(1f, 1f, 60f, 0f, 2f));
}
