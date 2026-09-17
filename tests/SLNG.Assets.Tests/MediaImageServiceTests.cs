using SkiaSharp;
using SLNG.Assets;
using Xunit;

namespace SLNG.Assets.Tests;

/// <summary>MVP3-3 Phase 3: MediaImageService's decode step and its non-network guard rails.
/// Deliberately does not exercise FetchAsync's HTTP path -- a real network call has no place in a
/// unit test (flaky, no network in CI, and an untrusted host besides); the scheme/decode logic
/// below is what's actually ours to get right.</summary>
public class MediaImageServiceTests
{
    // The canonical 1x1 transparent PNG (widely published as a tracking-pixel placeholder) --
    // small, known-good, and not hand-typed bytes that could themselves be silently malformed.
    private const string OnePixelPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";

    [Fact]
    public void Decode_ValidPng_ReturnsExactDimensionsAndFourBytesPerPixel()
    {
        var bytes = Convert.FromBase64String(OnePixelPngBase64);

        var data = MediaImageService.Decode(bytes);

        Assert.NotNull(data);
        Assert.Equal(1, data!.Width);
        Assert.Equal(1, data.Height);
        Assert.Equal(4, data.Rgba.Length);
    }

    [Fact]
    public void Decode_Garbage_ReturnsNull_NeverThrows()
    {
        var data = MediaImageService.Decode(new byte[] { 1, 2, 3, 4, 5 });
        Assert.Null(data);
    }

    [Fact]
    public void Decode_EmptyBytes_ReturnsNull()
    {
        var data = MediaImageService.Decode(Array.Empty<byte>());
        Assert.Null(data);
    }

    /// <summary>PRIM_MEDIA_AUTO_SCALE ("fit the media into the display area") is a real "contain"
    /// fit, not a stretch -- confirmed live 2026-09-17 against Firestorm's own black-letterboxed
    /// rendering of a MOAP probe face. A 4x2 source into a 4x4 target scales by exactly 1 (the
    /// limiting axis is height, 4/2=2, vs width 4/4=1) and centers with a clean 1px black bar top
    /// and bottom -- integer offsets on purpose, so the assertions below aren't fighting
    /// anti-aliasing at a fractional boundary.</summary>
    [Fact]
    public void Decode_WithFitDimensions_LetterboxesPreservingAspectRatio_NotAStretch()
    {
        using var source = new SKBitmap(4, 2, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(source)) canvas.Clear(SKColors.Red);
        using var image = SKImage.FromBitmap(source);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);

        var data = MediaImageService.Decode(encoded.ToArray(), fitWidth: 4, fitHeight: 4);

        Assert.NotNull(data);
        Assert.Equal(4, data!.Width);
        Assert.Equal(4, data.Height);

        // Row-major RGBA8: pixel (x, y) starts at ((y * width) + x) * 4.
        Assert.Equal((0, 0, 0, 255), PixelAt(data, x: 0, y: 0));   // top bar: black
        Assert.Equal((255, 0, 0, 255), PixelAt(data, x: 0, y: 1)); // image: red
        Assert.Equal((255, 0, 0, 255), PixelAt(data, x: 3, y: 2)); // image: red
        Assert.Equal((0, 0, 0, 255), PixelAt(data, x: 0, y: 3));   // bottom bar: black
    }

    private static (byte R, byte G, byte B, byte A) PixelAt(TextureData data, int x, int y)
    {
        int i = ((y * data.Width) + x) * 4;
        return (data.Rgba[i], data.Rgba[i + 1], data.Rgba[i + 2], data.Rgba[i + 3]);
    }

    [Fact]
    public async Task FetchAsync_NonHttpScheme_ReturnsNullWithoutANetworkCall()
    {
        var data = await MediaImageService.FetchAsync("ftp://example.com/image.png");
        Assert.Null(data);
    }

    [Fact]
    public async Task FetchAsync_UnparseableUrl_ReturnsNull()
    {
        var data = await MediaImageService.FetchAsync("not a url");
        Assert.Null(data);
    }

    /// <summary>FEAT-SEC-01. <c>PrivateAddressPolicyTests</c> pins the policy itself; this pins
    /// that it is actually WIRED INTO the HttpClient, which is the part that would silently stop
    /// being true if someone rebuilt the handler.
    ///
    /// <para>Asserting on the log line rather than on the null return, because a null is what
    /// comes back from an ordinary failed connection too — and "refused on purpose" versus
    /// "nothing was listening" is exactly the distinction under test. Still no real network call:
    /// the address is judged and rejected before a socket is opened.</para></summary>
    [Fact]
    public async Task FetchAsync_LoopbackHost_IsRefusedByThePolicyRatherThanAttempted()
    {
        var captured = new StringWriter();
        var previous = Console.Error;
        TextWriter? restore = null;
        try
        {
            Console.SetError(captured);
            restore = previous;

            // Port 9 (discard) and a unique path so the failure cache from another test cannot
            // answer this one.
            var data = await MediaImageService.FetchAsync(
                $"http://127.0.0.1:9/{Guid.NewGuid():N}.png");

            Assert.Null(data);
        }
        finally
        {
            if (restore != null) Console.SetError(restore);
        }

        string log = captured.ToString();
        Assert.Contains("private or reserved", log, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1", log, StringComparison.Ordinal);
    }

    /// <summary>The same guard, reached through a NAME rather than a literal address — the case a
    /// URL-string check cannot catch, since nothing about "localhost" looks like an IP.</summary>
    [Fact]
    public async Task FetchAsync_HostnameResolvingToLoopback_IsAlsoRefused()
    {
        var captured = new StringWriter();
        var previous = Console.Error;
        try
        {
            Console.SetError(captured);
            var data = await MediaImageService.FetchAsync(
                $"http://localhost:9/{Guid.NewGuid():N}.png");
            Assert.Null(data);
        }
        finally
        {
            Console.SetError(previous);
        }

        Assert.Contains("private or reserved", captured.ToString(), StringComparison.Ordinal);
    }
}
