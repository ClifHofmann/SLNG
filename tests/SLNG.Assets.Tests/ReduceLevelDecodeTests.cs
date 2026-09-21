using Xunit;

namespace SLNG.Assets.Tests;

/// <summary>
/// Guards reduce-level decoding (BUG-NET-11, corrected by BUG-RENDER-36): asking the JPEG-2000
/// decoder for fewer wavelet levels than the asset carries, so a distant object does not pay a
/// full decode to produce a small upload.
///
/// Three things here are easy to get wrong and expensive to get wrong in-world:
///
/// 1. <b>The reduced decode must contain the WHOLE image.</b> This is the one that shipped broken.
///    ImageMagick's <c>jp2:reduce-factor=N</c> returns the top-left <c>1/2^N</c> region downscaled
///    by <c>2^N</c>, not the picture at a lower resolution -- so vendor panels showed a zoomed
///    close-up until you flew near enough to force a full re-decode. The old tests here checked
///    only DIMENSIONS, which that define gets right, and so they passed throughout. Worse, the
///    fixture drew its marker in the NORTHWEST corner, which is exactly the part a top-left crop
///    keeps: even a content check would have passed. The marker now sits in the SOUTHEAST.
/// 2. <b>One step is one halving.</b> The old mapping divided discard levels by two, because
///    reduce-factor's output dimensions looked like 4^N. They did -- while cropping.
/// 3. <b>A reduced decode must not read as a truncated one.</b> <see cref="AssetService"/> flags a
///    decode degraded when it comes out smaller than the SIZ marker declares, and the disk-cache
///    path DELETES the .j2c that produced a degraded decode.
/// </summary>
public class ReduceLevelDecodeTests
{
    private static byte[] EncodeJ2c(int size)
    {
        using var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.CornflowerBlue, (uint)size, (uint)size);
        // Real content, so the codestream isn't a degenerate single-colour one -- and deliberately
        // in the SOUTHEAST quarter. A decoder that returns the top-left region instead of the whole
        // image loses this entirely, which is the failure that shipped. The previous fixture put it
        // in the northwest, where a crop keeps it.
        using (var overlay = new ImageMagick.MagickImage(ImageMagick.MagickColors.Orange, (uint)(size / 2), (uint)(size / 2)))
        {
            image.Composite(overlay, ImageMagick.Gravity.Southeast, ImageMagick.CompositeOperator.Over);
        }
        image.Format = ImageMagick.MagickFormat.J2c;
        return RawCodestream(image.ToByteArray());
    }

    /// <summary>
    /// Unwraps Magick's JP2-BOXED output down to the bare codestream SL and OpenSim actually
    /// serve.
    /// </summary>
    /// <remarks>
    /// Without this the fixture is not the thing under test. Magick's own <c>J2c</c> writer emits
    /// a boxed file starting <c>00 00 00 0C 6A 50 20 20</c>, while the grid serves a raw
    /// codestream starting <c>FF 4F</c> -- and the decoder path behaves differently on the two, so
    /// a test built on the boxed form can pass while production is broken. That is not
    /// hypothetical: it is how the crop this file now guards against got through.
    /// </remarks>
    private static byte[] RawCodestream(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0x4F) return bytes;

        for (int i = 0; i + 1 < bytes.Length; i++)
        {
            if (bytes[i] == 0xFF && bytes[i + 1] == 0x4F && i + 3 < bytes.Length
                && bytes[i + 2] == 0xFF && bytes[i + 3] == 0x51)
            {
                var raw = new byte[bytes.Length - i];
                System.Array.Copy(bytes, i, raw, 0, raw.Length);
                return raw;
            }
        }
        return bytes;
    }

    /// <summary>Roughly how orange a pixel is, from the decoded RGBA buffer.</summary>
    private static bool IsOrangeish(TextureData t, int x, int y)
    {
        int i = (y * t.Width + x) * 4;
        byte r = t.Rgba[i], g = t.Rgba[i + 1], b = t.Rgba[i + 2];
        return r > 150 && g > 80 && b < 120;
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
    public void OneResolutionStepHalvesEachDimension()
    {
        byte[] bytes = EncodeJ2c(256);

        var full = AssetService.DecodeTexture(bytes);
        Assert.NotNull(full);
        Assert.Equal(256, full!.Width);
        Assert.Equal(256, full.Height);

        var reduced = AssetService.DecodeTexture(bytes, isSculpt: false, resolutionSteps: 1);
        Assert.NotNull(reduced);
        Assert.Equal(128, reduced!.Width);
        Assert.Equal(128, reduced.Height);

        var smaller = AssetService.DecodeTexture(bytes, isSculpt: false, resolutionSteps: 2);
        Assert.NotNull(smaller);
        Assert.Equal(64, smaller!.Width);
        Assert.Equal(64, smaller.Height);
    }

    [Fact]
    public void AReducedDecodeContainsTheWholeImage()
    {
        // THE test this file was missing. The fixture's orange block sits in the southeast
        // quarter; a decoder that hands back the top-left region -- which is what
        // jp2:reduce-factor actually does -- returns an image with no orange in it at all.
        byte[] bytes = EncodeJ2c(256);

        var reduced = AssetService.DecodeTexture(bytes, isSculpt: false, resolutionSteps: 1);
        Assert.NotNull(reduced);

        int w = reduced!.Width, h = reduced.Height;
        Assert.True(IsOrangeish(reduced, w - 4, h - 4),
            "the bottom-right corner must still be the marker; if it is not, the decoder returned " +
            "a crop of the top-left instead of the whole image at a lower resolution");
        Assert.False(IsOrangeish(reduced, 4, 4),
            "the top-left corner must still be the background -- otherwise the image was not " +
            "merely cropped but rescaled from the wrong region");
    }

    [Fact]
    public void AReducedDecodeIsNotReportedAsDegraded()
    {
        byte[] bytes = EncodeJ2c(256);

        var reduced = AssetService.DecodeTexture(bytes, isSculpt: false, resolutionSteps: 1);

        Assert.NotNull(reduced);
        Assert.False(reduced!.IsDegraded,
            "a reduce-level decode is smaller ON PURPOSE; flagging it degraded makes the disk-cache " +
            "path delete the .j2c that produced it");
    }

    [Fact]
    public void AReducedDecodeReportsTheFullAssetSizeAsItsSource()
    {
        byte[] bytes = EncodeJ2c(256);

        var reduced = AssetService.DecodeTexture(bytes, isSculpt: false, resolutionSteps: 1);

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
        var sculpt = AssetService.DecodeTexture(bytes, isSculpt: true, resolutionSteps: 2);

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
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(4, 4)]
    // Never negative, whatever a caller passes.
    [InlineData(-3, 0)]
    public void OneDiscardLevelIsOneResolutionStep(int discard, int expected)
        => Assert.Equal(expected, TextureLod.ResolutionStepsFor(discard));

    [Theory]
    [InlineData(1024, 0, 1024)]
    [InlineData(1024, 1, 512)]
    [InlineData(1024, 2, 256)]
    [InlineData(24, 1, 12)]
    // Never to zero -- the expectation a truncation check is compared against must not undercut
    // what the decoder actually produces.
    [InlineData(1, 3, 1)]
    public void ReducedDimensionMatchesTheDecoder(int dimension, int steps, int expected)
        => Assert.Equal(expected, TextureLod.ReducedDimension(dimension, steps));

    [Fact]
    public void DecompositionLevelsAreReadableWithoutDecoding()
    {
        // A resolution level is ABSOLUTE -- 0 is the smallest image -- so "two steps smaller"
        // cannot be expressed without knowing how many levels the codestream carries.
        byte[] bytes = EncodeJ2c(256);

        Assert.True(AssetService.TryReadJ2kDecompositionLevels(bytes, out int levels));
        Assert.InRange(levels, 1, 32);
    }
}
