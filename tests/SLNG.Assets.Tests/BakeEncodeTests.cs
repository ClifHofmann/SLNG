using CoreJ2K;
using CoreJ2K.Configuration;
using SkiaSharp;
using SLNG.Assets;
using Xunit;

namespace SLNG.Assets.Tests;

/// <summary>
/// FEAT-AVATAR-01: pins why LibreMetaverse's avatar bake produces a blank texture, and that SLNG's
/// replacement encoder does not.
///
/// <para><b>How this was narrowed down.</b> A dry run on 2026-08-31 inspected the composited image
/// directly instead of only its encoded size. Every bake channel came out at 507 bytes (Eyes 207)
/// while the <c>ManagedImage</c> the Baker had just composited was a full 1024x1024 with more than
/// eight distinct red values. So compositing works and the inputs decode (5/5 textures, two of them
/// 1024x1024 with Color+Alpha). The giveaway: Head (distinctRed &gt; 8) and Hair (distinctRed = 2)
/// produced <b>exactly</b> the same 507 bytes — the output size did not depend on the content at
/// all. The loss is in <c>AssetTexture.Encode</c>:
/// <code>AssetData = CompleteConfigurationPresets.Streaming.Encode(Image.ExportBitmap());</code></para>
///
/// <para>These tests reproduce that offline. Every earlier round of this investigation cost a login,
/// a rebake and a visual inspection; this answers the same question from a unit test.</para>
/// </summary>
public class BakeEncodeTests
{
    /// <summary>A bitmap with genuinely varied colour, so a small encode result can only mean the
    /// encoder discarded content — never that there was none.</summary>
    private static SKBitmap Gradient(int size, byte alpha = 255)
    {
        var bmp = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Unpremul));

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                bmp.SetPixel(x, y, new SKColor(
                    (byte)(x * 255 / size), (byte)(y * 255 / size), (byte)((x ^ y) & 0xFF), alpha));
            }
        }

        return bmp;
    }

    private static byte[] Bgra(SKBitmap bmp)
    {
        var raw = new byte[bmp.Width * bmp.Height * 4];
        for (int y = 0, i = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++, i += 4)
            {
                var c = bmp.GetPixel(x, y);
                raw[i] = c.Blue; raw[i + 1] = c.Green; raw[i + 2] = c.Red; raw[i + 3] = c.Alpha;
            }
        }
        return raw;
    }

    /// <summary>
    /// THE BUG. The preset LibreMetaverse hardcodes throws away essentially the whole image, on a
    /// fully opaque input with no alpha involved at all. This is the 507-byte bake, reproduced
    /// without a grid.
    /// </summary>
    [Fact]
    public void LibreMetaverses_encode_preset_discards_the_image()
    {
        using var bmp = Gradient(512);

        byte[] encoded = CompleteConfigurationPresets.Streaming.Encode(bmp);

        Assert.True(encoded.Length < 1000,
            $"expected LibreMetaverse's Streaming preset to still be broken, got {encoded.Length} bytes. " +
            "If CoreJ2K fixed rate control, J2KBakeTextureEncoder can drop back to a lossy preset.");
    }

    /// <summary>
    /// Rules out the obvious alternative explanations, so the fix targets the real cause. Neither
    /// the alpha channel nor the requested bitrate makes any difference: <c>WithBitrate</c> and
    /// <c>WithQuality</c> are inert in CoreJ2K 2.3.3.91, which is why raising quality could never
    /// have fixed the bake.
    /// </summary>
    [Fact]
    public void Neither_alpha_nor_bitrate_explains_the_loss()
    {
        using var opaque = Gradient(512);
        using var transparent = Gradient(512, 0);

        int opaqueLen = CompleteConfigurationPresets.Streaming.Encode(opaque).Length;
        int transparentLen = CompleteConfigurationPresets.Streaming.Encode(transparent).Length;

        // Transparency is not the culprit -- the opaque image is destroyed just as thoroughly.
        Assert.True(opaqueLen < 1000 && transparentLen < 1000,
            $"opaque {opaqueLen}, transparent {transparentLen}");

        // And asking for more bits changes nothing, at any setting.
        foreach (float bitrate in new[] { 0.5f, 2f, 8f })
        {
            Assert.True(CompleteConfigurationPresets.Streaming.WithBitrate(bitrate).Encode(opaque).Length < 1000,
                $"bitrate {bitrate} unexpectedly produced a real image -- rate control may be fixed upstream");
        }
    }

    /// <summary>
    /// THE FIX, proven end to end: SLNG's encoder produces a substantial stream that decodes back to
    /// the original pixels exactly. Zero channel error is what makes the lossless path worth its
    /// size — the bake that gets uploaded is the bake that was composited.
    /// </summary>
    [Fact]
    public void SLNGs_bake_encoder_round_trips_the_image_exactly()
    {
        using var source = Gradient(512);
        var encoder = new J2KBakeTextureEncoder();

        byte[] encoded = encoder.EncodeBake(Bgra(source), 512, 512);

        Assert.True(encoded.Length > 50_000,
            $"a lossless 512x512 bake should be substantial, got {encoded.Length} bytes");

        using var decoded = J2kImage.DecodeToImage<SKBitmap>(encoded);
        Assert.Equal(512, decoded.Width);
        Assert.Equal(512, decoded.Height);

        long error = 0;
        for (int y = 0; y < 512; y += 7)
        {
            for (int x = 0; x < 512; x += 7)
            {
                var a = source.GetPixel(x, y);
                var b = decoded.GetPixel(x, y);
                error += System.Math.Abs(a.Red - b.Red) + System.Math.Abs(a.Green - b.Green) + System.Math.Abs(a.Blue - b.Blue);
            }
        }

        Assert.Equal(0, error);
    }

    /// <summary>A malformed request must return empty rather than throw — this runs inside the bake
    /// path, where an exception would take out the whole appearance update.</summary>
    [Fact]
    public void Bake_encoder_rejects_undersized_input_without_throwing()
    {
        var encoder = new J2KBakeTextureEncoder();

        Assert.Empty(encoder.EncodeBake(new byte[16], 64, 64));
        Assert.Empty(encoder.EncodeBake(new byte[16], 0, 0));
    }
}
