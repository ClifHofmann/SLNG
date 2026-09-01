using System.Linq;
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

    /// <summary>
    /// THE CONTAINER. Second Life textures are raw JPEG2000 codestreams, which start with the SOC
    /// and SIZ markers FF 4F FF 51 — verified against a texture pulled off the grid (2048x2048,
    /// four components, MCT on, no file-format boxes).
    ///
    /// <para>CoreJ2K wraps its output in JP2 boxes by default, so the stream began
    /// 00 00 00 0C 6A 50 20 20 instead. That upload is accepted, stored and served, and then renders
    /// as flat grey in a real viewer while decoding perfectly here — because CoreJ2K reads back its
    /// own container. Measured 2026-09-01 on three uploaded test textures, seen in Firestorm.</para>
    ///
    /// <para>This is the check the round-trip test could not make: decoding with the same library
    /// that encoded proves the data survives, not that anyone else can read it.</para>
    /// </summary>
    [Fact]
    public void Bake_encoder_emits_a_raw_codestream_not_a_jp2_file()
    {
        using var source = Gradient(256);

        byte[] encoded = new J2KBakeTextureEncoder().EncodeBake(Bgra(source), 256, 256);

        Assert.True(encoded.Length > 4, "nothing encoded");
        Assert.Equal(new byte[] { 0xFF, 0x4F, 0xFF, 0x51 }, encoded.Take(4).ToArray());
    }

    /// <summary>The colour transform and component count have to match what the grid ships too: four
    /// 8-bit components with the multiple-component transform on. Read straight out of the SIZ and
    /// COD markers, so a preset change that silently drops colour is caught here rather than by
    /// someone looking at a grey avatar.</summary>
    [Fact]
    public void Bake_encoder_keeps_four_components_and_the_colour_transform()
    {
        using var source = Gradient(256);
        byte[] j2k = new J2KBakeTextureEncoder().EncodeBake(Bgra(source), 256, 256);

        int components = -1, mct = -1;
        for (int p = 2; p < j2k.Length - 3 && j2k[p] == 0xFF;)
        {
            byte marker = j2k[p + 1];
            if (marker == 0x93) break; // SOD — pixel data from here on
            int length = (j2k[p + 2] << 8) | j2k[p + 3];
            int seg = p + 4;

            if (marker == 0x51) components = (j2k[seg + 34] << 8) | j2k[seg + 35]; // SIZ.Csiz
            if (marker == 0x52) mct = j2k[seg + 4];                                 // COD.SGcod MCT
            p += 2 + length;
        }

        Assert.Equal(4, components);
        Assert.Equal(1, mct);
    }
}
