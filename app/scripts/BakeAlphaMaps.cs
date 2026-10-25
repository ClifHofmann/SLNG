using System;
using System.Collections.Concurrent;
using SLNG.Assets;

namespace SLNG.App;

/// <summary>
/// BUG-RENDER-50 [BomAlpha] (--diag only): a downsampled alpha-only map (at most 128 texels a side, 16 KB) of
/// every avatar bake this session handled, so a Bakes-on-Mesh face can be measured against the alpha it
/// samples even after the decoded bake has left the asset cache. Filled from
/// <see cref="AssetService.BakeMaterialised"/> (every path that hands out a bake's pixels) and, failing that,
/// once per bake from the uploaded texture (<see cref="AvatarRenderer"/>'s [BomAlpha]). Held in memory and
/// nowhere else: on a Linden grid another resident's bake must never be written out (TPV §2.b, FEAT-SL-01).
/// The oldest map goes first once <see cref="MaxMaps"/> are held: a crowded region has far more bakes than the
/// 128 the first version kept, and clearing them all lost the one being looked at.
/// </summary>
internal static class BakeAlphaMaps
{
    private const int MaxSide = 128;
    private const int MaxMaps = 1024;

    private static readonly ConcurrentDictionary<Guid, (byte[] Alpha, int Width, int Height)> _maps = new();
    private static readonly ConcurrentQueue<Guid> _order = new();

    public static void Capture(Guid bakeId, TextureData data) => Capture(bakeId, data.Rgba, data.Width, data.Height);

    public static void Capture(Guid bakeId, byte[] rgba, int width, int height)
    {
        if (_maps.ContainsKey(bakeId)) return;
        var map = BomAlphaProbe.DownsampleAlpha(rgba, width, height, MaxSide);
        if (map.Width <= 0 || !_maps.TryAdd(bakeId, map)) return;
        _order.Enqueue(bakeId);
        while (_maps.Count > MaxMaps && _order.TryDequeue(out var oldest)) _maps.TryRemove(oldest, out _);
    }

    public static bool TryGet(Guid bakeId, out (byte[] Alpha, int Width, int Height) map) => _maps.TryGetValue(bakeId, out map);
}
