using SLNG.Core.Camera;
using Xunit;

namespace SLNG.Core.Tests.Camera;

public class CameraFovLimitsTests
{
    private const float Wide16x9 = 16f / 9f;
    private const float Ultrawide21x9 = 21f / 9f;
    private const float Narrow9x16 = 9f / 16f;

    [Fact]
    public void Square_view_uses_the_absolute_limits()
    {
        Assert.Equal(5f, CameraFovLimits.MinDegrees(1f));
        Assert.Equal(175f, CameraFovLimits.MaxDegrees(1f));
    }

    [Fact]
    public void Wide_view_lowers_the_maximum_by_the_aspect_and_keeps_the_minimum()
    {
        // LLCamera::getMaxView: MAX_FIELD_OF_VIEW / aspect in a wide view (llcamera.cpp:88-94).
        Assert.Equal(5f, CameraFovLimits.MinDegrees(Wide16x9));
        Assert.Equal(175f / Wide16x9, CameraFovLimits.MaxDegrees(Wide16x9), 3);
        Assert.Equal(98.4375f, CameraFovLimits.MaxDegrees(Wide16x9), 3);
        Assert.Equal(75f, CameraFovLimits.MaxDegrees(Ultrawide21x9), 3);
    }

    [Fact]
    public void Narrow_view_raises_the_minimum_by_the_aspect_and_keeps_the_maximum()
    {
        // LLCamera::getMinView: MIN_FIELD_OF_VIEW / aspect in a narrow view (llcamera.cpp:80-86).
        Assert.Equal(5f / Narrow9x16, CameraFovLimits.MinDegrees(Narrow9x16), 3);
        Assert.Equal(8.889f, CameraFovLimits.MinDegrees(Narrow9x16), 3);
        Assert.Equal(175f, CameraFovLimits.MaxDegrees(Narrow9x16));
    }

    [Fact]
    public void Clamp_pulls_a_value_into_the_range_for_the_window_shape()
    {
        Assert.Equal(5f, CameraFovLimits.Clamp(1f, Wide16x9));
        Assert.Equal(60f, CameraFovLimits.Clamp(60f, Wide16x9));
        Assert.Equal(98.4375f, CameraFovLimits.Clamp(120f, Wide16x9), 3);

        // The same 120 degrees is fine in a narrow window and too much in an ultrawide one.
        Assert.Equal(120f, CameraFovLimits.Clamp(120f, Narrow9x16));
        Assert.Equal(75f, CameraFovLimits.Clamp(120f, Ultrawide21x9), 3);
        Assert.Equal(175f, CameraFovLimits.Clamp(400f, Narrow9x16));
    }

    [Fact]
    public void The_range_slng_shipped_before_is_inside_the_new_one()
    {
        // 50-100 degrees was the whole range before FEAT-UI-73; a stored value in it must survive at 4:3.
        for (float v = 50f; v <= 100f; v += 5f)
            Assert.Equal(v, CameraFovLimits.Clamp(v, 4f / 3f), 3);
    }

    [Fact]
    public void The_range_is_never_inverted_and_always_inside_the_absolute_bounds()
    {
        foreach (float aspect in new[] { 0.02f, 0.1f, 0.5f, 1f, 1.5f, 2.4f, 10f, 35f, 50f, 1000f, 0f, -3f })
        {
            float lo = CameraFovLimits.MinDegrees(aspect);
            float hi = CameraFovLimits.MaxDegrees(aspect);
            Assert.True(lo <= hi, $"aspect {aspect}: {lo} > {hi}");
            Assert.InRange(lo, CameraFovLimits.AbsoluteMinDegrees, CameraFovLimits.AbsoluteMaxDegrees);
            Assert.InRange(hi, CameraFovLimits.AbsoluteMinDegrees, CameraFovLimits.AbsoluteMaxDegrees);
        }
    }

    [Fact]
    public void An_absurdly_wide_view_collapses_to_the_minimum_like_the_viewer()
    {
        // Above aspect 35 the wide-view maximum (175 / aspect) drops under 5 degrees; the viewer's clamp order
        // ends at the absolute minimum, and so does this.
        Assert.Equal(5f, CameraFovLimits.Clamp(60f, 50f));
        Assert.Equal(5f, CameraFovLimits.MaxDegrees(50f));
    }

    [Fact]
    public void Unusable_inputs_fall_back_instead_of_propagating()
    {
        Assert.Equal(60f, CameraFovLimits.Clamp(float.NaN, Wide16x9));
        Assert.Equal(60f, CameraFovLimits.Clamp(float.PositiveInfinity, Wide16x9));
        Assert.Equal(CameraFovLimits.MaxDegrees(CameraFovLimits.DefaultAspect), CameraFovLimits.MaxDegrees(float.NaN));
    }

    [Fact]
    public void Zoom_in_steps_by_one_point_two_and_stops_at_five_degrees()
    {
        // 60 / 1.2^n: 50, 41.7, 34.7, 28.9, 24.1, 20.1, 16.7, 14.0, 11.6, 9.7, 8.1, 6.7, 5.6, then the floor.
        float fov = CameraFovLimits.ZoomIn(CameraFovLimits.DefaultDegrees, Wide16x9);
        Assert.Equal(50f, fov, 3);

        for (int step = 2; step <= 13; step++) fov = CameraFovLimits.ZoomIn(fov, Wide16x9);
        Assert.Equal(60f / MathF.Pow(1.2f, 13f), fov, 2);
        Assert.True(fov > 5f, $"13 steps should still be above the floor, got {fov}");

        fov = CameraFovLimits.ZoomIn(fov, Wide16x9);
        Assert.Equal(5f, fov);

        // And it stays there.
        Assert.Equal(5f, CameraFovLimits.ZoomIn(fov, Wide16x9));
    }

    [Fact]
    public void Zoom_out_steps_by_one_point_two_and_stops_at_the_maximum_for_the_window()
    {
        float first = CameraFovLimits.ZoomOut(CameraFovLimits.DefaultDegrees, Wide16x9);
        float second = CameraFovLimits.ZoomOut(first, Wide16x9);
        Assert.Equal(72f, first, 3);
        Assert.Equal(86.4f, second, 3);
        Assert.Equal(98.4375f, CameraFovLimits.ZoomOut(second, Wide16x9), 3);

        // An ultrawide window stops sooner, a narrow one much later.
        Assert.Equal(75f, CameraFovLimits.ZoomOut(72f, Ultrawide21x9), 3);
        Assert.Equal(175f, CameraFovLimits.ZoomOut(160f, Narrow9x16));
    }

    [Fact]
    public void Zoom_in_then_out_returns_to_the_start_while_unclamped()
    {
        float there = CameraFovLimits.ZoomIn(CameraFovLimits.ZoomIn(60f, Wide16x9), Wide16x9);
        Assert.Equal(60f, CameraFovLimits.ZoomOut(CameraFovLimits.ZoomOut(there, Wide16x9), Wide16x9), 3);
    }

    [Fact]
    public void Zoom_steps_start_from_the_value_the_window_allows()
    {
        // A stored 120 degrees is being shown as 98.4 at 16:9, so zooming in goes from what is on screen.
        Assert.Equal(98.4375f / 1.2f, CameraFovLimits.ZoomIn(120f, Wide16x9), 3);
    }

    [Fact]
    public void Default_is_sixty_degrees_where_the_window_allows_it()
    {
        Assert.Equal(60f, CameraFovLimits.Default(Wide16x9));
        Assert.Equal(60f, CameraFovLimits.Default(Narrow9x16));
        Assert.Equal(60f, CameraFovLimits.Default(Ultrawide21x9), 3);
        Assert.Equal(CameraFovLimits.MaxDegrees(4f), CameraFovLimits.Default(4f), 3); // 175 / 4 = 43.75: below 60
    }
}
