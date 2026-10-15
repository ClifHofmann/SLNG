using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>
/// FEAT-PERF-15: a fullbright + Shiny HIGH face or a smooth glTF material is also what a polished floor,
/// a beach or a water plane looks like. The orientation of the front face is what tells a mirror panel
/// from them, so these pin where the line is.
/// </summary>
public class MirrorCandidatePolicyTests
{
    [Theory]
    [InlineData(0f)]      // a wall
    [InlineData(0.2f)]    // slightly leaning back
    [InlineData(-0.2f)]
    [InlineData(0.7f)]    // exactly on the limit is still upright
    [InlineData(-0.7f)]
    public void A_mostly_vertical_panel_may_be_a_mirror(float normalY)
        => Assert.True(MirrorCandidatePolicy.IsUpright(normalY));

    [Theory]
    [InlineData(1f)]      // a floor / sand / water plane
    [InlineData(-1f)]     // a ceiling
    [InlineData(0.71f)]
    [InlineData(-0.71f)]
    [InlineData(0.9f)]
    public void A_mostly_horizontal_surface_is_never_a_heuristic_mirror(float normalY)
        => Assert.False(MirrorCandidatePolicy.IsUpright(normalY));
}
