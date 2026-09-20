using System.Numerics;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>FEAT-UI-04: the stretch handles sit on this box, so what it measures is what the user
/// grabs. Measured on the root prim alone — which is what SLNG did — a linked object's handles sit
/// on whichever part happens to be the root: on a chair, somewhere inside the seat cushion.</summary>
public class LinksetBoundsTests
{
    private static readonly Vector3 Origin = Vector3.Zero;

    [Fact]
    public void ASinglePrimMeasuresItself()
    {
        var (centre, half) = LinksetBounds.Measure(
            Origin, Quaternion.Identity, new Vector3(2f, 4f, 6f), parts: null);

        Assert.Equal(Vector3.Zero, centre);
        Assert.Equal(new Vector3(1f, 2f, 3f), half);
    }

    /// <summary>The case from the report: a root prim with the rest of the object hanging off to
    /// one side. The box has to cover both, which also moves its centre off the root.</summary>
    [Fact]
    public void ThePartsPullTheBoxOutAndMoveItsCentre()
    {
        var parts = new[]
        {
            new LinksetBounds.Part(new Vector3(2f, 0f, 0f), Quaternion.Identity, Vector3.One),
        };

        var (centre, half) = LinksetBounds.Measure(Origin, Quaternion.Identity, Vector3.One, parts);

        // root spans -0.5..0.5, the part 1.5..2.5
        Assert.Equal(new Vector3(1f, 0f, 0f), centre);
        Assert.Equal(new Vector3(1.5f, 0.5f, 0.5f), half);
    }

    /// <summary>A part turned 45 degrees reaches further along an axis than its own size, so the
    /// box has to be taken from its CORNERS. Measuring a part as a centre plus its half-size
    /// would cut through it.</summary>
    [Fact]
    public void ARotatedPartIsMeasuredByItsCorners()
    {
        var turned = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, System.MathF.PI / 4f);
        var parts = new[] { new LinksetBounds.Part(Origin, turned, Vector3.One) };

        var (centre, half) = LinksetBounds.Measure(Origin, Quaternion.Identity, new Vector3(0.1f, 0.1f, 0.1f), parts);

        float diagonal = System.MathF.Sqrt(0.5f); // half a unit square's diagonal
        Assert.Equal(0f, centre.Length(), 5);
        Assert.Equal(diagonal, half.X, 5);
        Assert.Equal(diagonal, half.Y, 5);
        Assert.Equal(0.5f, half.Z, 5);
    }

    /// <summary>Parts arrive in REGION coordinates, and the box is wanted in the root's frame --
    /// the frame the simulator resizes in. A rotated root must therefore not rotate its own box.
    /// </summary>
    [Fact]
    public void TheBoxIsMeasuredInTheRootsOwnFrame()
    {
        var rootRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, System.MathF.PI / 2f);
        var rootPosition = new Vector3(100f, 200f, 30f);

        // One metre "to the left" in the region is one metre along the root's own +X once the
        // root is turned 90 degrees about Z.
        var parts = new[]
        {
            new LinksetBounds.Part(rootPosition + new Vector3(0f, 1f, 0f), rootRotation, Vector3.One),
        };

        var (centre, half) = LinksetBounds.Measure(rootPosition, rootRotation, Vector3.One, parts);

        Assert.Equal(0.5f, centre.X, 5);
        Assert.Equal(0f, centre.Y, 5);
        Assert.Equal(new Vector3(1f, 0.5f, 0.5f).X, half.X, 5);
        Assert.Equal(0.5f, half.Y, 5);
    }

    /// <summary>A part that reaches back past the root counts too — the box is the union, not the
    /// span of the parts that happen to stick out forwards.</summary>
    [Fact]
    public void PartsOnBothSidesKeepTheCentreWhereItBelongs()
    {
        var parts = new[]
        {
            new LinksetBounds.Part(new Vector3(3f, 0f, 0f), Quaternion.Identity, Vector3.One),
            new LinksetBounds.Part(new Vector3(-1f, 0f, 0f), Quaternion.Identity, Vector3.One),
        };

        var (centre, half) = LinksetBounds.Measure(Origin, Quaternion.Identity, Vector3.One, parts);

        // spans -1.5 .. 3.5
        Assert.Equal(1f, centre.X, 5);
        Assert.Equal(2.5f, half.X, 5);
    }
}
