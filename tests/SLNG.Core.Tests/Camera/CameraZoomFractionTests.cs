using SLNG.Core.Camera;
using Xunit;

namespace SLNG.Core.Tests.Camera;

public class CameraZoomFractionTests
{
    private const float Min = 0.5f;
    private const float Max = CameraZoom.MaxZoom;

    [Fact]
    public void The_closest_distance_is_the_top_of_the_slider_and_the_farthest_the_bottom()
    {
        Assert.Equal(1f, CameraZoomFraction.ToFraction(Min, Min, Max));
        Assert.Equal(0f, CameraZoomFraction.ToFraction(Max, Min, Max));
    }

    [Fact]
    public void The_scale_is_logarithmic_so_everyday_distances_use_the_track()
    {
        // The geometric middle of 0.5 and 200 m is 10 m; every doubling is the same stretch of track.
        Assert.Equal(0.5f, CameraZoomFraction.ToFraction(10f, Min, Max), 4);
        float at2 = CameraZoomFraction.ToFraction(2f, Min, Max);
        float at4 = CameraZoomFraction.ToFraction(4f, Min, Max);
        float at8 = CameraZoomFraction.ToFraction(8f, Min, Max);
        Assert.Equal(at2 - at4, at4 - at8, 4);
        // A usual third-person distance is well inside the track, not pinned at the top.
        Assert.InRange(at4, 0.6f, 0.7f);
    }

    [Fact]
    public void A_distance_outside_the_range_is_clamped()
    {
        Assert.Equal(1f, CameraZoomFraction.ToFraction(0.1f, Min, Max));
        Assert.Equal(1f, CameraZoomFraction.ToFraction(-5f, Min, Max));
        Assert.Equal(0f, CameraZoomFraction.ToFraction(500f, Min, Max));
    }

    [Fact]
    public void A_fraction_maps_back_to_the_ends_of_the_range()
    {
        Assert.Equal(Min, CameraZoomFraction.ToDistance(1f, Min, Max));
        Assert.Equal(Max, CameraZoomFraction.ToDistance(0f, Min, Max));
        Assert.Equal(10f, CameraZoomFraction.ToDistance(0.5f, Min, Max), 3);
    }

    [Fact]
    public void A_fraction_outside_zero_to_one_is_clamped()
    {
        Assert.Equal(Min, CameraZoomFraction.ToDistance(1.5f, Min, Max));
        Assert.Equal(Max, CameraZoomFraction.ToDistance(-0.5f, Min, Max));
    }

    [Theory]
    [InlineData(0.5f)]
    [InlineData(4f)]
    [InlineData(37.5f)]
    [InlineData(199f)]
    public void Fraction_and_distance_round_trip(float distance)
    {
        float back = CameraZoomFraction.ToDistance(CameraZoomFraction.ToFraction(distance, Min, Max), Min, Max);
        Assert.Equal(distance, back, 3);
    }

    [Theory]
    [InlineData(0.02f)] // an object focus can go this close
    [InlineData(0.15f)] // the ground
    public void The_minimum_follows_the_kind_of_focus(float min)
    {
        Assert.Equal(1f, CameraZoomFraction.ToFraction(min, min, Max));
        Assert.Equal(min, CameraZoomFraction.ToDistance(1f, min, Max));
        // The same distance sits lower on the slider the closer the floor can go.
        Assert.True(CameraZoomFraction.ToFraction(0.5f, min, Max) < 1f);
    }

    [Fact]
    public void A_range_with_no_width_reads_as_fully_in_instead_of_dividing_by_zero()
    {
        Assert.Equal(1f, CameraZoomFraction.ToFraction(3f, 3f, 3f));
        Assert.Equal(1f, CameraZoomFraction.ToFraction(3f, 5f, 3f));
    }
}
