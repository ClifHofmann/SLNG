using Godot;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using SLNG.Core;
using SLNG.Net;

namespace SLNG.App.UI;

/// <summary>
/// The mini profile pictures shown before a name in a list (the chat's conversation list, the Recent
/// list). The picture is the avatar's 2nd Life profile image: its id comes from one
/// <c>AvatarPropertiesRequest</c> (<c>GridSession.RequestProfileImage</c>), the texture itself through the
/// same asset path and <see cref="GpuCache"/> as the profile window -- decoded off the main thread, uploaded
/// small (a 24 px icon never needs the full 1024 px picture on the GPU).
///
/// Main-thread API (<see cref="Get"/>, <see cref="Drain"/>, <see cref="Clear"/>); the network thread only
/// enqueues. Every texture it uses stays pinned in the cache (<c>AddRef</c> before the fetch, as the profile
/// window does) so eviction cannot dispose it under a <c>TextureRect</c>, and is released by
/// <see cref="Clear"/>. The number of pictures is capped; past it a row simply has no picture.
/// </summary>
public sealed class AvatarIcons
{
    /// <summary>The on-screen size an icon is drawn at. The upload is sized for it (area in pixels).</summary>
    public const int IconSize = 20;
    private const int MaxPictures = 160;

    private static ImageTexture? _placeholder;

    /// <summary>The generic person symbol shown while a picture loads and for an avatar that has none --
    /// what the reference viewer's avatar icon does (<c>default_icon_name</c>, "Generic_Person_Large"). Drawn
    /// here instead of shipped as an image: a muted tile with a head and shoulders, at twice the icon size so
    /// it stays sharp on a scaled UI. Built once, on first use, on the main thread.</summary>
    public static Texture2D Placeholder => _placeholder ??= BuildPlaceholder();

    private static ImageTexture BuildPlaceholder()
    {
        const int size = IconSize * 2;
        var image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);

        var tile = new Color(0.27f, 0.31f, 0.38f);
        var figure = new Color(0.66f, 0.70f, 0.76f);
        float c = size / 2f;
        float cornerRadius = size * 0.2f;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float px = x + 0.5f, py = y + 0.5f;

                // Rounded tile: distance outside the inner rectangle, anti-aliased over one pixel.
                float qx = Mathf.Max(Mathf.Abs(px - c) - (c - cornerRadius), 0f);
                float qy = Mathf.Max(Mathf.Abs(py - c) - (c - cornerRadius), 0f);
                float tileCover = Mathf.Clamp(cornerRadius - Mathf.Sqrt(qx * qx + qy * qy) + 0.5f, 0f, 1f);
                if (tileCover <= 0f) { image.SetPixel(x, y, new Color(0, 0, 0, 0)); continue; }

                // Head: a circle. Shoulders: an ellipse that runs off the bottom edge.
                float head = Mathf.Clamp(size * 0.19f - Mathf.Sqrt(Sq(px - c) + Sq(py - size * 0.38f)) + 0.5f, 0f, 1f);
                float ex = (px - c) / (size * 0.36f), ey = (py - size * 0.98f) / (size * 0.34f);
                float shoulders = Mathf.Clamp((1f - Mathf.Sqrt(ex * ex + ey * ey)) * size * 0.34f + 0.5f, 0f, 1f);

                var col = tile.Lerp(figure, Mathf.Max(head, shoulders));
                image.SetPixel(x, y, new Color(col, tileCover));
            }
        }
        return ImageTexture.CreateFromImage(image);
    }

    private static float Sq(float v) => v * v;

    private readonly GpuCache _gpu;
    private readonly SLNG.Assets.AssetService? _assets;
    private readonly Action<Guid> _ready;

    private GridSession? _session;
    private readonly ConcurrentQueue<Guid> _known = new();
    private readonly Dictionary<Guid, Texture2D> _textures = new();   // main thread only
    private readonly HashSet<Guid> _loading = new();                  // agents, main thread only
    private readonly List<Guid> _pinned = new();                      // texture ids this object holds a ref on
    private int _generation;   // bumped by Clear: a load that finishes afterwards no longer has its pin

    /// <param name="ready">Called when a picture became available for an agent -- from ANY thread; the
    /// owner marshals to the main thread (<c>CallDeferred</c>) and then asks <see cref="Get"/> again.</param>
    public AvatarIcons(GpuCache gpu, SLNG.Assets.AssetService? assets, Action<Guid> ready)
    {
        _gpu = gpu;
        _assets = assets;
        _ready = ready;
    }

    /// <summary>(Re-)binds to a session -- after a re-login, when the previous one is being disposed.</summary>
    public void Bind(GridSession session)
    {
        if (_session != null) _session.ProfileImageKnown -= OnProfileImageKnown;
        Clear();
        _session = session;
        _session.ProfileImageKnown += OnProfileImageKnown;
    }

    /// <summary>Unsubscribes and gives back every pinned texture.</summary>
    public void Dispose()
    {
        if (_session != null) _session.ProfileImageKnown -= OnProfileImageKnown;
        _session = null;
        Clear();
    }

    // Network thread: only queue; the main thread starts the load (Drain).
    private void OnProfileImageKnown(object? sender, ProfileImageEvent e)
    {
        _known.Enqueue(e.AgentId);
        _ready(e.AgentId); // wakes the owner, which calls Drain() then Get() on the main thread
    }

    /// <summary>The picture for an agent, or null until it has loaded (or when there is none). The first call
    /// for an agent asks the grid for it; later calls are dictionary lookups, so a list may call this for every
    /// row on every refresh.</summary>
    public Texture2D? Get(Guid agentId)
    {
        if (agentId == Guid.Empty || _session == null) return null;
        if (_textures.TryGetValue(agentId, out var tex)) return tex;

        if (_session.TryGetProfileImageId(agentId, out var imageId)) StartLoad(agentId, imageId);
        else _session.RequestProfileImage(agentId);
        return null;
    }

    /// <summary>Starts the loads for pictures whose id arrived since the last call. Main thread.</summary>
    public void Drain()
    {
        Collect();
        while (_known.TryDequeue(out var agentId))
        {
            if (_session != null && _session.TryGetProfileImageId(agentId, out var imageId)) StartLoad(agentId, imageId);
        }
    }

    private void StartLoad(Guid agentId, Guid imageId)
    {
        if (imageId == Guid.Empty || _textures.ContainsKey(agentId) || !_loading.Add(agentId)) return;
        if (_pinned.Count >= MaxPictures) return;

        _gpu.AddRef(imageId); // before the fetch, so a finished upload cannot be evicted before it is shown
        _pinned.Add(imageId);
        _ = LoadAsync(agentId, imageId, _generation);
    }

    private async Task LoadAsync(Guid agentId, Guid imageId, int generation)
    {
        try
        {
            var tex = await _gpu.GetOrUploadTextureAsync(imageId, _assets, generateMipmaps: true,
                screenPixelArea: IconSize * IconSize).ConfigureAwait(false);
            if (tex == null || generation != System.Threading.Volatile.Read(ref _generation)) return;

            // Back on the main thread through the owner's CallDeferred: the dictionary is main-thread only.
            _pendingTextures.Enqueue((agentId, tex));
            _ready(agentId);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[AvatarIcons] picture for {agentId} failed: {ex.Message}");
        }
    }

    private readonly ConcurrentQueue<(Guid Agent, Texture2D Texture)> _pendingTextures = new();

    /// <summary>Moves finished uploads into the lookup. Main thread; <see cref="Drain"/> calls it.</summary>
    private void Collect()
    {
        while (_pendingTextures.TryDequeue(out var item))
        {
            _textures[item.Agent] = item.Texture;
            _loading.Remove(item.Agent);
        }
    }

    private void Clear()
    {
        System.Threading.Interlocked.Increment(ref _generation);
        foreach (var id in _pinned) _gpu.ReleaseRef(id);
        _pinned.Clear();
        _textures.Clear();
        _loading.Clear();
        while (_known.TryDequeue(out _)) { }
        while (_pendingTextures.TryDequeue(out _)) { }
    }
}
