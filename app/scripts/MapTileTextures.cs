using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using SLNG.Assets;
using SLNG.Core;
using SLNG.Net;

namespace SLNG.App;

/// <summary>
/// FEAT-UI-39: region map tiles as Godot textures, for the radar (and, later, the world map).
/// <see cref="Get"/> is the whole interface: it returns the texture if it is ready and otherwise
/// starts the load, so a caller just asks every frame.
///
/// Two sources, tried in this order. The grid's map-tile server (<see cref="GridSession.MapServerUrl"/>,
/// a plain JPEG by grid coordinates -- what the reference viewer draws on its mini-map, and the only
/// place a Second Life tile lives), then the region's map image asset, which is how OpenSim without a
/// tile server hands its map out.
///
/// Threading: the fetch and the JPEG decode run on worker threads; only the texture upload and the
/// dictionary write happen on the main thread, through the same work queue as every other upload.
/// <see cref="Get"/> and <see cref="Clear"/> are main-thread calls.
/// </summary>
public sealed class MapTileTextures
{
    /// <summary>A tile that failed is not asked for again for this long.</summary>
    private const long RetryAfterMsec = 60_000;

    /// <summary>Marks a tile as loaded or loading -- never retried until <see cref="Clear"/>.</summary>
    private const long Settled = long.MaxValue;

    private readonly GridSession _session;
    private readonly GpuCache _gpuCache;
    private readonly AssetService _assetService;
    private readonly MapTileService _service;

    // Main thread only: written by the queued upload, read by Get.
    private readonly Dictionary<ulong, Texture2D> _textures = new();

    // Handle -> Environment.TickCount64 before which it must not be requested again. Settled while
    // loading and once loaded. Touched from workers on failure, hence concurrent.
    private readonly ConcurrentDictionary<ulong, long> _blockedUntil = new();

    // Asset ids pinned in the GPU cache for the asset fallback, released in Clear.
    private readonly ConcurrentBag<Guid> _pinned = new();

    // Bumped by Clear so a load that finishes after a re-login is dropped, not stored.
    private int _generation;

    public MapTileTextures(GridSession session, GpuCache gpuCache, AssetService assetService, string cacheDirectory)
    {
        _session = session;
        _gpuCache = gpuCache;
        _assetService = assetService;
        _service = new MapTileService(cacheDirectory);
    }

    /// <summary>The tile for a region, or null while it is not there (yet, or at all). Starts the
    /// load the first time it is asked, and again a minute after a failure.</summary>
    public Texture2D? Get(ulong regionHandle)
    {
        if (_textures.TryGetValue(regionHandle, out var texture)) return texture;
        Request(regionHandle);
        return null;
    }

    /// <summary>Forgets every tile, for a new session -- a different grid has different tiles.</summary>
    public void Clear()
    {
        _generation++;
        _textures.Clear();
        _blockedUntil.Clear();
        while (_pinned.TryTake(out var id)) _gpuCache.ReleaseRef(id);
    }

    private void Request(ulong regionHandle)
    {
        if (_blockedUntil.TryGetValue(regionHandle, out var until) && System.Environment.TickCount64 < until) return;
        _blockedUntil[regionHandle] = Settled;
        _ = LoadAsync(regionHandle, _generation);
    }

    private async Task LoadAsync(ulong regionHandle, int generation)
    {
        try
        {
            string baseUrl = _session.MapServerUrl;
            if (baseUrl.Length > 0)
            {
                var data = await _service
                    .GetTileAsync(baseUrl, RegionHandle.GridX(regionHandle), RegionHandle.GridY(regionHandle))
                    .ConfigureAwait(false);
                if (data != null && PublishPixels(regionHandle, generation, data)) return;
            }

            var info = await _session.ResolveRegionByHandleAsync(regionHandle).ConfigureAwait(false);
            if (info != null && info.MapImageId != Guid.Empty)
            {
                var texture = await _gpuCache
                    .GetOrUploadTextureAsync(info.MapImageId, _assetService, generateMipmaps: false)
                    .ConfigureAwait(false);
                if (texture != null)
                {
                    // Pinned for as long as it is drawn: the cache entry starts unreferenced and is
                    // evictable -- a disposed texture crashes every redraw (WorldMapWindow, MVP2-3).
                    _gpuCache.AddRef(info.MapImageId);
                    _pinned.Add(info.MapImageId);
                    Publish(regionHandle, generation, () => texture);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[MapTile] {regionHandle}: {ex.Message}");
        }

        _blockedUntil[regionHandle] = System.Environment.TickCount64 + RetryAfterMsec;
    }

    /// <summary>Builds the image on this worker thread and queues only the GPU upload.</summary>
    private bool PublishPixels(ulong regionHandle, int generation, TextureData data)
    {
        if (GpuCache.ShuttingDown) return true;
        var image = Image.CreateFromData(data.Width, data.Height, false, Image.Format.Rgba8, data.Rgba);
        if (image == null) return false;
        Publish(regionHandle, generation, () => ImageTexture.CreateFromImage(image));
        return true;
    }

    private void Publish(ulong regionHandle, int generation, Func<Texture2D> makeTexture)
    {
        MainThreadWorkQueue.Enqueue(MainThreadWorkQueue.Lane.Refine, () =>
        {
            if (generation != _generation) return;
            _textures[regionHandle] = makeTexture();
        }, label: "map.tile");
    }
}
