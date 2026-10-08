using SLNG.Assets;
using Xunit;

namespace SLNG.Assets.Tests;

/// <summary>BUG-PERF-09: the admission arithmetic that keeps the VRAM budget under a burst.</summary>
public class TextureAdmissionTests
{
    private const long Mb = 1024 * 1024;

    [Fact]
    public void TextureBytes_CountsTheMipChainAsAThirdOnTop()
    {
        Assert.Equal(1024L * 1024 * 4, TextureAdmission.TextureBytes(1024, 1024, mipmaps: false));
        long withMips = TextureAdmission.TextureBytes(1024, 1024, mipmaps: true);
        Assert.InRange(withMips, 1024L * 1024 * 4 * 4 / 3 - 4, 1024L * 1024 * 4 * 4 / 3 + 4); // the exact 4/3
        Assert.Equal(0, TextureAdmission.TextureBytes(0, 64, true));
    }

    [Fact]
    public void ExtraDiscard_IsZero_WhileTheTextureFits()
    {
        // 1024^2 with mips = ~5.3 MB; 100 MB of room.
        Assert.Equal(0, TextureAdmission.ExtraDiscardFor(10 * Mb, 100 * Mb, 1024, 1024, true, maxExtra: 2));
    }

    [Fact]
    public void ExtraDiscard_IsTheSmallestLevelThatFits()
    {
        // Full size ~5.33 MB, one level ~1.33 MB, two levels ~0.33 MB.
        long budget = 100 * Mb;

        // 96 MB resident: 4 MB left -> full size does not fit, one level does.
        Assert.Equal(1, TextureAdmission.ExtraDiscardFor(96 * Mb, budget, 1024, 1024, true, 2));
        // 99 MB resident: 1 MB left -> only two levels fit.
        Assert.Equal(2, TextureAdmission.ExtraDiscardFor(99 * Mb, budget, 1024, 1024, true, 2));
    }

    [Fact]
    public void ExtraDiscard_IsTheCap_WhenNothingFits()
    {
        // Already over budget: take the cheapest allowed upload, never more than the cap.
        Assert.Equal(2, TextureAdmission.ExtraDiscardFor(120 * Mb, 100 * Mb, 1024, 1024, true, 2));
        Assert.Equal(3, TextureAdmission.ExtraDiscardFor(120 * Mb, 100 * Mb, 1024, 1024, true, 3));
    }

    [Fact]
    public void ExtraDiscard_NeverExceedsTheCap_AndZeroCapMeansNoAdmission()
    {
        Assert.Equal(0, TextureAdmission.ExtraDiscardFor(500 * Mb, 100 * Mb, 1024, 1024, true, 0));
        Assert.Equal(0, TextureAdmission.ExtraDiscardFor(500 * Mb, 100 * Mb, 0, 0, true, 2));
    }

    [Fact]
    public void ExtraDiscard_IsMonotonic_InProjectedSizeAndInTextureSize()
    {
        long budget = 200 * Mb;
        int previous = 0;
        for (long projected = 0; projected <= 400 * Mb; projected += Mb)
        {
            int level = TextureAdmission.ExtraDiscardFor(projected, budget, 2048, 2048, true, 3);
            Assert.InRange(level, previous, 3);
            previous = level;
        }

        // At one projected size, a bigger texture never gets fewer levels than a smaller one.
        long fixedProjected = 190 * Mb;
        int last = 0;
        foreach (int side in new[] { 64, 128, 256, 512, 1024, 2048 })
        {
            int level = TextureAdmission.ExtraDiscardFor(fixedProjected, budget, side, side, true, 3);
            Assert.True(level >= last, $"{side}px got {level}, a smaller texture got {last}");
            last = level;
        }
    }

    [Fact]
    public void ExtraDiscard_HoldsTheBudgetOverABurst_UntilTheCapRunsOut()
    {
        // The Secret Love shape: thousands of 1024^2 textures arrive at once against a budget that
        // holds a few hundred of them. Admitting each with its level keeps the total inside the
        // budget, where admitting every one at full size would end at many times the budget.
        long budget = 1024 * Mb;
        long projected = 0;
        long unadmitted = 0;
        for (int i = 0; i < 7000; i++)
        {
            int extra = TextureAdmission.ExtraDiscardFor(projected, budget, 1024, 1024, true, 2);
            projected += TextureAdmission.TextureBytes(
                TextureLod.DimensionAfterDiscard(1024, extra), TextureLod.DimensionAfterDiscard(1024, extra), true);
            unadmitted += TextureAdmission.TextureBytes(1024, 1024, true);

            // While the cap still has room to work, the budget is never crossed.
            if (extra < 2) Assert.True(projected <= budget, $"crossed the budget at texture {i}");
        }
        Assert.True(unadmitted > 30 * budget);
        // Past the point where even the cap does not fit, the overshoot is the cap's floor
        // (~0.33 MB each), not the 5.33 MB of a full upload.
        Assert.True(projected < budget + 7000L * 350_000);
    }

    [Fact]
    public void ExtraDiscard_AlreadyAtTheSmallestSize_StaysWithinTheCapAndCostsNothingExtra()
    {
        // 16x16 cannot shrink below 8x8; asking for more levels changes nothing but must not throw.
        int level = TextureAdmission.ExtraDiscardFor(100 * Mb, 100 * Mb, 16, 16, true, 2);
        Assert.InRange(level, 0, 2);
    }

    [Theory]
    [InlineData(1024, 0, 1024)]
    [InlineData(1024, 1, 512)]
    [InlineData(1024, 2, 256)]
    [InlineData(64, 5, 8)]
    [InlineData(8, 3, 8)]
    [InlineData(4, 2, 4)]   // never grows
    public void DimensionAfterDiscard_HalvesDownToTheFloor(int dim, int discard, int expected)
        => Assert.Equal(expected, TextureLod.DimensionAfterDiscard(dim, discard));

    [Fact]
    public void BuiltForArea_ScalesByFourPerLevel()
    {
        Assert.Equal(40000f, TextureAdmission.BuiltForArea(40000f, 0));
        Assert.Equal(10000f, TextureAdmission.BuiltForArea(40000f, 1), 1);
        Assert.Equal(2500f, TextureAdmission.BuiltForArea(40000f, 2), 1);
    }
}
