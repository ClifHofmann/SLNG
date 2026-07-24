using System;
using System.Linq;
using ImageMagick;
using SLNG.Assets;
using Xunit;

namespace SLNG.Assets.Tests;

/// <summary>
/// Regression tests for the sculpted-prim "melted blob" bug (fix/broken-mesh-objects branch).
///
/// Root cause: <see cref="AssetService.DecodeTexture"/> used to unconditionally force every
/// sculpt-map decode through the CoreJ2K fallback path, discarding Magick.NET's decode (and its
/// ability to detect a truncated/corrupt J2C bitstream) even for perfectly intact assets.
/// CoreJ2K does NOT reliably detect truncation — it silently returns a full-dimension "successful"
/// decode with the missing wavelet detail replaced by a smoothed/averaged approximation. Because a
/// sculpt map encodes an independent vertex position per pixel, that smoothing corrupts the whole
/// vertex grid into rounded, edge-free geometry instead of failing loudly or leaving the true
/// pixel data intact.
///
/// These tests pin down the contract that fix restores: an intact sculpt-map bitstream decodes
/// through Magick.NET (preserving sharp per-pixel detail, not marked degraded), while a genuinely
/// truncated one is either rejected outright or explicitly flagged <c>IsDegraded</c> so
/// <see cref="AssetService"/>'s retry logic does not hand corrupted vertex data to the mesher.
/// </summary>
public class SculptDecodeTests
{
    private const int Size = 64;

    /// <summary>Builds a 64x64 high-frequency "sculpt-like" bitstream (every pixel encodes an
    /// unrelated value, like independent vertex coordinates) using Magick.NET/OpenJP2 at a
    /// realistic lossy quality — not a contrived worst-case max-compression encode.</summary>
    private static byte[] EncodeSyntheticSculptMap()
    {
        using var image = new MagickImage(MagickColors.Black, Size, Size);
        using (var pixels = image.GetPixels())
        {
            var buf = new byte[Size * Size * 3];
            var rnd = new Random(1234);
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    // A fine checkerboard plus per-cell jitter: high local contrast, similar in
                    // spirit to how adjacent sculpt-map texels encode unrelated vertex positions.
                    byte baseValue = (byte)(((x / 3 + y / 3) % 2 == 0) ? 40 : 210);
                    byte jitter = (byte)rnd.Next(0, 20);
                    byte v = (byte)Math.Clamp(baseValue + jitter - 10, 0, 255);
                    int idx = (y * Size + x) * 3;
                    buf[idx] = v;
                    buf[idx + 1] = v;
                    buf[idx + 2] = v;
                }
            }
            pixels.SetPixels(buf);
        }
        image.Format = MagickFormat.J2c;
        image.Quality = 90;
        return image.ToByteArray();
    }

    [Fact]
    public void IntactSculptMap_DecodesThroughMagickPath_NotMarkedDegraded()
    {
        byte[] bytes = EncodeSyntheticSculptMap();

        var result = AssetService.DecodeTexture(bytes, isSculpt: true);

        Assert.NotNull(result);
        Assert.Equal(Size, result!.Width);
        Assert.Equal(Size, result.Height);
        // The regression: the old code forced every sculpt map through the CoreJ2K fallback,
        // which always returns IsDegraded = true (see the CoreJ2K catch-block below). An intact
        // bitstream must decode through the primary Magick.NET path and NOT be marked degraded.
        Assert.False(result.IsDegraded);
    }

    [Fact]
    public void IntactSculptMap_PreservesHighFrequencyDetail_NotSmoothedToFlatAverage()
    {
        byte[] bytes = EncodeSyntheticSculptMap();

        var result = AssetService.DecodeTexture(bytes, isSculpt: true);
        Assert.NotNull(result);

        // A "melted" decode collapses adjacent, wildly different pixels toward a flat mid-gray
        // average. Sum the horizontal gradient across the middle row; a healthy decode preserves
        // most of the checkerboard's contrast, a smoothed one collapses it toward zero.
        long gradientSum = 0;
        int y = Size / 2;
        for (int x = 1; x < Size; x++)
        {
            int prev = result!.Rgba[(y * Size + (x - 1)) * 4];
            int cur = result.Rgba[(y * Size + x) * 4];
            gradientSum += Math.Abs(cur - prev);
        }

        // The source checkerboard swings ~170 levels roughly every 3 pixels; a faithful decode
        // should retain a large fraction of that contrast across the row. A collapsed/averaged
        // decode (e.g. everything pulled toward ~128) would score close to zero here.
        Assert.True(gradientSum > 2000, $"Expected sharp sculpt-map detail to survive decode, got gradient sum {gradientSum} (looks smoothed/melted).");
    }

    [Fact]
    public void TruncatedSculptMap_NeverReturnsATrustedNonDegradedResult()
    {
        byte[] full = EncodeSyntheticSculptMap();
        // Simulate a dropped tail UDP packet: keep most, but not all, of the codestream.
        byte[] truncated = full[..(int)(full.Length * 0.7)];

        var result = AssetService.DecodeTexture(truncated, isSculpt: true);

        // Either outright rejected, or explicitly flagged degraded so the caller knows not to
        // trust it for geometry (and, per AssetService.FetchAndDecodeTextureAsync, retries the
        // fetch instead of handing it straight to the mesher). What must NEVER happen is a
        // "successful", non-degraded result built from incomplete bytes.
        if (result != null)
        {
            Assert.True(result.IsDegraded, "A decode built from a truncated sculpt-map bitstream must be marked degraded, not trusted as-is.");
        }
    }
}
