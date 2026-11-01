using SLNG.Net;

namespace SLNG.Assets;

/// <summary>
/// FEAT-PERF-29: progressive texture streaming -- the switch, and the pure rules the streamer and the GPU
/// cache share. A texture is first fetched coarse (the estimator's discard-5 size), shown, and then refined
/// to the discard its pixel area needs, the way the reference viewer streams (lltexturefetch.cpp:2572-2593).
/// See docs/specs/FEAT-PERF-29-progressive-streaming.md.
/// </summary>
public static class TextureStreaming
{
    /// <summary>Off by default; <c>--tex-stream</c> (with <c>--diag</c>) turns it on for the A/B. Startup only.</summary>
    public static bool Enabled { get; set; }

    /// <summary>The estimator's discard-5 size for a texture of unknown size (it assumes 2048x2048): 18,840 B.
    /// The spike decoded every such cut cleanly; the viewer's own 600 B was blank or far off on large textures.</summary>
    public static readonly int DefaultFirstRequestBytes =
        J2kByteSizeEstimator.CalcDataSizeJ2C(0, 0, J2kByteSizeEstimator.MaxDiscardLevel);

    /// <summary>Bytes of the first request, before the header is known. <c>--tex-stream-first=N</c> for the A/B.</summary>
    public static int FirstRequestBytes { get; set; } = DefaultFirstRequestBytes;

    /// <summary>BUG-RENDER-15: an alpha texture's channel is its silhouette, which is gone at 32 px. Streaming
    /// never stops one below this many pixels on its long side.</summary>
    public const int AlphaFloorPx = 64;

    /// <summary>The discard a face of <paramref name="screenPixelArea"/> pixels needs from a
    /// <paramref name="width"/> x <paramref name="height"/> texture: <see cref="TextureLod.DiscardLevelFor"/>
    /// (the viewer's log4 rule, with the VRAM bias), floored at <see cref="AlphaFloorPx"/> for one with an
    /// alpha component.</summary>
    public static int TargetDiscard(int width, int height, int components, float screenPixelArea)
    {
        int discard = TextureLod.DiscardLevelFor(width, height, screenPixelArea);
        if (components >= 4)
        {
            int longSide = Math.Max(width, height);
            while (discard > 0 && (longSide >> discard) < AlphaFloorPx) discard--;
        }
        return discard;
    }

    /// <summary>The finest discard whose estimated size <paramref name="heldBytes"/> reach. A prefix shorter
    /// than the discard-5 estimate still only serves discard 5.</summary>
    public static int CoveredDiscard(int width, int height, int heldBytes)
    {
        for (int discard = 0; discard < J2kByteSizeEstimator.MaxDiscardLevel; discard++)
            if (heldBytes >= J2kByteSizeEstimator.CalcDataSizeJ2C(width, height, discard)) return discard;
        return J2kByteSizeEstimator.MaxDiscardLevel;
    }

    /// <summary>The screen area a coarse upload of <paramref name="width"/> x <paramref name="height"/> is
    /// "built for" in <c>GpuCache</c>'s sharpen rule (sharpen once the area reaches 4x it): a quarter of its
    /// texels, so the next level is fetched as soon as the face shows more than the image holds. Never more
    /// than the area that was asked for.</summary>
    public static float BuiltForArea(float requestedArea, int width, int height)
        => Math.Min(requestedArea, Math.Max(1f, width * (float)height / 4f));

    /// <summary>Decodes a prefix at <paramref name="reduce"/>, and when that fails retries it 256 B and then 1 KB
    /// shorter. CoreJ2K throws in <c>FileBitstreamReaderAgent.readTilePkts</c> at some cut points (1.4 % of random
    /// ones in the spike), and the same bytes cut a little earlier decode.</summary>
    internal static TextureData? DecodeWithRescue(byte[] prefix, int reduce, Func<byte[], int, TextureData?> decode, out bool rescued)
    {
        rescued = false;
        var data = decode(prefix, reduce);
        if (data != null) return data;
        foreach (int shorter in RescueCuts)
        {
            if (prefix.Length - shorter <= 0) break;
            data = decode(prefix[..^shorter], reduce);
            if (data != null) { rescued = true; return data; }
        }
        return null;
    }

    private static readonly int[] RescueCuts = { 256, 1024 };

    // --diag counters for the [TexStream] line.
    internal static long FirstRequests, FirstBytes, Refinements, RefineBytes, LocalDecodes, Completed, Rescued, FellBack;
    private static long _reportedFirst, _reportedRefine, _reportedLocal;

    /// <summary>The <c>[TexStream]</c> line: totals, and what this interval added. Null when nothing happened.</summary>
    internal static string? TakeLine()
    {
        long first = Interlocked.Read(ref FirstRequests), refine = Interlocked.Read(ref Refinements), local = Interlocked.Read(ref LocalDecodes);
        if (first == _reportedFirst && refine == _reportedRefine && local == _reportedLocal) return null;
        string line = $"[TexStream] first={first} ({Interlocked.Read(ref FirstBytes) / 1048576.0:0.0} MB) " +
                      $"refine={refine} ({Interlocked.Read(ref RefineBytes) / 1048576.0:0.0} MB) fromPart={local} " +
                      $"completed={Interlocked.Read(ref Completed)} rescued={Interlocked.Read(ref Rescued)} " +
                      $"fellBack={Interlocked.Read(ref FellBack)} | last interval: +{first - _reportedFirst} first " +
                      $"+{refine - _reportedRefine} refine +{local - _reportedLocal} fromPart";
        _reportedFirst = first; _reportedRefine = refine; _reportedLocal = local;
        return line;
    }
}
