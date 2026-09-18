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

    /// <summary>Pins the letterbox's RESAMPLING, which the test above does not reach: it fits 4x2
    /// into 4x4, a scale of exactly 1, so no filter ever runs. Here a 2x2 source into 4x4 is a
    /// clean 2x upscale, where nearest-neighbour and any smooth filter disagree visibly.
    ///
    /// <para>Nearest is the behaviour being pinned, and deliberately so. SkiaSharp 3 resolved the
    /// paintless <c>DrawBitmap(bitmap, rect)</c> to <c>SKSamplingOptions.Default</c>
    /// (<c>DrawBitmap</c> -> <c>DrawImage</c> -> <c>paint?.FilterQuality... ?? Default</c>,
    /// verified against the v3.119.0 source), and <c>Default</c> is
    /// <c>SKFilterMode.Nearest</c>. SkiaSharp 4 made that overload obsolete and the call now
    /// passes the options explicitly -- this test is what proves the migration kept the pixels
    /// identical rather than quietly picking a different filter.</para>
    ///
    /// <para>Nearest is arguably the wrong choice for downscaling fetched media, but it is the
    /// state the MOAP letterbox was compared against Firestorm in on 2026-09-17. If that is ever
    /// revisited, this test is the thing that should fail, on purpose.</para></summary>
    [Fact]
    public void Decode_WithFitDimensions_UpscalesWithNearestNeighbour_NoBlendedEdges()
    {
        using var source = new SKBitmap(2, 2, SKColorType.Rgba8888, SKAlphaType.Premul);
        source.SetPixel(0, 0, SKColors.Red);
        source.SetPixel(1, 0, SKColors.Blue);
        source.SetPixel(0, 1, SKColors.Lime);
        source.SetPixel(1, 1, SKColors.White);
        using var image = SKImage.FromBitmap(source);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);

        // 2x2 -> 4x4: aspect matches, so the fit is a pure 2x scale with no bars at all.
        var data = MediaImageService.Decode(encoded.ToArray(), fitWidth: 4, fitHeight: 4);

        Assert.NotNull(data);

        // Each source pixel must become a solid 2x2 block. The pixels that matter are the ones
        // STRADDLING a source boundary -- (1,0) and (2,0) are the last red and the first blue
        // column. Any smooth filter blends exactly there; nearest leaves them pure.
        Assert.Equal((255, 0, 0, 255), PixelAt(data!, x: 0, y: 0));
        Assert.Equal((255, 0, 0, 255), PixelAt(data, x: 1, y: 0));
        Assert.Equal((0, 0, 255, 255), PixelAt(data, x: 2, y: 0));
        Assert.Equal((0, 0, 255, 255), PixelAt(data, x: 3, y: 0));

        // Same boundary on the vertical axis, so a filter differing only in one direction cannot
        // slip through.
        Assert.Equal((0, 255, 0, 255), PixelAt(data, x: 0, y: 2));
        Assert.Equal((255, 255, 255, 255), PixelAt(data, x: 3, y: 3));
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
    /// <para>Asserts on the refusal COUNTER rather than the null return, because a null is what
    /// comes back from an ordinary failed connection too — "refused on purpose" versus "nothing
    /// was listening" is the distinction under test. An earlier version captured
    /// <c>Console.Error</c> and matched the log line; that was flaky, because Console.Error is
    /// process-global and xUnit runs test classes in parallel, so a concurrent class can swap it
    /// out between the redirect and the write. It passed locally and failed in CI, which is the
    /// usual shape of that mistake.</para>
    ///
    /// <para>Still no real network call: the address is judged and rejected before a socket is
    /// opened.</para></summary>
    [Fact]
    public async Task FetchAsync_LoopbackHost_IsRefusedByThePolicyRatherThanAttempted()
    {
        int before = MediaImageService.PrivateAddressRefusals;

        // Port 9 (discard) and a unique path, so no cached result can answer this.
        var data = await MediaImageService.FetchAsync($"http://127.0.0.1:9/{Guid.NewGuid():N}.png");

        Assert.Null(data);
        Assert.True(MediaImageService.PrivateAddressRefusals > before,
            "the connect callback should have refused the loopback address");
    }

    /// <summary>The same guard reached through a NAME rather than a literal address — the case a
    /// URL-string check cannot catch, since nothing about "localhost" looks like an IP.</summary>
    [Fact]
    public async Task FetchAsync_HostnameResolvingToLoopback_IsAlsoRefused()
    {
        int before = MediaImageService.PrivateAddressRefusals;

        var data = await MediaImageService.FetchAsync($"http://localhost:9/{Guid.NewGuid():N}.png");

        Assert.Null(data);
        Assert.True(MediaImageService.PrivateAddressRefusals > before,
            "localhost resolves only to loopback and should have been refused");
    }
}
