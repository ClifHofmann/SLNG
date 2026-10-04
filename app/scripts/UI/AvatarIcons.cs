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
