using System.Net;
using System.Net.Http;
using SkiaSharp;
using SLNG.Assets;
using Xunit;

namespace SLNG.Assets.Tests;

/// <summary>MapTileService: URL building, the disk cache's freshness rules, the failure backoff,
/// request de-duplication and the size/decode guard rails. Every test runs against a fake
/// <see cref="HttpMessageHandler"/> and a private temp directory -- a map server is a real
/// network host and has no place in a unit test -- and drives the two timers through the injected
/// clock instead of sleeping.</summary>
public sealed class MapTileServiceTests : IDisposable
{
    private const string Base = "http://map.example.test/";
    private const long FourMegabytes = 4 * 1024 * 1024;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "slng-maptile-" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is not a test failure.
        }
    }

    private MapTileService NewService(FakeHandler handler) =>
        new(_dir, handler, utcNow: () => _now);

    /// <summary>A small but real JPEG, generated rather than committed so there is no binary
    /// fixture to go stale. 16x8 on purpose: non-square, so a swapped width/height shows up.</summary>
    private static byte[] MakeJpeg(SKColor color, int width = 16, int height = 8)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 95);
        return encoded.ToArray();
    }

    /// <summary>Where the service is documented to keep a tile: host folder, then the viewer's
    /// own file name.</summary>
    private string CachePath(string hostFolder, int x, int y) =>
        Path.Combine(_dir, hostFolder, $"map-1-{x}-{y}-objects.jpg");

    private void SeedCache(string hostFolder, int x, int y, byte[] bytes, DateTime writtenUtc)
    {
        string path = CachePath(hostFolder, x, y);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, writtenUtc);
    }

    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(body),
    };

    // --- BuildTileUrl -------------------------------------------------------------------

    [Fact]
    public void BuildTileUrl_UsesTheViewersFileName()
    {
        Assert.Equal(
            "https://map.example.test/map-1-1000-1001-objects.jpg",
            MapTileService.BuildTileUrl("https://map.example.test/", 1000, 1001));
    }

    [Fact]
    public void BuildTileUrl_AddsAMissingTrailingSlash_AndKeepsAPath()
    {
        Assert.Equal(
            "http://map.example.test/tiles/map-1-7-9-objects.jpg",
            MapTileService.BuildTileUrl("http://map.example.test/tiles", 7, 9));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://map.example.test/")]
    [InlineData("file:///etc/")]
    [InlineData("/relative/path/")]
    [InlineData("map.example.test/")]
    [InlineData("not a url")]
    public void BuildTileUrl_RejectsBlankNonHttpAndRelative(string baseUrl)
    {
        Assert.Null(MapTileService.BuildTileUrl(baseUrl, 1000, 1000));
    }

    [Fact]
    public async Task GetTileAsync_InvalidBase_ReturnsNullWithoutAnyRequest()
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(Ok(MakeJpeg(SKColors.Red))));
        var service = NewService(handler);

        Assert.Null(await service.GetTileAsync("ftp://map.example.test/", 1000, 1000));
        Assert.Null(await service.GetTileAsync("", 1000, 1000));

        Assert.Equal(0, handler.RequestCount);
    }

    // --- cache --------------------------------------------------------------------------

    [Fact]
    public async Task FreshCacheHit_MakesNoRequest()
    {
        SeedCache("map.example.test", 1000, 1000, MakeJpeg(SKColors.Red), _now - TimeSpan.FromHours(11));
        var handler = new FakeHandler((_, _) => throw new InvalidOperationException("network must not be touched"));
        var service = NewService(handler);

        var tile = await service.GetTileAsync(Base, 1000, 1000);

        Assert.NotNull(tile);
        Assert.Equal(16, tile!.Width);
        Assert.Equal(8, tile.Height);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task FirstFetch_WritesTheCacheFile_AndReturnsADecodedRgbaTile()
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(Ok(MakeJpeg(SKColors.Red))));
        var service = NewService(handler);

        var tile = await service.GetTileAsync(Base, 1000, 1001);

        Assert.NotNull(tile);
        Assert.Equal(16, tile!.Width);
        Assert.Equal(8, tile.Height);
        Assert.Equal(16 * 8 * 4, tile.Rgba.Length);

        // Row-major RGBA8 of a solid red JPEG: red dominant, opaque.
        Assert.True(tile.Rgba[0] > 200, $"R was {tile.Rgba[0]}");
        Assert.True(tile.Rgba[1] < 60, $"G was {tile.Rgba[1]}");
        Assert.True(tile.Rgba[2] < 60, $"B was {tile.Rgba[2]}");
        Assert.Equal(255, tile.Rgba[3]);

        Assert.Equal("http://map.example.test/map-1-1000-1001-objects.jpg", Assert.Single(handler.Requests).ToString());

        // Exactly the one file: the atomic write must not leave its temp file behind.
        string[] files = Directory.GetFiles(Path.Combine(_dir, "map.example.test"));
        Assert.Equal(CachePath("map.example.test", 1000, 1001), Assert.Single(files));
    }

    [Fact]
    public async Task SecondCall_IsServedFromTheCacheItJustWrote()
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(Ok(MakeJpeg(SKColors.Red))));
        var service = NewService(handler);

        await service.GetTileAsync(Base, 1000, 1000);
        var again = await service.GetTileAsync(Base, 1000, 1000);

        Assert.NotNull(again);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task StaleCacheIsRefetched_AndReplaced()
    {
        SeedCache("map.example.test", 1000, 1000, MakeJpeg(SKColors.Red, 16, 8), _now - TimeSpan.FromHours(13));
        var handler = new FakeHandler((_, _) => Task.FromResult(Ok(MakeJpeg(SKColors.Blue, 32, 32))));
        var service = NewService(handler);

        var tile = await service.GetTileAsync(Base, 1000, 1000);

        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(32, tile!.Width);

        // The refreshed file is stamped with the service clock, so it is fresh again right away.
        Assert.Equal(_now, File.GetLastWriteTimeUtc(CachePath("map.example.test", 1000, 1000)));
        await service.GetTileAsync(Base, 1000, 1000);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task StaleCache_WithAFailingFetch_ReturnsTheStaleTile()
    {
        SeedCache("map.example.test", 1000, 1000, MakeJpeg(SKColors.Red), _now - TimeSpan.FromHours(13));
        var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var service = NewService(handler);

        var tile = await service.GetTileAsync(Base, 1000, 1000);

        Assert.NotNull(tile);
        Assert.Equal(16, tile!.Width);
        Assert.Equal(8, tile.Height);
        Assert.Equal(1, handler.RequestCount);

        // Inside the backoff the stale tile keeps being served, with no further request, and the
        // file is kept so it still works once the backoff has passed.
        Assert.NotNull(await service.GetTileAsync(Base, 1000, 1000));
        Assert.Equal(1, handler.RequestCount);
        Assert.True(File.Exists(CachePath("map.example.test", 1000, 1000)));
    }

    [Fact]
    public async Task StaleCache_WithATransportException_ReturnsTheStaleTile()
    {
        SeedCache("map.example.test", 1000, 1000, MakeJpeg(SKColors.Red), _now - TimeSpan.FromDays(3));
        var handler = new FakeHandler((_, _) => throw new HttpRequestException("connection refused"));
        var service = NewService(handler);

        Assert.NotNull(await service.GetTileAsync(Base, 1000, 1000));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task CorruptCachedFile_IsDeleted_AndTreatedAsMissing()
    {
        SeedCache("map.example.test", 1000, 1000, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, _now);
        var handler = new FakeHandler((_, _) => Task.FromResult(Ok(MakeJpeg(SKColors.Lime, 24, 12))));
        var service = NewService(handler);

        var tile = await service.GetTileAsync(Base, 1000, 1000);

        // The corrupt file looked fresh, but it was not trusted: the tile came from the network
        // and the file now on disk is the good one.
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(24, tile!.Width);
        Assert.NotNull(SKBitmap.Decode(File.ReadAllBytes(CachePath("map.example.test", 1000, 1000))));
    }

    [Fact]
    public async Task CorruptCachedFile_WithAFailingFetch_ReturnsNull_AndIsDeleted()
    {
        SeedCache("map.example.test", 1000, 1000, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, _now - TimeSpan.FromDays(1));
        var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        var service = NewService(handler);

        Assert.Null(await service.GetTileAsync(Base, 1000, 1000));

        Assert.False(File.Exists(CachePath("map.example.test", 1000, 1000)));
    }

    [Fact]
    public async Task DifferentHosts_UseDifferentCacheFolders()
    {
        var handler = new FakeHandler((req, _) => Task.FromResult(Ok(MakeJpeg(
            req.RequestUri!.Host == "grid-a.example.test" ? SKColors.Red : SKColors.Blue,
            req.RequestUri.Host == "grid-a.example.test" ? 16 : 40, 8))));
        var service = NewService(handler);

        var a = await service.GetTileAsync("http://grid-a.example.test/", 1000, 1000);
        var b = await service.GetTileAsync("http://grid-b.example.test/", 1000, 1000);

        // Same coordinates, two grids: two requests (grid B did not get grid A's tile) and two
        // separate files.
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(16, a!.Width);
        Assert.Equal(40, b!.Width);
        Assert.True(File.Exists(CachePath("grid-a.example.test", 1000, 1000)));
        Assert.True(File.Exists(CachePath("grid-b.example.test", 1000, 1000)));
    }

    [Fact]
    public async Task NonDefaultPort_IsPartOfTheCacheFolder()
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(Ok(MakeJpeg(SKColors.Red))));
        var service = NewService(handler);

        await service.GetTileAsync("http://127.0.0.1:9000/", 1000, 1000);

        Assert.True(File.Exists(CachePath("127.0.0.1_9000", 1000, 1000)));
    }

    // --- failure backoff ----------------------------------------------------------------

    [Fact]
    public async Task NotFound_WithNoCache_ReturnsNull_ThenIsNotAskedAgainUntilTheBackoffPasses()
    {
        bool serverHasTile = false;
        var handler = new FakeHandler((_, _) => Task.FromResult(serverHasTile
            ? Ok(MakeJpeg(SKColors.Red))
            : new HttpResponseMessage(HttpStatusCode.NotFound)));
        var service = NewService(handler);

        Assert.Null(await service.GetTileAsync(Base, 1000, 1000));
        Assert.Equal(1, handler.RequestCount);

        // Inside the 2 minute backoff: no request, even though the server could answer now.
        serverHasTile = true;
        _now += TimeSpan.FromSeconds(119);
        Assert.Null(await service.GetTileAsync(Base, 1000, 1000));
        Assert.Equal(1, handler.RequestCount);

        // Past it: asked again, and the tile now loads.
        _now += TimeSpan.FromSeconds(2);
        var tile = await service.GetTileAsync(Base, 1000, 1000);
        Assert.NotNull(tile);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Backoff_IsPerTile()
    {
        var handler = new FakeHandler((req, _) => Task.FromResult(req.RequestUri!.AbsolutePath.Contains("-1000-1000-")
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : Ok(MakeJpeg(SKColors.Red))));
        var service = NewService(handler);

        Assert.Null(await service.GetTileAsync(Base, 1000, 1000));
        Assert.NotNull(await service.GetTileAsync(Base, 1001, 1000));
        Assert.Equal(2, handler.RequestCount);
    }

    // --- request sharing and cancellation -----------------------------------------------

    [Fact]
    public async Task TwoConcurrentCalls_ShareOneRequest()
    {
        var gate = new TaskCompletionSource();
        var handler = new FakeHandler(async (_, _) =>
        {
            await gate.Task;
            return Ok(MakeJpeg(SKColors.Red));
        });
        var service = NewService(handler);

        var first = service.GetTileAsync(Base, 1000, 1000);
        await WaitUntil(() => handler.RequestCount == 1);

        // The first request is parked inside the handler, so this one finds it in flight.
        var second = service.GetTileAsync(Base, 1000, 1000);
        gate.SetResult();

        var tiles = await Task.WhenAll(first, second);

        Assert.Equal(1, handler.RequestCount);
        Assert.NotNull(tiles[0]);
        Assert.NotNull(tiles[1]);
        Assert.Equal(16, tiles[0]!.Width);
        Assert.Equal(16, tiles[1]!.Width);
    }

    [Fact]
    public async Task ACancelledToken_Throws_AndStartsNoRequest()
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(Ok(MakeJpeg(SKColors.Red))));
        var service = NewService(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.GetTileAsync(Base, 1000, 1000, cts.Token));

        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task CancellingOneCaller_DoesNotKillTheSharedRequest()
    {
        var gate = new TaskCompletionSource();
        var handler = new FakeHandler(async (_, _) =>
        {
            await gate.Task;
            return Ok(MakeJpeg(SKColors.Red));
        });
        var service = NewService(handler);
        using var cts = new CancellationTokenSource();

        var cancelled = service.GetTileAsync(Base, 1000, 1000, cts.Token);
        await WaitUntil(() => handler.RequestCount == 1);
        var patient = service.GetTileAsync(Base, 1000, 1000);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        gate.SetResult();
        var tile = await patient;

        Assert.NotNull(tile);
        Assert.Equal(1, handler.RequestCount);
    }

    // --- guard rails --------------------------------------------------------------------

    [Fact]
    public async Task BodyWithADeclaredLengthOverTheCap_IsRejectedWithoutReadingIt()
    {
        // An endless body behind a length header over the cap: if the service read it at all
        // this would run until the 10 s timeout instead of returning immediately.
        var handler = new FakeHandler((_, _) =>
        {
            var content = new StreamContent(new EndlessStream());
            content.Headers.ContentLength = FourMegabytes + 1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        var service = NewService(handler);

        Assert.Null(await service.GetTileAsync(Base, 1000, 1000));
        Assert.False(File.Exists(CachePath("map.example.test", 1000, 1000)));
    }

    [Fact]
    public async Task BodyWithNoDeclaredLength_IsCutOffAtTheCapWhileStreaming()
    {
        var body = new EndlessStream();
        var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(body),
        }));
        var service = NewService(handler);

        Assert.Null(await service.GetTileAsync(Base, 1000, 1000));

        // It stopped shortly after the cap instead of draining an infinite stream into memory.
        Assert.True(body.BytesServed <= FourMegabytes + 64 * 1024, $"served {body.BytesServed} bytes");
        Assert.False(File.Exists(CachePath("map.example.test", 1000, 1000)));
    }

    [Fact]
    public async Task GarbageBytes_AreRejected_AndNotCached()
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(Ok(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 1, 2, 3 })));
        var service = NewService(handler);

        Assert.Null(await service.GetTileAsync(Base, 1000, 1000));

        Assert.False(File.Exists(CachePath("map.example.test", 1000, 1000)));
        Assert.False(Directory.Exists(Path.Combine(_dir, "map.example.test"))
            && Directory.GetFiles(Path.Combine(_dir, "map.example.test")).Length > 0);

        // And it counts as a failure: a server that keeps answering with junk is not hammered.
        Assert.Null(await service.GetTileAsync(Base, 1000, 1000));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task EmptyBody_IsRejected()
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(Ok(Array.Empty<byte>())));
        var service = NewService(handler);

        Assert.Null(await service.GetTileAsync(Base, 1000, 1000));
    }

    [Fact]
    public async Task Request_CarriesNoCredentials()
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(Ok(MakeJpeg(SKColors.Red))));
        var service = NewService(handler);

        await service.GetTileAsync(Base, 1000, 1000);

        var request = Assert.Single(handler.RequestMessages);
        Assert.Null(request.Headers.Authorization);
        Assert.False(request.Headers.Contains("Cookie"));
        Assert.NotEmpty(request.Headers.UserAgent);
    }

    [Fact]
    public async Task LoopbackMapServer_IsAllowed()
    {
        // The reverse of MediaImageService's private-address guard: a local OpenSim serves its
        // map from 127.0.0.1 and must work.
        var handler = new FakeHandler((_, _) => Task.FromResult(Ok(MakeJpeg(SKColors.Red))));
        var service = NewService(handler);

        Assert.NotNull(await service.GetTileAsync("http://127.0.0.1:9000/", 1000, 1000));
        Assert.Equal(1, handler.RequestCount);
    }

    // --- helpers ------------------------------------------------------------------------

    /// <summary>Polls with a timeout rather than sleeping a guessed interval.</summary>
    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for the request to reach the handler");
            await Task.Delay(5);
        }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
        private readonly List<HttpRequestMessage> _messages = new();
        private int _count;

        public FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
            _respond = respond;

        public int RequestCount => Volatile.Read(ref _count);

        public IReadOnlyList<Uri> Requests
        {
            get
            {
                lock (_messages) return _messages.Select(m => m.RequestUri!).ToList();
            }
        }

        public IReadOnlyList<HttpRequestMessage> RequestMessages
        {
            get
            {
                lock (_messages) return _messages.ToList();
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_messages) _messages.Add(request);
            Interlocked.Increment(ref _count);
            return _respond(request, cancellationToken);
        }
    }

    /// <summary>A body that never ends, and cannot report a length. Stands in for a hostile or
    /// broken server; a service that tried to buffer it whole would hang or exhaust memory.</summary>
    private sealed class EndlessStream : Stream
    {
        private long _served;

        public long BytesServed => Interlocked.Read(ref _served);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, count);
            Interlocked.Add(ref _served, count);
            return count;
        }

        public override int Read(Span<byte> buffer)
        {
            buffer.Clear();
            Interlocked.Add(ref _served, buffer.Length);
            return buffer.Length;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            buffer.Span.Clear();
            Interlocked.Add(ref _served, buffer.Length);
            return new ValueTask<int>(buffer.Length);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
