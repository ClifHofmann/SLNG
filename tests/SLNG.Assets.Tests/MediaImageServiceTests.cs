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
}
