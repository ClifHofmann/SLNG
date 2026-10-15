using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>
/// FEAT-PERF-14: pins <see cref="SmallObjectCull"/>. The rule is a screen-radius threshold in the
/// viewer's own pixel measure (<c>LLFace::calcPixelArea</c>), scaled the way <c>calcLOD</c> scales
/// every size-derived distance. These tests hold the properties the renderer relies on: bigger
/// objects reach further, a bigger Object Detail reaches further, nothing near is ever hidden, and
/// "no limit" is reported as exactly 0.
/// </summary>
public class SmallObjectCullTests
{
    // 1080p at the default 60 degrees vertical field of view.
    private const float Ppr = 1080f / (60f * (float)System.Math.PI / 180f);
    private const float Px = SmallObjectCull.DefaultPixelRadius;
    private const float Draw = 96f * 1.15f;

    [Fact]
    public void LodScale_IsOneAtTheViewersDefault_AndFollowsCalcLodsFactor()
    {
        Assert.Equal(1f, SmallObjectCull.LodScale(1f), 4);
        // factor / (1 - 0.1 * factor), normalised: 3.0 -> (3 / 0.7) * 0.9
        Assert.Equal(3f / 0.7f * 0.9f, SmallObjectCull.LodScale(3f), 3);
        Assert.Equal(2f / 0.8f * 0.9f, SmallObjectCull.LodScale(2f), 3);
    }

    [Fact]
    public void LodScale_StaysFinite_ForAbsurdSettings()
    {
        Assert.True(float.IsFinite(SmallObjectCull.LodScale(10f)));
        Assert.True(float.IsFinite(SmallObjectCull.LodScale(1000f)));
        Assert.Equal(1f, SmallObjectCull.LodScale(0f));
        Assert.Equal(1f, SmallObjectCull.LodScale(-2f));
        Assert.Equal(1f, SmallObjectCull.LodScale(float.NaN));
    }

    [Fact]
    public void EndDistance_MatchesTheScreenRadiusDefinition()
    {
        // A sphere of radius r at distance d spans atan(r / d) * Ppr pixels of radius (calcPixelArea).
        // At the end distance it must be spanning the threshold.
        const float r = 0.2f;
        float end = SmallObjectCull.EndDistance(r, Ppr, Px, 1f, hideDistance: 1000f);
        float pixelsAtEnd = (float)System.Math.Atan(r / end) * Ppr;
        Assert.Equal(Px, pixelsAtEnd, 2);
    }

    [Fact]
    public void EndDistance_GrowsWithRadius()
    {
        float a = SmallObjectCull.EndDistance(0.15f, Ppr, Px, 1f, 1000f);
        float b = SmallObjectCull.EndDistance(0.30f, Ppr, Px, 1f, 1000f);
        Assert.True(b > a);
        Assert.Equal(2f, b / a, 2);
    }

    [Fact]
    public void EndDistance_GrowsWithObjectDetail_LikeCalcLod()
    {
        float low = SmallObjectCull.EndDistance(0.2f, Ppr, Px, 1f, 1000f);
        float ultra = SmallObjectCull.EndDistance(0.2f, Ppr, Px, 3f, 1000f);
        Assert.Equal(SmallObjectCull.LodScale(3f), ultra / low, 2);
    }

    [Fact]
    public void EndDistance_GrowsWithViewportHeight_AndShrinksWithAWiderFov()
    {
        float hd = SmallObjectCull.EndDistance(0.2f, 1080f / 1.047f, Px, 1f, 5000f);
        float uhd = SmallObjectCull.EndDistance(0.2f, 2160f / 1.047f, Px, 1f, 5000f);
        float wide = SmallObjectCull.EndDistance(0.2f, 1080f / 1.745f, Px, 1f, 5000f);
        Assert.Equal(2f, uhd / hd, 2);
        Assert.True(wide < hd);
    }

    [Fact]
    public void EndDistance_NeverHidesAnythingNear()
    {
        // A speck: the threshold alone would say 1 m. The floor keeps it for 15 m.
        float end = SmallObjectCull.EndDistance(0.003f, Ppr, Px, 1f, 1000f);
        Assert.Equal(SmallObjectCull.MinEndMetres, end);
    }

    [Fact]
    public void EndDistance_NeverCullsABigObject()
    {
        Assert.Equal(0f, SmallObjectCull.EndDistance(SmallObjectCull.NeverCullRadius, Ppr, Px, 1f, 100000f));
        Assert.Equal(0f, SmallObjectCull.EndDistance(40f, Ppr, Px, 1f, 100000f));
    }

    [Fact]
    public void EndDistance_IsZero_WhenItWouldNotAct()
    {
        // 0.4 m radius: ~137 m at this threshold, which is past where the sweep hides it anyway.
        Assert.Equal(0f, SmallObjectCull.EndDistance(0.4f, Ppr, Px, 1f, Draw));
        // Disabled, or no radius, or no viewport yet.
        Assert.Equal(0f, SmallObjectCull.EndDistance(0.1f, Ppr, 0f, 1f, Draw));
        Assert.Equal(0f, SmallObjectCull.EndDistance(0f, Ppr, Px, 1f, Draw));
        Assert.Equal(0f, SmallObjectCull.EndDistance(0.1f, 0f, Px, 1f, Draw));
        Assert.Equal(0f, SmallObjectCull.EndDistance(float.NaN, Ppr, Px, 1f, Draw));
    }

    [Fact]
    public void EndDistance_CullsTheSmallDecorOfAClub_WithinTheDrawDistance()
    {
        // 10 cm cube (radius 0.087): a speck at 30 m. 25 cm cube (0.22): 76 m. Both inside 110 m.
        Assert.InRange(SmallObjectCull.EndDistance(0.087f, Ppr, Px, 1f, Draw), 25f, 35f);
        Assert.InRange(SmallObjectCull.EndDistance(0.22f, Ppr, Px, 1f, Draw), 70f, 80f);
    }

    [Fact]
    public void ShadowEnd_IsShorterThanTheMainEnd_ForTheSameObject()
    {
        float main = SmallObjectCull.EndDistance(0.2f, Ppr, Px, 1f, 5000f);
        float shadow = SmallObjectCull.ShadowEndDistance(0.2f, Ppr, Px, 1f);
        Assert.True(shadow < main);
        Assert.Equal(SmallObjectCull.ShadowPixelFactor, main / shadow, 2);
    }

    [Fact]
    public void ShadowEnd_IsZero_WhenTheCullIsOff_AndHonoursTheFloor()
    {
        Assert.Equal(0f, SmallObjectCull.ShadowEndDistance(0.2f, Ppr, 0f, 1f));
        Assert.Equal(SmallObjectCull.MinEndMetres, SmallObjectCull.ShadowEndDistance(0.003f, Ppr, Px, 1f));
    }

    [Fact]
    public void Margin_IsAFractionOfTheEnd_WithAOneMetreFloor_AndZeroForNoLimit()
    {
        Assert.Equal(0f, SmallObjectCull.Margin(0f));
        Assert.Equal(1f, SmallObjectCull.Margin(5f));
        Assert.Equal(7.5f, SmallObjectCull.Margin(75f), 3);
    }
}
