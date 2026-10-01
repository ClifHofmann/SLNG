using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;

namespace SLNG.Assets;

/// <summary>Fetches a region's world-map tile and decodes it into the same neutral
/// <see cref="TextureData"/> every other texture path returns.
///
/// <para>A map tile is NOT a grid asset: it is a plain JPEG served over ordinary HTTP from the
/// map server the grid names in its login response, so it has nothing to do with the GetTexture
/// capability or JPEG2000. The file name is fixed by the reference viewer
/// (<c>LLWorldMipmap::loadObjectsTile</c>, llworldmipmap.cpp: <c>map-{level}-{x}-{y}-objects.jpg</c>);
/// level 1 is the full-resolution one-region tile.</para>
///
/// <para>Uses its own bare <see cref="HttpClient"/> -- never LibreMetaverse's, which would hand
/// the map server the session's caps URL and cookies for no reason. Unlike
/// <see cref="MediaImageService"/> it deliberately does NOT refuse private addresses: that guard
/// exists because a media URL is arbitrary in-world content, whereas the map server URL comes from
/// the grid's own login response (the one source this viewer already has to trust to log in at
/// all), and a local OpenSim serving its map from 127.0.0.1 is the default test setup.</para>
///
/// <para>Two layers keep a busy world map cheap. A disk cache, keyed by map server host so two
/// grids never share a tile, means reopening the map costs no network at all while a tile is
/// young. A short in-memory failure memory stops the ocean -- where every tile is a 404 -- from
/// being re-requested on every redraw. Concurrent requests for one tile share a single flight.
/// All IO and decoding run on the thread pool: callers are on Godot's main thread and must never
/// wait on this.</para></summary>
public sealed class MapTileService
{
    /// <summary>A real one-region tile is a few tens of KB; this only has to stop a broken or
    /// hostile server from streaming the process out of memory.</summary>
    private const long MaxTileBytes = 4 * 1024 * 1024;

    /// <summary>The world map asks for many tiles at once; without a bound each would open its own
    /// connection to the map server. Excess requests queue here (asynchronously, no thread held).</summary>
    private const int MaxConcurrentFetches = 8;

    /// <summary>Soft bound on the remembered failures. Entries expire on their own after
    /// <c>failureBackoff</c>; this only triggers a sweep of the expired ones so a long session
    /// panning across an ocean does not accumulate them forever.</summary>
    private const int FailureSweepThreshold = 4096;

    private const string UserAgent = "SLNG/1.0 (+https://github.com/ClifHofmann/SLNG)";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultMaxCacheAge = TimeSpan.FromHours(12);
    private static readonly TimeSpan DefaultFailureBackoff = TimeSpan.FromMinutes(2);

    /// <summary>A cached file stamped slightly in the future (the clock was stepped back) is still
    /// fresh; one stamped far in the future is not trusted, or it would stay "fresh" until real
    /// time caught up with it.</summary>
    private static readonly TimeSpan ClockSkewTolerance = TimeSpan.FromMinutes(5);

    private readonly string _cacheDirectory;
    private readonly TimeSpan _maxCacheAge;
    private readonly TimeSpan _failureBackoff;
    private readonly Func<DateTime> _utcNow;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _fetchGate = new(MaxConcurrentFetches);

    /// <summary>Tile URL -> instant before which it is not worth asking again.</summary>
    private readonly ConcurrentDictionary<string, DateTime> _failedUntil = new();

    /// <summary>One shared flight per tile URL. Lazy so a lost GetOrAdd race never starts a second
    /// request; entries are removed on completion -- this dedupes, it does not cache.</summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<TextureData?>>> _inFlight = new();

    /// <param name="cacheDirectory">Root of the on-disk tile cache; created lazily on first write.</param>
    /// <param name="handler">Test seam for the transport. Null builds the real one.</param>
    /// <param name="maxCacheAge">How long a cached tile is served without asking the map server
    /// again. A map tile changes only when the region's terrain or objects do, and the grid itself
    /// regenerates them slowly, so half a day is fresh enough. Default 12 h.</param>
    /// <param name="failureBackoff">How long a failed tile is left alone. Default 2 min.</param>
    /// <param name="utcNow">Test seam for the two timers above. Also stamps the cache files, so a
    /// fake clock and the file ages stay consistent.</param>
    public MapTileService(string cacheDirectory, HttpMessageHandler? handler = null,
        TimeSpan? maxCacheAge = null, TimeSpan? failureBackoff = null, Func<DateTime>? utcNow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        _cacheDirectory = cacheDirectory;
        _maxCacheAge = maxCacheAge ?? DefaultMaxCacheAge;
        _failureBackoff = failureBackoff ?? DefaultFailureBackoff;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _http = BuildHttpClient(handler);
    }

    private static HttpClient BuildHttpClient(HttpMessageHandler? handler)
    {
        handler ??= new SocketsHttpHandler
        {
            // The map server is not the grid and must never see a session cookie.
            UseCookies = false,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.All,
        };

        // No HttpClient.Timeout: it does not cover reading the body when headers are read first,
        // so the 10 s limit is enforced per request by a CancellationTokenSource instead.
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        // Same reasoning as MediaImageService: some CDNs reject a request with no User-Agent, and
        // the one .NET sends by default is empty. Plain and honest, nothing session-specific.
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    /// <summary>The URL of the full-resolution tile for the region at (<paramref name="gridX"/>,
    /// <paramref name="gridY"/>), or null when <paramref name="mapServerBaseUrl"/> is blank or not
    /// an absolute http/https URL. <paramref name="gridX"/>/<paramref name="gridY"/> are region-grid
    /// coordinates (the region handle's meters divided by 256), not meters. A missing trailing
    /// slash is added because the login response is not consistent about it.</summary>
    public static string? BuildTileUrl(string mapServerBaseUrl, int gridX, int gridY)
    {
        if (string.IsNullOrWhiteSpace(mapServerBaseUrl)) return null;

        string baseUrl = mapServerBaseUrl.Trim();
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return null;

        if (!baseUrl.EndsWith('/')) baseUrl += '/';
        return baseUrl + TileFileName(gridX, gridY);
    }

    /// <summary>Invariant culture on purpose: a negative number must never pick up a locale's
    /// minus sign and end up in a URL or a file name.</summary>
    private static string TileFileName(int gridX, int gridY) =>
        string.Create(CultureInfo.InvariantCulture, $"map-1-{gridX}-{gridY}-objects.jpg");

    /// <summary>Returns the decoded tile, or null when there is none to show (no tile exists for
    /// that region, the server is unreachable, the bytes are not an image). Never throws apart from
    /// <see cref="OperationCanceledException"/> when <paramref name="ct"/> is cancelled -- an
    /// ocean tile missing is the normal case, not an error.
    ///
    /// <para>The shared request is deliberately not tied to any one caller's token: another caller
    /// may be waiting on the same tile, and the result lands in the disk cache either way.
    /// Cancelling only abandons THIS caller's wait. Callers that share a flight share the returned
    /// <see cref="TextureData"/> instance, so it must be treated as read-only.</para></summary>
    public async Task<TextureData?> GetTileAsync(string mapServerBaseUrl, int gridX, int gridY,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        string? url = BuildTileUrl(mapServerBaseUrl, gridX, gridY);
        if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;

        string cachePath = Path.Combine(_cacheDirectory, HostFolderName(uri), TileFileName(gridX, gridY));

        // Everything up to here is string work. The Task.Run below moves the disk, network and
        // decode off the caller's thread, which is Godot's main thread.
        Task<TextureData?> flight = _inFlight.GetOrAdd(
            url,
            static (key, state) => new Lazy<Task<TextureData?>>(
                () => Task.Run(() => state.Service.LoadAsync(key, state.Uri, state.CachePath))),
            (Service: this, Uri: uri, CachePath: cachePath)).Value;

        return await flight.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The cache subfolder for a map server: its host (and port when not the scheme
    /// default, so two OpenSim instances on one machine stay apart), reduced to characters that
    /// are legal in a file name on every platform. IdnHost keeps it ASCII.</summary>
    private static string HostFolderName(Uri uri)
    {
        var name = new System.Text.StringBuilder();
        foreach (char c in uri.IdnHost)
            name.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? char.ToLowerInvariant(c) : '_');

        if (!uri.IsDefaultPort) name.Append('_').Append(uri.Port.ToString(CultureInfo.InvariantCulture));

        // "." and ".." are legal host-ish strings to a URI parser but mean something to a file
        // system; never let the host pick the folder out of the cache root.
        string result = name.ToString();
        return result.Trim('.').Length == 0 ? "_" + result.Replace('.', '_') : result;
    }

    private async Task<TextureData?> LoadAsync(string url, Uri uri, string cachePath)
    {
        byte[]? cached = null;
        try
        {
            DateTime writtenUtc;
            (cached, writtenUtc) = await ReadCacheAsync(cachePath).ConfigureAwait(false);

            if (cached != null && IsFresh(writtenUtc, _utcNow()))
            {
                TextureData? hit = Decode(cached);
                if (hit != null) return hit;

                // A truncated or overwritten file would otherwise be re-read, re-decoded and
                // re-rejected on every call until it aged out.
                DeleteQuietly(cachePath);
                cached = null;
            }

            if (IsBackedOff(url)) return FallBackToStale(cached, cachePath);

            byte[]? body = null;
            TextureData? tile = null;
            try
            {
                body = await DownloadAsync(uri).ConfigureAwait(false);
                if (body != null) tile = Decode(body);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
            {
                // Routine: unreachable host, refused connection, TLS failure, our own 10 s timeout.
                // Not worth a line in a console that is deliberately kept quiet.
            }

            if (tile == null)
            {
                RememberFailure(url);
                return FallBackToStale(cached, cachePath);
            }

            _failedUntil.TryRemove(url, out _);

            // Only ever reached with bytes that decoded, so garbage from a misbehaving server is
            // never persisted (and then served for 12 hours).
            await TryWriteCacheAsync(cachePath, body!).ConfigureAwait(false);
            return tile;
        }
        catch (Exception ex)
        {
            // Genuinely unexpected: everything routine is handled above. The contract is "never
            // throws", so the tile is simply absent -- but this one is worth a line.
            Console.Error.WriteLine($"[MapTile] {url}: {ex.GetType().Name}: {ex.Message}");
            RememberFailure(url);
            return FallBackToStale(cached, cachePath);
        }
        finally
        {
            _inFlight.TryRemove(url, out _);
        }
    }

    private bool IsFresh(DateTime writtenUtc, DateTime now)
    {
        TimeSpan age = now - writtenUtc;
        return age < _maxCacheAge && age > -ClockSkewTolerance;
    }

    private bool IsBackedOff(string url) =>
        _failedUntil.TryGetValue(url, out DateTime until) && _utcNow() < until;

    private void RememberFailure(string url)
    {
        DateTime now = _utcNow();
        _failedUntil[url] = now + _failureBackoff;

        if (_failedUntil.Count <= FailureSweepThreshold) return;
        foreach (var entry in _failedUntil)
        {
            if (entry.Value <= now) _failedUntil.TryRemove(entry.Key, out _);
        }
    }

    /// <summary>The "old tile is better than a blank one" fallback: a map that goes dark the
    /// moment the map server hiccups is worse than one a few hours out of date. Decoded lazily,
    /// only when actually needed, because the common path (the fetch succeeds) never wants it.</summary>
    private static TextureData? FallBackToStale(byte[]? cached, string cachePath)
    {
        if (cached == null) return null;

        TextureData? tile = Decode(cached);
        if (tile == null) DeleteQuietly(cachePath);
        return tile;
    }

    private static TextureData? Decode(byte[] bytes) => MediaImageService.Decode(bytes);

    private static async Task<(byte[]? Bytes, DateTime WrittenUtc)> ReadCacheAsync(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return (null, default);

            // We only ever write files that decoded and were under the cap, so anything else is
            // damage (disk full mid-write, a stray edit) rather than a tile.
            if (info.Length is 0 or > MaxTileBytes)
            {
                DeleteQuietly(path);
                return (null, default);
            }

            DateTime writtenUtc = info.LastWriteTimeUtc;
            return (await File.ReadAllBytesAsync(path).ConfigureAwait(false), writtenUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, default);
        }
    }

    /// <summary>Temp file plus move, so a crash or a concurrent reader never sees half a JPEG.
    /// The file is stamped with the service clock rather than left to the file system, which keeps
    /// the age check and the injected clock talking about the same time. A failed write only costs
    /// the next call a refetch, so it is swallowed.</summary>
    private async Task TryWriteCacheAsync(string path, byte[] bytes)
    {
        string temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(temp, bytes).ConfigureAwait(false);
            File.SetLastWriteTimeUtc(temp, _utcNow());
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DeleteQuietly(temp);
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a file we cannot delete is simply retried (and rejected) next time.
        }
    }

    /// <summary>Downloads the tile body, or null when the server answered anything but a
    /// success or the body is over <see cref="MaxTileBytes"/>. The cap is checked against the
    /// declared Content-Length first (rejecting before reading a byte) and again while reading,
    /// because a server can lie, or send chunked with no length at all. Transport failures
    /// surface as exceptions for the caller to treat as routine.</summary>
    private async Task<byte[]?> DownloadAsync(Uri uri)
    {
        await _fetchGate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cts = new CancellationTokenSource(RequestTimeout);
            using var response = await _http
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            long? declared = response.Content.Headers.ContentLength;
            if (declared is > MaxTileBytes) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            using var body = new MemoryStream(declared is > 0 ? (int)declared.Value : 64 * 1024);

            // Pooled: a world map pulls in hundreds of tiles and each needs a read buffer.
            byte[] buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
            try
            {
                int read;
                while ((read = await stream.ReadAsync(buffer, cts.Token).ConfigureAwait(false)) > 0)
                {
                    if (body.Length + read > MaxTileBytes) return null;
                    body.Write(buffer, 0, read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return body.ToArray();
        }
        finally
        {
            _fetchGate.Release();
        }
    }
}
