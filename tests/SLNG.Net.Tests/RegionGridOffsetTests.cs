using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>
/// BUG-NET-04 — "nach dem Teleport sehe ich noch die sim auf der ich gerade war": a teleport to a
/// distant region left the old region's terrain/objects rendered indefinitely, because nothing
/// forced its removal until the grid's own <c>DisableSimulator</c> arrived, which does not
/// reliably happen promptly for a teleport (only well-tested for a walking border-crossing, where
/// the old region stays a live BUG-NET-03 neighbor circuit).
///
/// <see cref="GridSession.OnSimChanged"/> (not directly testable -- it needs a live
/// <c>NetworkManager.SimChanged</c> event) removes the old region eagerly when it is more than one
/// region-grid step away from the new current sim, on the reasoning that such a region cannot
/// possibly be a legitimate neighbor. These tests pin the distance math that decision depends on:
/// <see cref="GridSession.RegionGridOffset"/>.
/// </summary>
public class RegionGridOffsetTests
{
    /// <summary>Builds a region handle the way SL/OpenSim do: X and Y are the region's SOUTHWEST
    /// corner in metres (each a multiple of 256), packed as (X &lt;&lt; 32) | Y.</summary>
    private static ulong Handle(int regionStepsX, int regionStepsY) =>
        ((ulong)(uint)(regionStepsX * 256) << 32) | (uint)(regionStepsY * 256);

    [Fact]
    public void Same_region_is_zero_offset()
    {
        var h = Handle(1000, 1000);
        Assert.Equal((0L, 0L), GridSession.RegionGridOffset(h, h));
    }

    [Theory]
    [InlineData(1, 0)]   // east
    [InlineData(-1, 0)]  // west
    [InlineData(0, 1)]   // north
    [InlineData(0, -1)]  // south
    [InlineData(1, 1)]   // diagonal neighbor
    [InlineData(-1, -1)] // diagonal neighbor
    public void Immediate_neighbors_are_within_one_step(int dx, int dy)
    {
        var from = Handle(1000, 1000);
        var neighbor = Handle(1000 + dx, 1000 + dy);

        var (gotDx, gotDy) = GridSession.RegionGridOffset(neighbor, from);

        Assert.Equal(dx, gotDx);
        Assert.Equal(dy, gotDy);
        // This is the exact test OnSimChanged applies to decide "leave it to DisableSimulator".
        Assert.True(System.Math.Abs(gotDx) <= 1 && System.Math.Abs(gotDy) <= 1);
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(0, -5)]
    [InlineData(40, 40)]   // a real cross-continent teleport, not a border crossing
    [InlineData(-100, 0)]
    public void Distant_regions_exceed_one_step(int dx, int dy)
    {
        var from = Handle(1000, 1000);
        var distant = Handle(1000 + dx, 1000 + dy);

        var (gotDx, gotDy) = GridSession.RegionGridOffset(distant, from);

        Assert.Equal(dx, gotDx);
        Assert.Equal(dy, gotDy);
        // This is the exact test OnSimChanged applies to decide "remove it eagerly, don't wait".
        Assert.True(System.Math.Abs(gotDx) > 1 || System.Math.Abs(gotDy) > 1);
    }

    [Fact]
    public void Offset_is_antisymmetric()
    {
        var a = Handle(1000, 1000);
        var b = Handle(1005, 998);

        var (dx1, dy1) = GridSession.RegionGridOffset(b, a);
        var (dx2, dy2) = GridSession.RegionGridOffset(a, b);

        Assert.Equal(dx1, -dx2);
        Assert.Equal(dy1, -dy2);
    }
}
