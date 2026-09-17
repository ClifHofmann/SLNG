using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Microsoft.Extensions.Caching.Memory;
using SkiaSharp;

namespace SLNG.Assets;

/// <summary>MVP3-3 Phase 3: fetches a MOAP face's <c>CurrentUrl</c> and decodes it into the same
/// neutral <see cref="TextureData"/> every other texture path returns, when (and only when) the
/// response is a direct image (vendor boards, gallery prims, webcam stills — the common in-world
/// case a full embedded browser is not needed for).
///
/// <para>Uses its own bare <see cref="HttpClient"/> -- <b>never</b> LibreMetaverse's
/// <c>HttpCapsClient</c>. A third-party media host must never receive the session's caps URL,
/// agent id, or cookies; the whole point of this request looking like an ordinary anonymous
/// browser GET is that it IS one.</para>
///
/// <para>Cached by URL for the process lifetime -- a media face's material gets rebuilt on
/// ordinary scene churn (edits, LOD changes, re-selection) far more often than its actual media
/// changes, and re-fetching an unrelated host's image on every rebuild would be both wasteful and
/// exactly the repeated-request pattern BUG-NET-11 already burned this project on once.</para></summary>
public static class MediaImageService
{
    /// <summary>Generous for a vendor-board/gallery image, nowhere near enough for a real video
    /// frame dump or a hostile server trying to exhaust memory. PRIM_MEDIA_MAX_WIDTH_PIXELS/
    /// MAX_HEIGHT_PIXELS cap the DISPLAY size at 2048x2048 (llmediaentry.h:184-188); this caps the
    /// download itself, which for a plain image is normally far smaller than that would imply.</summary>
    private const long MaxContentBytes = 8 * 1024 * 1024;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private static readonly HttpClient HttpClient = BuildHttpClient();

    private static HttpClient BuildHttpClient()
    {
        // FEAT-SEC-01: the URL comes from in-world content, so the viewer must not be talked into
        // reaching its own machine or LAN -- a router admin page, a local dev server, a cloud
        // instance-metadata endpoint. The check lives in ConnectCallback rather than in front of
        // the request for two reasons that a URL-level check cannot cover:
        //
        //   * it runs for EVERY connection the handler makes, redirect targets included, so a
        //     public URL that 302s to 169.254.169.254 is refused at the second hop;
        //   * it resolves the host itself and then connects to one of the addresses it just
        //     checked, closing the gap where a name is resolved for the check and resolved again
        //     -- to a different address -- for the connection.
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = ConnectToPublicAddressAsync,
            // A media host that redirects more than a couple of times is not serving a still
            // image; the default 50 is a lot of connections to make on a prim's say-so.
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };

        var client = new HttpClient(handler) { Timeout = RequestTimeout };
        // Live-tested against a real MOAP probe (2026-09-17): a Wikimedia Commons URL answered
        // every request with a network-level failure until this was added. Several CDNs --
        // Wikimedia's among the better-documented ones -- reject or rate-limit requests that carry
        // no User-Agent at all, which .NET's HttpClient sends by default; a real-looking one (not
        // an obviously-scripted "curl"/empty string) is standard practice for any client fetching
        // arbitrary third-party URLs, not something specific to Wikimedia.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SLNG/1.0 (+https://github.com/ClifHofmann/SLNG)");
        return client;
    }

    /// <summary>Resolves the target host and connects only to a public address, refusing the
    /// whole request when every address it resolves to is private or reserved. See
    /// <see cref="SLNG.Core.PrivateAddressPolicy"/> for what counts and why the test is on the
    /// address rather than the name.</summary>
    private static async ValueTask<Stream> ConnectToPublicAddressAsync(
        SocketsHttpConnectionContext context, CancellationToken token)
    {
        string host = context.DnsEndPoint.Host;
        int port = context.DnsEndPoint.Port;

        var resolved = await Dns.GetHostAddressesAsync(host, token).ConfigureAwait(false);
        var permitted = resolved.Where(a => !SLNG.Core.PrivateAddressPolicy.IsPrivateOrReserved(a)).ToArray();

        if (permitted.Length == 0)
        {
            // Counted, not just logged. A test proving the guard is WIRED IN has to observe it
            // somehow, and asserting on captured Console.Error turned out to be flaky under
            // xUnit's parallel runner -- Console.Error is process-global, so a concurrent class
            // can swap it out between the redirect and the write. Caught by CI, green locally,
            // which is the usual shape of that mistake. An interlocked counter is observable
            // without touching global state.
            System.Threading.Interlocked.Increment(ref _privateAddressRefusals);

            // Logged with the addresses, because "media did not load" and "media was refused on
            // purpose" have to be distinguishable -- and a prim aimed at the LAN is worth seeing.
            Console.Error.WriteLine(
                $"[MediaImage] {host}:{port}: refusing -- resolves only to private or reserved " +
                $"addresses ({string.Join(", ", resolved.Select(a => a.ToString()))})");
            throw new HttpRequestException(
                $"refusing to connect to {host}: private or reserved address");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            // Connects to one of the addresses just vetted, not to a fresh resolution of the name.
            await socket.ConnectAsync(permitted, port, token).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>FEAT-SEC-03: decoded results, bounded. This was a <c>ConcurrentDictionary</c> that
    /// was never cleared, keyed by URL and holding a full RGBA buffer — at the 2048x2048 display
    /// cap that is 16 MB an entry, and a prim whose URL carries a changing query string
    /// (<c>?t=1234</c>) would have filled the process with them, without anyone touching
    /// anything. Same <c>MemoryCache</c>-with-a-size-limit shape <c>AssetService</c> already uses
    /// for textures, just far smaller: media faces are rare next to textured ones.</summary>
    private static readonly MemoryCache Decoded = new(new MemoryCacheOptions { SizeLimit = 32 * 1024 * 1024 });

    /// <summary>Failures, remembered briefly. The old dictionary cached the <c>null</c> too, for
    /// the life of the process, so a face whose host happened to be down at the moment it first
    /// came into view never loaded again until a restart. A minute is long enough to stop a dead
    /// URL being retried on every material rebuild and short enough that a transient outage
    /// heals itself.</summary>
    private static readonly MemoryCache Failures = new(new MemoryCacheOptions { SizeLimit = 4096 });

    private static readonly TimeSpan FailureMemory = TimeSpan.FromMinutes(1);

    private static int _privateAddressRefusals;

    /// <summary>How many fetches have been refused because the host resolved only to private or
    /// reserved addresses. Test seam for <see cref="SLNG.Core.PrivateAddressPolicy"/> actually
    /// being enforced by the HttpClient, rather than merely existing.</summary>
    internal static int PrivateAddressRefusals => System.Threading.Volatile.Read(ref _privateAddressRefusals);

    /// <summary>In-flight requests, so a face rebuilt several times in one frame issues one GET.
    /// Entries are removed on completion — this dedupes, it does not cache.</summary>
    private static readonly ConcurrentDictionary<(string Url, int FitWidth, int FitHeight), Task<TextureData?>> InFlight = new();

    /// <summary>Fetches and decodes <paramref name="url"/>, or returns null when it isn't
    /// reachable, isn't actually an image, or exceeds <see cref="MaxContentBytes"/>. Never throws
    /// -- an untrusted, arbitrary in-world URL failing is routine, not exceptional, and must never
    /// surface as an exception to the caller -- but every rejection reason IS logged (`[MediaImage]`
    /// via `Console.Error`, bridged into `godot.log` the same way every other net-boundary
    /// diagnostic in this project is): a face that silently never loads its media, with nothing
    /// distinguishing "URL is genuinely dead" from "we're doing something wrong talking to it", is
    /// undiagnosable from the outside, and unlike a routine 404 it is real project bugs, not hostile
    /// URLs, that this project has hit here so far (missing User-Agent, above).
    ///
    /// <para>When <paramref name="fitWidth"/>/<paramref name="fitHeight"/> are both positive, the
    /// decoded image is letterboxed onto a canvas of exactly that size (<see
    /// cref="MediaFace.WidthPixels"/>/<see cref="MediaFace.HeightPixels"/>, the creator's own
    /// declared media size) instead of returned at its native resolution -- matching
    /// <c>PRIM_MEDIA_AUTO_SCALE</c>: SL fits the image into the display area preserving its own
    /// aspect ratio, with the face's existing UV mapping (which already spans that declared size)
    /// left untouched, rather than the naive stretch-to-fill a plain texture swap would otherwise
    /// produce. Confirmed live 2026-09-17 against Firestorm's own black-letterboxed rendering of
    /// the same face.</para></summary>
    public static Task<TextureData?> FetchAsync(string url, int fitWidth = 0, int fitHeight = 0)
    {
        var key = (url, fitWidth, fitHeight);

        if (Decoded.TryGetValue(key, out TextureData? hit)) return Task.FromResult(hit);
        if (Failures.TryGetValue(key, out _)) return Task.FromResult<TextureData?>(null);

        return InFlight.GetOrAdd(key, static k => FetchAndRememberAsync(k));
    }

    private static async Task<TextureData?> FetchAndRememberAsync((string Url, int FitWidth, int FitHeight) key)
    {
        try
        {
            var result = await FetchCoreAsync(key.Url, key.FitWidth, key.FitHeight).ConfigureAwait(false);

            if (result != null)
            {
                Decoded.Set(key, result, new MemoryCacheEntryOptions
                {
                    Size = result.Rgba.Length,
                    SlidingExpiration = TimeSpan.FromMinutes(10),
                });
            }
            else
            {
                Failures.Set(key, true, new MemoryCacheEntryOptions
                {
                    Size = 1,
                    AbsoluteExpirationRelativeToNow = FailureMemory,
                });
            }

            return result;
        }
        finally
        {
            InFlight.TryRemove(key, out _);
        }
    }

    private static async Task<TextureData?> FetchCoreAsync(string url, int fitWidth, int fitHeight)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            Console.Error.WriteLine($"[MediaImage] {url}: not an absolute http(s) URL -- refusing");
            return null;
        }

        try
        {
            using var cts = new CancellationTokenSource(RequestTimeout);
            using var response = await HttpClient
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"[MediaImage] {url}: HTTP {(int)response.StatusCode} {response.StatusCode}");
                return null;
            }

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType == null || !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"[MediaImage] {url}: content-type '{contentType ?? "(none)"}' is not image/*");
                return null;
            }

            if (response.Content.Headers.ContentLength is { } declaredLength && declaredLength > MaxContentBytes)
            {
                Console.Error.WriteLine($"[MediaImage] {url}: declared length {declaredLength} exceeds {MaxContentBytes} byte cap");
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            using var bounded = new MemoryStream();
            await stream.CopyToAsync(bounded, cts.Token).ConfigureAwait(false);
            if (bounded.Length > MaxContentBytes)
            {
                Console.Error.WriteLine($"[MediaImage] {url}: body exceeded {MaxContentBytes} byte cap while streaming");
                return null;
            }

            var decoded = Decode(bounded.ToArray(), fitWidth, fitHeight);
            if (decoded == null)
                Console.Error.WriteLine($"[MediaImage] {url}: downloaded {bounded.Length} bytes ({contentType}) but SKBitmap could not decode it");
            return decoded;
        }
        catch (Exception ex)
        {
            // Untrusted host, arbitrary network conditions -- a failure here is routine (timeout,
            // DNS, TLS refusal), not something to propagate as an exception. It IS worth a line in
            // the log, the same as every other transport failure this project surfaces.
            Console.Error.WriteLine($"[MediaImage] {url}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Decodes the raw bytes, optionally letterboxing onto a <paramref name="fitWidth"/> x
    /// <paramref name="fitHeight"/> canvas (see <see cref="FetchAsync"/>), then converts to exact
    /// RGBA via the same SKBitmap path AssetService's own CoreJ2K fallback already uses, so a media
    /// image and a J2K world texture reach Godot through identical, already-proven code.</summary>
    internal static TextureData? Decode(byte[] bytes, int fitWidth = 0, int fitHeight = 0)
    {
        SKBitmap? bitmap;
        try
        {
            // Unlike most of SkiaSharp's API, SKBitmap.Decode does NOT return null for input it
            // can't make sense of -- it throws ArgumentNullException from inside its own codec
            // lookup (confirmed against the actual installed 3.119.4 by a test feeding it garbage
            // bytes). An arbitrary in-world URL serving a non-image, truncated, or corrupt response
            // despite claiming an image/* content-type is routine, not exceptional.
            bitmap = SKBitmap.Decode(bytes);
        }
        catch
        {
            return null;
        }
        using var _ = bitmap;
        if (bitmap == null || bitmap.Width <= 0 || bitmap.Height <= 0) return null;

        if (fitWidth > 0 && fitHeight > 0)
        {
            using var letterboxed = Letterbox(bitmap, fitWidth, fitHeight);
            return ToTextureData(letterboxed);
        }

        return ToTextureData(bitmap);
    }

    /// <summary>Composites <paramref name="source"/>, scaled to preserve its own aspect ratio,
    /// centered onto an opaque black <paramref name="width"/> x <paramref name="height"/> canvas --
    /// the "contain" fit `PRIM_MEDIA_AUTO_SCALE` describes, and what a reference viewer actually
    /// shows (verified live: Firestorm pillarboxes this exact image with black bars, not a stretch).
    /// Black, not transparent: a MOAP display area reads as a screen/frame, not a cutout.</summary>
    private static SKBitmap Letterbox(SKBitmap source, int width, int height)
    {
        var canvasBitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(canvasBitmap))
        {
            canvas.Clear(SKColors.Black);

            float scale = Math.Min((float)width / source.Width, (float)height / source.Height);
            float drawWidth = source.Width * scale;
            float drawHeight = source.Height * scale;
            float offsetX = (width - drawWidth) / 2f;
            float offsetY = (height - drawHeight) / 2f;

            canvas.DrawBitmap(source, new SKRect(offsetX, offsetY, offsetX + drawWidth, offsetY + drawHeight));
        }
        return canvasBitmap;
    }

    private static TextureData? ToTextureData(SKBitmap bitmap)
    {
        var rgbaBitmap = bitmap.ColorType == SKColorType.Rgba8888 ? bitmap : bitmap.Copy(SKColorType.Rgba8888);
        if (rgbaBitmap == null) return null;

        try
        {
            int width = bitmap.Width, height = bitmap.Height;
            var rgba = new byte[width * height * 4];

            if (rgbaBitmap.RowBytes == width * 4)
            {
                System.Runtime.InteropServices.Marshal.Copy(rgbaBitmap.GetPixels(), rgba, 0, rgba.Length);
            }
            else
            {
                var ptr = rgbaBitmap.GetPixels();
                for (int y = 0; y < height; y++)
                    System.Runtime.InteropServices.Marshal.Copy(ptr + y * rgbaBitmap.RowBytes, rgba, y * width * 4, width * 4);
            }

            return new TextureData(width, height, rgba);
        }
        finally
        {
            if (rgbaBitmap != bitmap) rgbaBitmap.Dispose();
        }
    }
}
