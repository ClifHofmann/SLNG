using System.Numerics;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

public class RadarProjectionTests
{
    private static RadarProjection Make(float w = 400, float h = 300, float fx = 128, float fy = 128, float range = 64)
        => new(new Vector2(w, h), new Vector2(fx, fy), range);

    [Fact]
    public void Focus_maps_to_canvas_centre()
    {
        var p = Make(w: 400, h: 300, fx: 90, fy: 30);

        var c = p.ToCanvas(new Vector2(90, 30));

        Assert.Equal(200f, c.X, 3);
        Assert.Equal(150f, c.Y, 3);
    }

    [Fact]
    public void North_is_up_east_is_right()
    {
        var p = Make();
        var centre = p.ToCanvas(new Vector2(128, 128));

        var north = p.ToCanvas(new Vector2(128, 138));
        var east = p.ToCanvas(new Vector2(138, 128));

        Assert.True(north.Y < centre.Y, "region +Y (north) must move UP the canvas, whose Y grows downward");
        Assert.Equal(centre.X, north.X, 3);
        Assert.True(east.X > centre.X);
        Assert.Equal(centre.Y, east.Y, 3);
    }

    [Fact]
    public void Scale_is_the_shorter_side_over_the_visible_range()
    {
        var p = Make(w: 400, h: 300, range: 60);

        Assert.Equal(5f, p.PixelsPerMetre, 3); // 300 / 60
    }

    [Theory]
    [InlineData(10f, 20f)]
    [InlineData(200f, 240f)]
    [InlineData(128f, 128f)]
    [InlineData(0f, 0f)]
    public void ToRegion_inverts_ToCanvas(float x, float y)
    {
        var p = Make(fx: 100, fy: 150, range: 48);

        var back = p.ToRegion(p.ToCanvas(new Vector2(x, y)));

        Assert.Equal(x, back.X, 2);
        Assert.Equal(y, back.Y, 2);
    }

    [Fact]
    public void A_click_at_the_canvas_centre_lands_on_the_focus()
    {
        var p = Make(w: 400, h: 300, fx: 77, fy: 211);

        var region = p.ToRegion(new Vector2(200, 150));

        Assert.Equal(77f, region.X, 3);
        Assert.Equal(211f, region.Y, 3);
    }

    [Fact]
    public void A_zero_sized_canvas_does_not_produce_NaN()
    {
        var p = Make(w: 0, h: 0);

        var region = p.ToRegion(new Vector2(5, 5));

        Assert.False(float.IsNaN(region.X) || float.IsNaN(region.Y));
        Assert.False(float.IsInfinity(region.X) || float.IsInfinity(region.Y));
    }

    [Fact]
    public void A_range_below_one_metre_is_clamped()
    {
        var p = Make(w: 100, h: 100, range: 0f);

        Assert.Equal(100f, p.PixelsPerMetre, 3); // 100 px / 1 m
    }

    [Theory]
    [InlineData(0f, 0f, true)]
    [InlineData(255.9f, 255.9f, true)]
    [InlineData(256f, 10f, false)]
    [InlineData(10f, 256f, false)]
    [InlineData(-0.1f, 10f, false)]
    [InlineData(10f, -0.1f, false)]
    public void Within_is_a_half_open_rectangle(float x, float y, bool expected)
    {
        Assert.Equal(expected, RadarProjection.Within(new Vector2(x, y), 256, 256));
    }
}
