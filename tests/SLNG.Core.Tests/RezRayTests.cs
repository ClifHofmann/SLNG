using System.Numerics;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>FEAT-INV-15: the ray of a RezObject packet, per LLToolDragAndDrop::dropObject
/// (lltooldraganddrop.cpp:1846-1997).</summary>
public class RezRayTests
{
    private static readonly Vector3 Camera = new(10f, 20f, 30f);
    private static readonly Vector3 Hit = new(14f, 20f, 26f);

    [Fact]
    public void Dropping_on_land_bypasses_the_raycast_and_ends_exactly_on_the_hit()
    {
        var ray = RezRay.For(Camera, Hit, Guid.Empty);

        Assert.True(ray.BypassRaycast);
        Assert.Equal(Camera, ray.Start);
        Assert.Equal(Hit, ray.End); // not doubled, not lifted off the surface
        Assert.Equal(Guid.Empty, ray.TargetId);
    }

    [Fact]
    public void Dropping_on_an_object_leaves_the_raycast_to_the_simulator_and_doubles_the_ray()
    {
        var target = Guid.NewGuid();

        var ray = RezRay.For(Camera, Hit, target);

        Assert.False(ray.BypassRaycast);
        Assert.Equal(Camera, ray.Start);
        // hit + (hit - camera): "the ray's end point is an approximation, and is sometimes too short"
        Assert.Equal(new Vector3(18f, 20f, 22f), ray.End);
        Assert.Equal(target, ray.TargetId);
    }

    [Fact]
    public void A_ray_with_the_camera_on_the_hit_does_not_move_the_end()
    {
        var ray = RezRay.For(Hit, Hit, Guid.NewGuid());
        Assert.Equal(Hit, ray.End);
    }

    [Theory]
    [InlineData(0f, 0f, true)]
    [InlineData(255.9f, 255.9f, true)]
    [InlineData(128f, 128f, true)]
    [InlineData(-0.01f, 10f, false)]
    [InlineData(10f, -0.01f, false)]
    [InlineData(256f, 10f, false)]
    [InlineData(10f, 256f, false)]
    [InlineData(float.NaN, 10f, false)]
    public void A_point_is_in_a_256_region_only_when_both_axes_are_within_it(float x, float y, bool inside)
    {
        Assert.Equal(inside, RezRay.InsideRegion(new Vector3(x, y, 50f), 256f, 256f));
    }

    [Fact]
    public void Height_does_not_decide_whether_a_point_is_in_the_region()
    {
        Assert.True(RezRay.InsideRegion(new Vector3(5f, 5f, 5000f), 256f, 256f));
        Assert.True(RezRay.InsideRegion(new Vector3(5f, 5f, -20f), 256f, 256f));
    }

    [Fact]
    public void A_larger_region_takes_a_larger_range()
    {
        Assert.True(RezRay.InsideRegion(new Vector3(300f, 500f, 0f), 512f, 512f));
        Assert.False(RezRay.InsideRegion(new Vector3(300f, 500f, 0f), 256f, 512f));
    }
}
