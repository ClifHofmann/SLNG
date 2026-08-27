using Xunit;

namespace SLNG.Assets.Tests;

/// <summary>
/// Guards the invariant the worn-HUD black-square fix depends on: the Magick.NET path must not
/// flag a healthy decode as degraded. <see cref="AssetService"/> marks EVERY CoreJ2K-fallback
/// result <c>IsDegraded</c>, and callers that pass <c>rejectDegraded</c> turn that flag into a
/// discarded texture — so a false positive here renders faces untextured.
///
/// The matching negative case (a TRUNCATED codestream still decodes, via the CoreJ2K fallback) is
/// deliberately NOT unit-tested: it cannot be reproduced synthetically with the tools in this
/// repo. Magick.NET's J2C encoder emits a single-quality-layer codestream and ignores
/// number-resolutions/rate/progression-order defines (measured: byte-identical output with and
/// without them), and a single-layer stream is genuinely undecodable from a prefix — every
/// truncation of a self-encoded 64/256/512 px image decoded to null. Real SL/OpenSim assets are
/// multi-layer precisely so they can stream, which is why they behave the opposite way.
///
/// That behaviour was instead measured directly, 2026-08-27, against six real truncated
/// codestreams in this machine's asset cache: five worn-HUD textures of exactly 600 bytes (the SL
/// protocol's FIRST_PACKET_SIZE) whose first tile-part declares ~15-18 kB via Psot. Magick.NET
/// refused all six ("Tile part length size inconsistent with stream length"); CoreJ2K decoded all
/// six at their declared resolution (256x256, 512x512, 64x64, ...). Committing one as a fixture
/// would mean redistributing third-party texture data, which this project declines to do (same
/// call as IMG_ALPHA_GRAD_2D in FEAT-RENDER-02).
/// </summary>
public class TruncatedCodestreamDecodeTests
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

    [Theory]
    [InlineData(64)]
    [InlineData(256)]
    public void Complete_codestream_decodes_clean_and_is_not_flagged_degraded(int size)
    {
        var bytes = EncodeJ2c(size);

        var result = AssetService.DecodeTexture(bytes);

        Assert.NotNull(result);
        Assert.Equal(size, result!.Width);
        Assert.Equal(size, result.Height);
        Assert.NotEmpty(result.Rgba);
        // A false "degraded" is not cosmetic: rejectDegraded callers discard the texture entirely.
        Assert.False(result.IsDegraded, "an intact codestream must not be reported as degraded");
    }
}
