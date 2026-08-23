using SLNG.Assets;
using Xunit;

namespace SLNG.Assets.Tests;

/// <summary>
/// The viewer's sculpt grid rule (sculpt_calc_mesh_resolution, llvolume.cpp:3163-3196).
///
/// <para>PrimMesher derives the grid by halving both map dimensions together until the pixel
/// count fits, which preserves the aspect ratio only in powers of two. On a square map the two
/// rules agree exactly, which is why this went unnoticed; on an elongated one they diverge by a
/// factor of two in vertex count.</para>
/// </summary>
public class SlSculptResolutionTests
{
    [Fact]
    public void SquareMapAtFullDetailIsThirtyTwoByThirtyTwo()
    {
        // SCULPT_REZ_4 = 32, and a 64x64 map can supply 64*64/4 = 1024 vertices, exactly the LOD
        // budget -- so neither limit bites and the grid is square.
        SlSculptResolution.Calc(64, 64, 4.0f, out int s, out int t);

        Assert.Equal(32, s);
        Assert.Equal(32, t);
    }

    [Fact]
    public void ElongatedMapSpendsItsBudgetInTheMapsProportion()
    {
        // The reef rock that exposed this: a 512x16 map. The viewer produces 5x204 (1230 vertices
        // once the wrap seam is added), PrimMesher's halving produced 129x5 = 645. Half the
        // resolution along the long axis, which is the axis the rock's strata run along.
        SlSculptResolution.Calc(512, 16, 4.0f, out int s, out int t);

        Assert.Equal(5, s);
        Assert.Equal(204, t);
    }

    [Theory]
    // "detail is usually one of: 1, 1.5, 2.5, 4.0" -- the viewer's own comment on sculpt_sides.
    [InlineData(1.0f, 6)]
    [InlineData(1.5f, 8)]
    [InlineData(2.5f, 16)]
    [InlineData(4.0f, 32)]
    public void DetailSelectsTheViewersRezSteps(float detail, int expectedSides)
    {
        Assert.Equal(expectedSides, SlSculptResolution.SidesForDetail(detail));
    }

    [Fact]
    public void ASmallMapCannotBeAskedForMoreVerticesThanItHas()
    {
        // max_vertices_map = w*h/4. A 16x16 map supplies 64, well under the 1024 the LOD allows,
        // so the map is the binding limit and the grid is 8x8.
        SlSculptResolution.Calc(16, 16, 4.0f, out int s, out int t);

        Assert.Equal(8, s);
        Assert.Equal(8, t);
    }

    [Fact]
    public void NeitherSideIsEverDegenerate()
    {
        // The viewer clamps both to 4 ("no degenerate sizes, please"). An extremely elongated map
        // would otherwise drive one side to zero and produce no surface at all.
        SlSculptResolution.Calc(1024, 4, 4.0f, out int s, out int t);

        Assert.True(s >= 4, $"s was {s}");
        Assert.True(t >= 4, $"t was {t}");
    }

    [Fact]
    public void AZeroSizedMapDoesNotDivideByZero()
    {
        SlSculptResolution.Calc(0, 0, 4.0f, out int s, out int t);

        Assert.True(s >= 4);
        Assert.True(t >= 4);
    }
}
