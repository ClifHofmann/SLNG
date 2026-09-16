using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>
/// FEAT-PERF-07: pins <see cref="VolumeLod"/> against the reference viewer's arithmetic
/// (<c>LLVOVolume::calcLOD</c>, <c>LLVolumeLODGroup::getDetailFromTan</c>).
///
/// <para>The point of these tests is not that the numbers are "reasonable" -- it is that they are
/// the VIEWER's. Every constant in there was copied from a specific line of the vendored source,
/// and the failure mode of getting one wrong is not a crash but content that renders at a
/// different detail than Firestorm shows at the same spot, which is only discoverable by standing
/// in two viewers side by side.</para>
/// </summary>
public class VolumeLodTests
{
    private const float Factor = VolumeLod.DefaultLodFactor;

    /// <summary>The thresholds themselves: <c>BASE_THRESHOLD * {1, 2, 8}</c> with
    /// <c>BASE_THRESHOLD = 0.03</c>, and the comparison is <c>&lt;=</c>, so a tangent exactly ON a
    /// threshold belongs to the LOWER level.</summary>
    [Theory]
    [InlineData(0.0f, MeshDetailLevel.Low)]
    [InlineData(0.03f, MeshDetailLevel.Low)]      // exactly on: <= wins for the lower level
    [InlineData(0.0301f, MeshDetailLevel.Medium)]
    [InlineData(0.06f, MeshDetailLevel.Medium)]
    [InlineData(0.0601f, MeshDetailLevel.High)]
    [InlineData(0.24f, MeshDetailLevel.High)]
    [InlineData(0.2401f, MeshDetailLevel.Highest)]
    [InlineData(1000f, MeshDetailLevel.Highest)]
    public void ForTangent_MatchesTheViewersThresholdTable(float tanAngle, MeshDetailLevel expected)
    {
        Assert.Equal(expected, VolumeLod.ForTangent(tanAngle));
    }

    /// <summary>Detail falls monotonically with distance. Not a tautology: the near ramp squares
    /// the distance below <c>lodFactor*2</c> metres, so the curve has a kink in it, and a sign
    /// slip there would make close objects coarser than far ones.</summary>
    [Fact]
    public void ForDistance_NeverGetsMoreDetailedAsTheObjectGetsFurtherAway()
    {
        const float scaleLength = 4f;
        var previous = VolumeLod.ForDistance(0.25f, scaleLength, Factor);

        for (float d = 0.5f; d <= 400f; d += 0.25f)
        {
            var level = VolumeLod.ForDistance(d, scaleLength, Factor);
            Assert.True(level <= previous,
                $"detail rose from {previous} to {level} between {d - 0.25f} m and {d} m");
            previous = level;
        }
    }

    /// <summary>A bigger object holds its detail further out, at every distance. This is the whole
    /// reason the viewer's rule is an ANGLE rather than a distance: a 32 m building and a 0.2 m
    /// bottle at 40 m are not the same LOD problem.</summary>
    [Fact]
    public void ForDistance_LargerObjectsHoldDetailFurtherOut()
    {
        for (float d = 1f; d <= 200f; d += 1f)
        {
            var small = VolumeLod.ForDistance(d, 0.25f, Factor);
            var large = VolumeLod.ForDistance(d, 16f, Factor);
            Assert.True(large >= small, $"a 16 m object was coarser than a 0.25 m one at {d} m");
        }
    }

    /// <summary>
    /// The headline numbers, worked by hand from the viewer's own formula, so a silent change to
    /// any of the three places <c>lodFactor</c> appears shows up here.
    ///
    /// <para>A 2 m cube of mesh has scale vector (2,2,2), whose length is 3.4641. At 40 m with
    /// factor 1.0: radius = 3.4641*0.5 = 1.7321; distance = 40*(1-0.1) = 36, past the 2 m ramp;
    /// 36*PI/3 = 37.699; tan = 1.7321/37.699 = 0.04594, which falls between 0.03 and 0.06 -- the
    /// asset's <c>low_lod</c>. That is the measurement in the spec made concrete: on a real sim
    /// most of what is on screen is small and far, and the viewer draws it from blocks SLNG was
    /// ignoring entirely.</para>
    /// </summary>
    [Fact]
    public void ForDistance_TwoMetreCubeAtFortyMetres_ComesFromLowLod()
    {
        float scaleLength = new System.Numerics.Vector3(2f, 2f, 2f).Length();
        Assert.Equal(MeshDetailLevel.Medium, VolumeLod.ForDistance(40f, scaleLength, Factor));
    }

    /// <summary>The same cube at arm's length is the highest block (at 5 m: 4.5*PI/3 = 4.712,
    /// tan = 0.3675, past 0.24). The pair is what makes the test above a LOD test rather than an
    /// "everything is coarse" test.</summary>
    [Fact]
    public void ForDistance_TwoMetreCubeUpClose_ComesFromHighLod()
    {
        float scaleLength = new System.Numerics.Vector3(2f, 2f, 2f).Length();
        Assert.Equal(MeshDetailLevel.Highest, VolumeLod.ForDistance(5f, scaleLength, Factor));
    }

    /// <summary>And a small object -- a 0.3 m cube, the size most decorative clutter on a sim
    /// actually is -- is already at the lowest block by 40 m: length 0.5196, radius 0.2598,
    /// tan = 0.00689. This is the case that makes the feature worth its complexity; SLNG was
    /// drawing every one of these from <c>high_lod</c>.</summary>
    [Fact]
    public void ForDistance_SmallClutterAtFortyMetres_ComesFromLowestLod()
    {
        float scaleLength = new System.Numerics.Vector3(0.3f, 0.3f, 0.3f).Length();
        Assert.Equal(MeshDetailLevel.Low, VolumeLod.ForDistance(40f, scaleLength, Factor));
    }

    /// <summary>Raising Object Detail must never LOWER an object's level. The factor appears three
    /// times in the formula, once of them as <c>1 - factor*0.1</c> where it moves the opposite
    /// way, so "higher is more detail" is a real invariant and not obvious from the source.</summary>
    [Fact]
    public void ForDistance_RaisingTheLodFactor_NeverLowersTheLevel()
    {
        foreach (float scaleLength in new[] { 0.25f, 1f, 4f, 16f })
        {
            for (float d = 1f; d <= 200f; d += 1f)
            {
                var previous = VolumeLod.ForDistance(d, scaleLength, 0.5f);
                foreach (float factor in new[] { 1.0f, 1.5f, 2.0f, 3.0f, 4.0f })
                {
                    var level = VolumeLod.ForDistance(d, scaleLength, factor);
                    Assert.True(level >= previous,
                        $"factor {factor} gave {level} where a lower factor gave {previous} " +
                        $"at {d} m, scale {scaleLength}");
                    previous = level;
                }
            }
        }
    }

    /// <summary>Degenerate inputs resolve to the highest level rather than to a coarse mesh or a
    /// NaN. An object at distance 0 (the camera inside it) and a zero scale both reach this from
    /// ordinary code paths -- a prim being rezzed has no scale yet.</summary>
    [Theory]
    [InlineData(0f, 2f)]
    [InlineData(-1f, 2f)]
    [InlineData(10f, 0f)]
    [InlineData(float.NaN, 2f)]
    [InlineData(10f, float.NaN)]
    public void ForDistance_DegenerateInput_FallsBackToHighest(float distance, float scaleLength)
    {
        Assert.Equal(MeshDetailLevel.Highest, VolumeLod.ForDistance(distance, scaleLength, Factor));
    }

    /// <summary>
    /// The dead band: an object sitting exactly on a threshold must NOT change level, in either
    /// direction, or a camera that bobs with the walk animation re-keys its mesh and re-applies
    /// every face's material a few times a second forever.
    /// </summary>
    [Fact]
    public void ForDistanceWithHysteresis_OnTheThreshold_StaysWhereItIs()
    {
        const float scaleLength = 2f;

        // Find the distance where the level changes, by the un-hysteretic rule.
        float boundary = 0f;
        var previous = VolumeLod.ForDistance(1f, scaleLength, Factor);
        for (float d = 1f; d <= 300f; d += 0.01f)
        {
            var level = VolumeLod.ForDistance(d, scaleLength, Factor);
            if (level != previous) { boundary = d; break; }
            previous = level;
        }
        Assert.True(boundary > 0f, "no LOD boundary found to test against");

        // Just inside the band on the far side: the un-hysteretic rule has already switched, the
        // hysteretic one must not have.
        var coarser = VolumeLod.ForDistance(boundary, scaleLength, Factor);
        Assert.NotEqual(previous, coarser);
        Assert.Equal(previous,
            VolumeLod.ForDistanceWithHysteresis(boundary, scaleLength, Factor, previous));

        // And symmetrically: an object already at the coarser level does not jump back up just
        // because it drifted a hair inside the boundary.
        Assert.Equal(coarser,
            VolumeLod.ForDistanceWithHysteresis(boundary * 0.999f, scaleLength, Factor, coarser));
    }

    /// <summary>Hysteresis delays a change; it must not prevent one. A band that never releases
    /// would pin every object at whatever level it was first loaded with -- which is precisely the
    /// bug FEAT-PERF-07 exists to fix, reintroduced through the back door.</summary>
    [Fact]
    public void ForDistanceWithHysteresis_ClearlyPastTheThreshold_DoesChange()
    {
        const float scaleLength = 2f;
        var near = VolumeLod.ForDistance(2f, scaleLength, Factor);
        var far = VolumeLod.ForDistance(200f, scaleLength, Factor);
        Assert.NotEqual(near, far);

        Assert.Equal(far, VolumeLod.ForDistanceWithHysteresis(200f, scaleLength, Factor, near));
        Assert.Equal(near, VolumeLod.ForDistanceWithHysteresis(2f, scaleLength, Factor, far));
    }
}
