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
    // ---- Rotation (FEAT-UI-39 phase 5) -------------------------------------------------------

    private const float HalfPi = MathF.PI / 2f;

    private static RadarProjection MakeUp(float upHeading, float w = 400, float h = 300, float fx = 128, float fy = 128, float range = 60)
        => new(new Vector2(w, h), new Vector2(fx, fy), range, upHeading);

    [Fact]
    public void North_up_is_the_default_and_matches_an_explicit_half_pi()
    {
        var implicitNorth = Make(fx: 100, fy: 150, range: 48);
        var explicitNorth = MakeUp(HalfPi, fx: 100, fy: 150, range: 48);

        Assert.Equal(HalfPi, implicitNorth.UpHeadingRadians, 6);
        foreach (var p in new[] { new Vector2(10, 20), new Vector2(200, 240), new Vector2(128, 128) })
        {
            var a = implicitNorth.ToCanvas(p);
            var b = explicitNorth.ToCanvas(p);
            Assert.Equal(b.X, a.X, 4);
            Assert.Equal(b.Y, a.Y, 4);
            // The exact old mapping, so a north-up radar is unchanged by the rotation work.
            Assert.Equal(200f + (p.X - 100f) * implicitNorth.PixelsPerMetre, a.X, 3);
            Assert.Equal(150f - (p.Y - 150f) * implicitNorth.PixelsPerMetre, a.Y, 3);
        }
    }

    [Fact]
    public void North_up_needs_no_canvas_rotation()
    {
        Assert.Equal(0f, Make().CanvasRotationRadians, 6);
        Assert.Equal(0f, MakeUp(HalfPi).CanvasRotationRadians, 6);
    }

    [Fact]
    public void East_up_puts_east_at_the_top_and_north_on_the_left()
    {
        var p = MakeUp(0f); // heading 0 = east
        var centre = p.ToCanvas(new Vector2(128, 128));

        var east = p.ToCanvas(new Vector2(138, 128));
        var north = p.ToCanvas(new Vector2(128, 138));

        Assert.Equal(centre.X, east.X, 3);
        Assert.True(east.Y < centre.Y, "east is the top of the canvas");
        Assert.Equal(centre.Y, north.Y, 3);
        Assert.True(north.X < centre.X, "with east at the top, north is on the LEFT");
    }

    [Fact]
    public void South_up_flips_the_map()
    {
        var p = MakeUp(-HalfPi);
        var centre = p.ToCanvas(new Vector2(128, 128));

        var south = p.ToCanvas(new Vector2(128, 118));
        var north = p.ToCanvas(new Vector2(128, 138));
        var east = p.ToCanvas(new Vector2(138, 128));

        Assert.True(south.Y < centre.Y);
        Assert.True(north.Y > centre.Y, "north is at the bottom");
        Assert.True(east.X < centre.X, "east is on the left when south is up");
    }

    [Fact]
    public void West_up_puts_north_on_the_right()
    {
        var p = MakeUp(MathF.PI);
        var centre = p.ToCanvas(new Vector2(128, 128));

        var west = p.ToCanvas(new Vector2(118, 128));
        var north = p.ToCanvas(new Vector2(128, 138));

        Assert.True(west.Y < centre.Y);
        Assert.True(north.X > centre.X);
        Assert.Equal(centre.Y, north.Y, 3);
    }

    [Fact]
    public void The_focus_stays_at_the_canvas_centre_whatever_is_up()
    {
        foreach (var h in new[] { 0f, 0.7f, HalfPi, 2.5f, -2f })
        {
            var c = MakeUp(h, fx: 90, fy: 30).ToCanvas(new Vector2(90, 30));
            Assert.Equal(200f, c.X, 3);
            Assert.Equal(150f, c.Y, 3);
        }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.6f)]
    [InlineData(1.5707964f)]
    [InlineData(2.4f)]
    [InlineData(3.14159f)]
    [InlineData(-0.9f)]
    [InlineData(-2.8f)]
    public void ToRegion_inverts_ToCanvas_for_any_heading(float heading)
    {
        var p = MakeUp(heading, fx: 100, fy: 150, range: 48);

        foreach (var pt in new[] { new Vector2(10, 20), new Vector2(200, 240), new Vector2(128, 128), new Vector2(0, 0), new Vector2(255, 3) })
        {
            var back = p.ToRegion(p.ToCanvas(pt));
            Assert.Equal(pt.X, back.X, 2);
            Assert.Equal(pt.Y, back.Y, 2);
        }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1f)]
    [InlineData(-2f)]
    public void Rotation_never_changes_the_scale(float heading)
    {
        var p = MakeUp(heading, range: 60);

        Assert.Equal(5f, p.PixelsPerMetre, 3); // 300 / 60

        var a = p.ToCanvas(new Vector2(128, 128));
        var b = p.ToCanvas(new Vector2(128 + 6, 128 + 8)); // 10 m apart
        Assert.Equal(10f * 5f, (b - a).Length(), 2);
    }

    // CanvasRotationRadians is what Godot's DrawSetTransform wants: a NORTH-UP drawing about the
    // canvas centre, turned by it, must land exactly where ToCanvas puts the point. Godot rotates a
    // canvas point (y down) by theta as x' = x cos - y sin, y' = x sin + y cos.
    [Theory]
    [InlineData(0f)]
    [InlineData(0.6f)]
    [InlineData(1.5707964f)]
    [InlineData(2.4f)]
    [InlineData(3.14159f)]
    [InlineData(-0.9f)]
    [InlineData(-2.8f)]
    public void Rotating_a_north_up_drawing_by_CanvasRotationRadians_matches_ToCanvas(float heading)
    {
        var rotated = MakeUp(heading, fx: 100, fy: 150, range: 48);
        var northUp = Make(fx: 100, fy: 150, range: 48);
        var centre = new Vector2(200, 150);
        float theta = rotated.CanvasRotationRadians;
        float cos = MathF.Cos(theta), sin = MathF.Sin(theta);

        foreach (var pt in new[] { new Vector2(10, 20), new Vector2(200, 240), new Vector2(128, 128), new Vector2(0, 0) })
        {
            var o = northUp.ToCanvas(pt) - centre;
            var turned = centre + new Vector2(o.X * cos - o.Y * sin, o.X * sin + o.Y * cos);
            var expected = rotated.ToCanvas(pt);
            Assert.Equal(expected.X, turned.X, 2);
            Assert.Equal(expected.Y, turned.Y, 2);
        }
    }

    [Fact]
    public void The_canvas_rotation_is_the_up_heading_less_a_quarter_turn()
    {
        Assert.Equal(-HalfPi, MakeUp(0f).CanvasRotationRadians, 5);       // east up
        Assert.Equal(HalfPi, MakeUp(MathF.PI).CanvasRotationRadians, 5);  // west up
        Assert.Equal(-MathF.PI, MakeUp(-HalfPi).CanvasRotationRadians, 5); // south up
    }

    [Fact]
    public void A_rotated_zero_sized_canvas_does_not_produce_NaN()
    {
        var p = MakeUp(1.1f, w: 0, h: 0);

        var region = p.ToRegion(new Vector2(5, 5));

        Assert.False(float.IsNaN(region.X) || float.IsNaN(region.Y));
        Assert.False(float.IsInfinity(region.X) || float.IsInfinity(region.Y));
    }
}
