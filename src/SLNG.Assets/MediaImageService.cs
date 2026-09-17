using System.Collections.Concurrent;
using System.Net.Http;
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

    private static readonly HttpClient HttpClient = new() { Timeout = RequestTimeout };

    private static readonly ConcurrentDictionary<string, Task<TextureData?>> Cache = new();

    /// <summary>Fetches and decodes <paramref name="url"/>, or returns null when it isn't
    /// reachable, isn't actually an image, or exceeds <see cref="MaxContentBytes"/>. Every failure
    /// mode is silent-null by design -- an untrusted, arbitrary in-world URL failing is routine,
    /// not exceptional, and must never surface as an exception to the caller.</summary>
    public static Task<TextureData?> FetchAsync(string url) => Cache.GetOrAdd(url, FetchCoreAsync);

    private static async Task<TextureData?> FetchCoreAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        try
        {
            using var cts = new CancellationTokenSource(RequestTimeout);
            using var response = await HttpClient
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType == null || !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return null;

            if (response.Content.Headers.ContentLength is { } declaredLength && declaredLength > MaxContentBytes)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            using var bounded = new MemoryStream();
            await stream.CopyToAsync(bounded, cts.Token).ConfigureAwait(false);
            if (bounded.Length > MaxContentBytes) return null;

            return Decode(bounded.ToArray());
        }
        catch
        {
            // Untrusted host, arbitrary content, arbitrary network conditions -- a failure here is
            // routine (404, timeout, TLS refusal, garbage bytes), not a bug to log or propagate.
            return null;
        }
    }

    /// <summary>Same SKBitmap -> exact-RGBA conversion AssetService's CoreJ2K fallback already
    /// uses, so a media image and a J2K world texture reach Godot through identical, already-
    /// proven code.</summary>
    internal static TextureData? Decode(byte[] bytes)
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
