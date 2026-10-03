using System.Numerics;
using SLNG.Core.Camera;
using Xunit;

namespace SLNG.Core.Tests.Camera;

public class RayCapsuleTests
{
    // A vertical capsule: axis from (0,0,0) to (0,2,0), radius 0.5.
    private static readonly Vector3 A = Vector3.Zero;
    private static readonly Vector3 B = new(0, 2, 0);
    private const float R = 0.5f;

    [Fact]
    public void Hits_the_cylinder_side()
    {
        bool hit = RayCapsule.Intersect(new Vector3(5, 1, 0), new Vector3(-1, 0, 0), A, B, R, out float t);
        Assert.True(hit);
        Assert.Equal(4.5f, t, 4);
    }

    [Fact]
    public void Hits_the_top_cap()
    {
        bool hit = RayCapsule.Intersect(new Vector3(0, 5, 0), new Vector3(0, -1, 0), A, B, R, out float t);
        Assert.True(hit);
        Assert.Equal(2.5f, t, 4); // the cap apex is at y = 2.5
    }

    [Fact]
    public void Hits_the_bottom_cap()
    {
        bool hit = RayCapsule.Intersect(new Vector3(0, -5, 0), new Vector3(0, 1, 0), A, B, R, out float t);
        Assert.True(hit);
        Assert.Equal(4.5f, t, 4); // apex at y = -0.5
    }

    [Fact]
    public void Hits_a_cap_off_axis()
    {
        // Aimed at the lower sphere's centre height from the side, below the cylinder range.
        bool hit = RayCapsule.Intersect(new Vector3(5, -0.3f, 0), new Vector3(-1, 0, 0), A, B, R, out float t);
        Assert.True(hit);
        float expectedX = MathF.Sqrt(R * R - 0.3f * 0.3f);
        Assert.Equal(5f - expectedX, t, 4);
    }

    [Fact]
    public void Misses_beside_the_capsule()
    {
        Assert.False(RayCapsule.Intersect(new Vector3(5, 1, 0.6f), new Vector3(-1, 0, 0), A, B, R, out _));
    }

    [Fact]
    public void Misses_above_the_cap()
    {
        Assert.False(RayCapsule.Intersect(new Vector3(5, 2.6f, 0), new Vector3(-1, 0, 0), A, B, R, out _));
    }

    [Fact]
    public void Ray_starting_inside_reports_where_it_leaves()
    {
        bool hit = RayCapsule.Intersect(new Vector3(0, 1, 0), new Vector3(1, 0, 0), A, B, R, out float t);
        Assert.True(hit);
        Assert.Equal(0.5f, t, 4);
    }

    [Fact]
    public void Capsule_behind_the_origin_is_a_miss()
    {
        Assert.False(RayCapsule.Intersect(new Vector3(5, 1, 0), new Vector3(1, 0, 0), A, B, R, out _));
    }

    [Fact]
    public void Direction_need_not_be_unit_length()
    {
        bool hit = RayCapsule.Intersect(new Vector3(5, 1, 0), new Vector3(-10, 0, 0), A, B, R, out float t);
        Assert.True(hit);
        Assert.Equal(4.5f, t, 4);
    }

    [Fact]
    public void Degenerate_segment_is_a_sphere()
    {
        var c = new Vector3(0, 1, 0);
        bool hit = RayCapsule.Intersect(new Vector3(3, 1, 0), new Vector3(-1, 0, 0), c, c, 0.25f, out float t);
        Assert.True(hit);
        Assert.Equal(2.75f, t, 4);
        Assert.False(RayCapsule.Intersect(new Vector3(3, 1.3f, 0), new Vector3(-1, 0, 0), c, c, 0.25f, out _));
    }

    [Fact]
    public void Ray_along_the_axis_hits_the_end_cap()
    {
        bool hit = RayCapsule.Intersect(new Vector3(0, 10, 0), new Vector3(0, -1, 0), A, B, R, out float t);
        Assert.True(hit);
        Assert.Equal(7.5f, t, 4);
    }

    [Fact]
    public void Nearest_of_two_surfaces_is_reported()
    {
        // Through the full width: entry at x = 5 - 0.5, never the exit at x = 5 + 0.5.
        RayCapsule.Intersect(new Vector3(-5, 1, 0), new Vector3(1, 0, 0), A, B, R, out float t);
        Assert.Equal(4.5f, t, 4);
    }

    [Fact]
    public void Tilted_capsule_is_hit_on_its_body()
    {
        var a = new Vector3(0, 0, 0);
        var b = new Vector3(2, 0, 0); // horizontal
        bool hit = RayCapsule.Intersect(new Vector3(1, 5, 0), new Vector3(0, -1, 0), a, b, 0.3f, out float t);
        Assert.True(hit);
        Assert.Equal(4.7f, t, 4);
    }

    [Fact]
    public void Zero_direction_or_radius_is_a_miss()
    {
        Assert.False(RayCapsule.Intersect(Vector3.Zero, Vector3.Zero, A, B, R, out _));
        Assert.False(RayCapsule.Intersect(new Vector3(5, 1, 0), new Vector3(-1, 0, 0), A, B, 0f, out _));
    }
}
