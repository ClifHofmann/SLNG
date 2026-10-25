using System;
using System.Collections.Concurrent;
using SLNG.Assets;

namespace SLNG.App;

/// <summary>
/// BUG-RENDER-50 [BomAlpha] (--diag only): a downsampled alpha-only map (at most 256 texels a side) of every
/// avatar bake decoded this session, so a Bakes-on-Mesh face can be measured against the alpha it samples even
/// after the decoded bake has left the asset cache (bakes are memory-only and short-lived there). Held in
/// memory and nowhere else: on a Linden grid another resident's bake must never be written out (TPV §2.b,
/// FEAT-SL-01). Captured on the decode worker.
/// </summary>
internal static class BakeAlphaMaps
{
    private const int MaxSide = 256;
    private const int MaxMaps = 128;

    private static readonly ConcurrentDictionary<Guid, (byte[] Alpha, int Width, int Height)> _maps = new();

    public static void Capture(Guid bakeId, TextureData data)
    {
        if (_maps.ContainsKey(bakeId)) return;
        if (_maps.Count >= MaxMaps) _maps.Clear(); // a diagnostic: forgetting all beats growing without bound
        var map = BomAlphaProbe.DownsampleAlpha(data.Rgba, data.Width, data.Height, MaxSide);
        if (map.Width > 0) _maps[bakeId] = map;
    }

    public static bool TryGet(Guid bakeId, out (byte[] Alpha, int Width, int Height) map) => _maps.TryGetValue(bakeId, out map);
}
