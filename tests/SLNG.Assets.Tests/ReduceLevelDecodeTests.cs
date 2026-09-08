using Xunit;

namespace SLNG.Assets.Tests;

/// <summary>
/// Guards reduce-level decoding (BUG-NET-11): asking the JPEG-2000 decoder for fewer wavelet
/// levels than the asset carries, so a distant object does not pay a 47.6 ms full decode to produce
/// a 64x64 upload.
///
/// Two things here are easy to get wrong and expensive to get wrong in-world:
///
/// 1. <b>ImageMagick's <c>jp2:reduce-factor=N</c> divides each dimension by 4^N, not 2^N.</b> The
///    name says otherwise and every reasonable assumption says otherwise. Measured per-file over 40
///    real cached assets (2026-09-03) and pinned here against a self-encoded codestream so a
///    Magick.NET upgrade that changes the mapping fails a test instead of silently halving every
///    texture's resolution.
/// 2. <b>A reduced decode must not read as a truncated one.</b> <see cref="AssetService"/> flags a
///    decode degraded when it comes out smaller than the SIZ marker declares, and the disk-cache
///    path DELETES the .j2c that produced a degraded decode. Without the reduce factor folded into
///    that expectation, turning this feature on would have wiped the texture cache one entry at a
///    time while rendering everything through the CoreJ2K fallback.
/// </summary>
public class ReduceLevelDecodeTests
{
    private static byte[] EncodeJ2c(int size)
    {
        using var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.CornflowerBlue, (uint)size, (uint)size);
        // Real content, so the codestream isn't a degenerate single-colour one.
        using (var overlay = new ImageMagick.MagickImage(ImageMagick.MagickColors.Orange, (uint)(size / 2), (uint)(size / 2)))
        {
            image.Composite(overlay, ImageMagick.Gravity.Northwest, ImageMagick.CompositeOperator.Over);
        }
        image.Format = ImageMagick.MagickFormat.J2c;
        return image.ToByteArray();
    }

    [Fact]
    public void SizMarkerIsReadableWithoutDecoding()
    {
        byte[] bytes = EncodeJ2c(256);

        Assert.True(AssetService.TryReadJ2kSize(bytes, out int w, out int h, out int components));
        Assert.Equal(256, w);
        Assert.Equal(256, h);
        Assert.True(components >= 3, $"expected at least RGB, got {components}");
    }

    [Fact]
    public void ReduceFactorDividesEachDimensionByFourPerLevel()
    {
        byte[] bytes = EncodeJ2c(256);

        var full = AssetService.DecodeTexture(bytes);
        Assert.NotNull(full);
        Assert.Equal(256, full!.Width);
        Assert.Equal(256, full.Height);

        var reduced = AssetService.DecodeTexture(bytes, isSculpt: false, reduceFactor: 1);
        Assert.NotNull(reduced);
        Assert.Equal(64, reduced!.Width);
        Assert.Equal(64, reduced.Height);
    }

    [Fact]
    public void AReducedDecodeIsNotReportedAsDegraded()
    {
        byte[] bytes = EncodeJ2c(256);

        var reduced = AssetService.DecodeTexture(bytes, isSculpt: false, reduceFactor: 1);

        Assert.NotNull(reduced);
        Assert.False(reduced!.IsDegraded,
            "a reduce-level decode is smaller ON PURPOSE; flagging it degraded makes the disk-cache " +
            "path delete the .j2c that produced it");
    }

    [Fact]
    public void AReducedDecodeReportsTheFullAssetSizeAsItsSource()
    {
        byte[] bytes = EncodeJ2c(256);

        var reduced = AssetService.DecodeTexture(bytes, isSculpt: false, reduceFactor: 1);

        Assert.NotNull(reduced);
        // Without this the renderer cannot tell "decoded small on purpose" from "this IS the whole
        // asset", so a texture first seen from far away would stay blurry for the session.
        Assert.Equal(256, reduced!.SourceWidth);
        Assert.Equal(256, reduced.SourceHeight);
        Assert.True(reduced.Width < reduced.SourceWidth);
    }

    [Fact]
    public void AFullDecodeReportsItsOwnSizeAsItsSource()
    {
        byte[] bytes = EncodeJ2c(256);

        var full = AssetService.DecodeTexture(bytes);

        Assert.NotNull(full);
        Assert.Equal(full!.Width, full.SourceWidth);
        Assert.Equal(full.Height, full.SourceHeight);
    }

    [Fact]
    public void SculptMapsAreNeverReduced()
    {
        byte[] bytes = EncodeJ2c(64);

        // A sculpt map's samples are vertex coordinates, not pixels -- decoding it at a lower
        // resolution silently drops vertices, so the reduce factor must be ignored outright.
        var sculpt = AssetService.DecodeTexture(bytes, isSculpt: true, reduceFactor: 2);

        Assert.NotNull(sculpt);
        Assert.Equal(64, sculpt!.Width);
        Assert.Equal(64, sculpt.Height);
    }

    [Theory]
    // A 1024x1024 (1,048,576 texels) covering the whole 1024-px-wide screen area needs it all.
    [InlineData(1024, 1024, 1_048_576f, 0)]
    // Quarter the texels per level: 4x smaller on screen is one discard level.
    [InlineData(1024, 1024, 262_144f, 1)]
    [InlineData(1024, 1024, 65_536f, 2)]
    // A distant object clamps rather than running away.
    [InlineData(1024, 1024, 1f, TextureLod.MaxDiscardLevel)]
    // No LOD information means decode everything -- the safe answer.
    [InlineData(1024, 1024, 0f, 0)]
    public void DiscardLevelFollowsTheTexelToPixelRatio(int w, int h, float area, int expected)
        => Assert.Equal(expected, TextureLod.DiscardLevelFor(w, h, area));

    // FEAT-PERF-04: the VRAM back-pressure bias adds discard levels to a world-texture upload...
    [Fact]
    public void GlobalLodBias_adds_discard_levels_for_a_real_screen_area()
    {
        int baseline = TextureLod.DiscardLevelFor(1024, 1024, 262_144f); // = 1
        try
        {
            TextureLod.GlobalLodBias = 2;
            Assert.Equal(baseline + 2, TextureLod.DiscardLevelFor(1024, 1024, 262_144f));
        }
        finally { TextureLod.GlobalLodBias = 0; }
    }

    // ...but NEVER for a caller with no LOD info (avatar / bake pass screenPixelArea 0).
    [Fact]
    public void GlobalLodBias_never_touches_an_unknown_screen_area()
    {
        try
        {
            TextureLod.GlobalLodBias = 3;
            Assert.Equal(0, TextureLod.DiscardLevelFor(1024, 1024, 0f));
        }
        finally { TextureLod.GlobalLodBias = 0; }
    }

    [Fact]
    public void GlobalLodBias_still_clamps_to_MaxDiscardLevel()
    {
        try
        {
            TextureLod.GlobalLodBias = 2;
            Assert.Equal(TextureLod.MaxDiscardLevel, TextureLod.DiscardLevelFor(1024, 1024, 1f));
        }
        finally { TextureLod.GlobalLodBias = 0; }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)] // one halving -- the caller's own resize handles it
    [InlineData(2, 1)] // two halvings == one reduce factor
    [InlineData(3, 1)]
    [InlineData(4, 2)]
    public void OneReduceFactorCoversTwoDiscardLevels(int discard, int expectedReduce)
        => Assert.Equal(expectedReduce, TextureLod.ReduceFactorFor(discard));

    [Theory]
    [InlineData(1024, 0, 1024)]
    [InlineData(1024, 1, 256)]
    [InlineData(1024, 2, 64)]
    [InlineData(24, 1, 6)]
    // Rounds UP, and never to zero -- the expectation a truncation check is compared against must
    // not undercut what the decoder actually produces.
    [InlineData(30, 1, 8)]
    [InlineData(3, 2, 1)]
    public void ReducedDimensionMatchesTheDecoder(int dimension, int reduce, int expected)
        => Assert.Equal(expected, TextureLod.ReducedDimension(dimension, reduce));
}
