using Xunit;

namespace SLNG.Assets.Tests;

/// <summary>
/// BUG-PERF-18: a reduce level is limited to what the codestream carries, and to a size a face can still
/// be drawn with. Before, a level beyond the codestream's decomposition levels made the reduced decode give
/// up and decode the whole image (4-30x the work, cached as if it were the small one), and nothing stopped a
/// 32x32 texture from being reduced to a single pixel.
/// </summary>
public class ReduceClampTests
{
    // ---- the pure clamp -------------------------------------------------------------------------

    [Theory]
    // Inside everything: unchanged.
    [InlineData(2, 1024, 1024, 5, 2)]
    [InlineData(0, 1024, 1024, 5, 0)]
    // Beyond the codestream's levels (a 3-level stream cannot go 5 steps down).
    [InlineData(5, 1024, 1024, 3, 3)]
    [InlineData(4, 1024, 1024, 1, 1)]
    // Beyond the renderer's own ceiling.
    [InlineData(9, 4096, 4096, 12, TextureLod.MaxDiscardLevel)]
    // Unknown or zero levels, or a negative request: decode whole.
    [InlineData(3, 1024, 1024, -1, 0)]
    [InlineData(3, 1024, 1024, 0, 0)]
    [InlineData(-2, 1024, 1024, 5, 0)]
    // A side never drops below 8 px (it is already smaller than 8: left alone).
    [InlineData(5, 32, 32, 5, 2)]       // 32 -> 8
    [InlineData(5, 16, 16, 5, 1)]       // 16 -> 8
    [InlineData(3, 8, 8, 5, 0)]         // already at the minimum
    [InlineData(3, 4, 4, 5, 0)]         // smaller than the minimum
    // The shorter side limits it: 1024x64 can halve three times (64 -> 8).
    [InlineData(5, 1024, 64, 5, 3)]
    public void ReduceLevelIsClampedToWhatTheCodestreamAndTheMinimumSizeAllow(
        int reduce, int width, int height, int levels, int expected)
        => Assert.Equal(expected, TextureLod.ClampReduceLevel(reduce, width, height, levels));

    [Fact]
    public void AClampedLevelNeverProducesAnImageSmallerThanTheMinimumSide()
    {
        foreach (int size in new[] { 8, 12, 16, 24, 32, 48, 64, 100, 128, 256, 512, 1024 })
        {
            foreach (int levels in new[] { 0, 1, 2, 3, 5, 7 })
            {
                foreach (int reduce in new[] { 0, 1, 2, 3, 4, 5, 6, 9 })
                {
                    int clamped = TextureLod.ClampReduceLevel(reduce, size, size, levels);
                    Assert.InRange(clamped, 0, System.Math.Min(reduce, System.Math.Max(0, levels)));
                    Assert.True(TextureLod.ReducedDimension(size, clamped) >= System.Math.Min(size, TextureLod.MinReducedDimension),
                        $"size {size}, levels {levels}, reduce {reduce} -> {clamped} leaves {TextureLod.ReducedDimension(size, clamped)} px");
                }
            }
        }
    }

    // ---- the decoder honours it -----------------------------------------------------------------

    private static byte[] EncodeJ2c(int size)
    {
        using var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.CornflowerBlue, (uint)size, (uint)size);
        image.Format = ImageMagick.MagickFormat.J2c;
        byte[] bytes = image.ToByteArray();
        // The grid serves a bare codestream, not Magick's JP2-boxed file (see ReduceLevelDecodeTests).
        for (int i = 0; i + 3 < bytes.Length; i++)
        {
            if (bytes[i] == 0xFF && bytes[i + 1] == 0x4F && bytes[i + 2] == 0xFF && bytes[i + 3] == 0x51)
            {
                var raw = new byte[bytes.Length - i];
                System.Array.Copy(bytes, i, raw, 0, raw.Length);
                return raw;
            }
        }
        return bytes;
    }

    [Fact]
    public void AReduceBeyondTheCodestreamsLevelsServesTheSmallestLevelItHasNotTheWholeImage()
    {
        byte[] bytes = EncodeJ2c(256);
        Assert.True(AssetService.TryReadJ2kDecompositionLevels(bytes, out int levels));

        var tooFar = AssetService.DecodeTexture(bytes, isSculpt: false, resolutionSteps: levels + 4);

        Assert.NotNull(tooFar);
        int expected = TextureLod.ReducedDimension(256, TextureLod.ClampReduceLevel(levels + 4, 256, 256, levels));
        Assert.Equal(expected, tooFar!.Width);
        Assert.Equal(expected, tooFar.Height);
        Assert.True(tooFar.Width < 256, "a level beyond the stream must not fall back to the full image");
        Assert.False(tooFar.IsDegraded);
        Assert.Equal(256, tooFar.SourceWidth);
    }

    [Fact]
    public void ATinyTextureIsNeverReducedBelowTheMinimumSide()
    {
        byte[] bytes = EncodeJ2c(16);

        var reduced = AssetService.DecodeTexture(bytes, isSculpt: false, resolutionSteps: 5);

        Assert.NotNull(reduced);
        Assert.True(reduced!.Width >= TextureLod.MinReducedDimension && reduced.Height >= TextureLod.MinReducedDimension,
            $"decoded {reduced.Width}x{reduced.Height}");
    }
}
