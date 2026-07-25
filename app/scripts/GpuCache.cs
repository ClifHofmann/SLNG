using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace SLNG.App;

/// <summary>
/// A Godot-side LRU cache for heavy Godot resources like ImageTextures and ArrayMeshes.
/// Prevents the Godot client from leaking VRAM by tracking references and evicting 
/// unused assets when a budget is exceeded.
/// </summary>
public class GpuCache
{
    private class CacheEntry
    {
        public Guid Id;
        public Resource Res = null!;
        public long Size;
        public LinkedListNode<CacheEntry>? Node;
        public int RefCount;
    }

    private readonly Dictionary<Guid, CacheEntry> _cache = new();
    private readonly LinkedList<CacheEntry> _lruList = new();

    // AddRef/ReleaseRef routinely race ahead of the matching Put: ObjectRenderer's texture path
    // (GetOrCreateGpuTextureAsync) creates the GPU resource and Puts it into the cache only after
    // an async decode completes, but the object that will use it registers its ownership via
    // AddRef as soon as BuildFaceMaterialAsync returns -- which happens immediately, well before
    // the decode finishes, because the texture fetch is intentionally fire-and-forget (see the
    // "No await Task.WhenAll" comment in ObjectRenderer.BuildFaceMaterialAsync). Both calls are
    // marshalled onto the main thread via CallDeferred, and the AddRef-side deferral is queued
    // first, so on a normal load it always executes BEFORE the Put-side deferral. Before this
    // dictionary existed, that meant AddRef found nothing in _cache and silently no-op'd, so Put
    // always inserted the entry at its default RefCount 0 -- i.e. every texture in the cache was
    // permanently untracked and eviction-eligible regardless of how many objects were actually
    // displaying it. On a sparse region the 256MB budget is never exceeded so EvictIfNeeded never
    // runs and the bug is invisible; on a content-dense region (many unique textures) the budget
    // is exceeded routinely, and EvictIfNeeded happily evicts+Disposes a texture an object is
    // still rendering with, leaving that object's material pointing at a freed GPU resource --
    // the "flat/untextured mesh objects" and "solid red" (bare glTF baseColorFactor with its
    // texture ripped out from under it) symptoms reported on OSGrid's Dangazi Forest. Meshes never
    // showed this because AssignSharedMesh always passes initialRefCount: 1 to Put, pinning itself
    // before EvictIfNeeded can run; textures relied entirely on a later AddRef that had already
    // been dropped. This dictionary buffers any AddRef/ReleaseRef that arrives before the entry
    // exists, and Put reconciles it into the real starting RefCount -- order-independent, so it
    // fixes the race regardless of which CallDeferred happens to run first.
    private readonly Dictionary<Guid, int> _pendingRefDelta = new();

    private long _currentSize = 0;
    private readonly long _maxSize;

    // FEAT-PERF-02: single-flight coordination for GetOrUploadTextureAsync, shared by every
    // renderer (Object/Avatar/Terrain) that uploads GPU textures through this one GpuCache
    // instance. Lazy<Task<T>> (ExecutionAndPublication), not a bare Task, guarantees the
    // fetch+decode+Image+mipmap+upload work below runs at most once per texture id even under a
    // genuine concurrent race -- e.g. many faces/objects that all reference the same
    // never-before-cached texture at once, which previously each independently ran
    // Image.CreateFromData + GenerateMipmaps on the (already-deduplicated, see AssetService)
    // decoded pixels, with only the first to reach the main-thread callback actually keeping its
    // ImageTexture -- the other N-1 builds were pure waste.
    private readonly ConcurrentDictionary<Guid, Lazy<Task<ImageTexture?>>> _inflightTextureUploads = new();

    /// <summary>
    /// Initializes a new GpuCache with a VRAM budget.
    /// Default is 256 MB.
    /// </summary>
    public GpuCache(long maxSizeInBytes = 256 * 1024 * 1024)
    {
        _maxSize = maxSizeInBytes;
    }

    public void AddRef(Guid id)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(id, out var entry))
            {
                entry.RefCount++;
            }
            else
            {
                // Entry doesn't exist yet (Put hasn't run) -- buffer the ref so it isn't lost;
                // see _pendingRefDelta's doc comment for why this race is routine, not rare.
                _pendingRefDelta.TryGetValue(id, out var delta);
                SetOrClearPending(id, delta + 1);
            }
        }
    }

    public void ReleaseRef(Guid id)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(id, out var entry))
            {
                entry.RefCount--;
                if (entry.RefCount < 0) entry.RefCount = 0;
                EvictIfNeeded();
            }
            else
            {
                // Symmetric with AddRef above (e.g. ApplyOnMainThread releasing refs for a node
                // that was freed before its texture finished loading, so no AddRef ever ran).
                _pendingRefDelta.TryGetValue(id, out var delta);
                SetOrClearPending(id, delta - 1);
            }
        }
    }

    /// <summary>Keeps _pendingRefDelta from accumulating permanent zero-value entries for ids
    /// whose net pending AddRef/ReleaseRef calls cancel out (e.g. a texture decode that ultimately
    /// fails and is never Put, followed by the object that wanted it going out of range).</summary>
    private void SetOrClearPending(Guid id, int delta)
    {
        if (delta == 0) _pendingRefDelta.Remove(id);
        else _pendingRefDelta[id] = delta;
    }

    public Resource? Get(Guid id)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(id, out var entry))
            {
                if (entry.Node != null)
                {
                    _lruList.Remove(entry.Node);
                    _lruList.AddLast(entry.Node);
                }
                return entry.Res;
            }
            return null;
        }
    }

    public void Put(Guid id, Resource res, long size, int initialRefCount = 0)
    {
        lock (_cache)
        {
            if (_cache.ContainsKey(id)) return;

            // Fold in any AddRef/ReleaseRef that raced ahead of this Put (see _pendingRefDelta).
            // Without this, those calls are silently lost and every newly-cached resource starts
            // at exactly `initialRefCount` no matter how many owners already registered interest.
            int pending = 0;
            if (_pendingRefDelta.TryGetValue(id, out var delta))
            {
                pending = delta;
                _pendingRefDelta.Remove(id);
            }

            // initialRefCount lets a caller that immediately holds the resource (e.g. a mesh
            // assigned to a node) pin it before EvictIfNeeded runs, so a tight budget can't
            // evict the entry on the same call that added it.
            int startRefCount = Math.Max(0, initialRefCount + pending);
            var entry = new CacheEntry { Id = id, Res = res, Size = size, RefCount = startRefCount };
            entry.Node = _lruList.AddLast(entry);
            _cache[id] = entry;
            _currentSize += size;

            EvictIfNeeded();
        }
    }

    /// <summary>
    /// Fetches/decodes (via <paramref name="assetService"/>) and uploads a texture as an
    /// <see cref="ImageTexture"/>, or returns the already-cached one. Centralizes what
    /// ObjectRenderer/AvatarRenderer/TerrainRenderer each used to implement separately (and had
    /// already drifted slightly -- e.g. only some call sites pass <paramref name="initialRefCount"/>),
    /// and adds a single-flight guarantee across ALL of them: concurrent callers requesting the
    /// same <paramref name="textureId"/> from any renderer share one Image/mipmap build and one
    /// upload, not just one network fetch (see AssetService.GetTextureAsync for that half).
    /// <paramref name="generateMipmaps"/>/<paramref name="initialRefCount"/> apply to whichever
    /// caller's request actually performs the build for a given id -- if two callers ever request
    /// the same id with different values (not expected: different renderers use disjoint texture
    /// categories in practice), the first one to start the build wins for that id.
    /// <para>FEAT-PERF-02 Phase 2: <paramref name="desiredDiscard"/> has the exact same
    /// first-caller-wins caveat, now more likely to matter (two instances of the same object at
    /// different distances CAN share a texture id). More importantly: once a texture is cached
    /// here (<see cref="Get"/> above short-circuits before ever calling AssetService again), it
    /// is NEVER re-fetched at a different discard level for the rest of the session -- a texture
    /// first requested by a distant/small object stays at that resolution even if the same or
    /// another instance later needs it sharp. Deliberate scope limit for this pass (a real fix
    /// needs a discard-aware cache key here too, not just in AssetService) -- see the spec's
    /// Phase 2 notes.</para>
    /// </summary>
    public Task<ImageTexture?> GetOrUploadTextureAsync(
        Guid textureId,
        SLNG.Assets.AssetService? assetService,
        bool generateMipmaps,
        int initialRefCount = 0,
        int desiredDiscard = 0,
        float priority = 0f)
    {
        if (textureId == Guid.Empty) return Task.FromResult<ImageTexture?>(null);

        var cached = Get(textureId) as ImageTexture;
        if (cached != null) return Task.FromResult(cached)!;

        if (assetService == null) return Task.FromResult<ImageTexture?>(null);

        var lazy = _inflightTextureUploads.GetOrAdd(textureId, id => new Lazy<Task<ImageTexture?>>(
            () => FetchAndUploadTextureAsync(id, assetService, generateMipmaps, initialRefCount, desiredDiscard, priority),
            LazyThreadSafetyMode.ExecutionAndPublication));
        return lazy.Value;
    }

    private async Task<ImageTexture?> FetchAndUploadTextureAsync(
        Guid textureId, SLNG.Assets.AssetService assetService, bool generateMipmaps, int initialRefCount, int desiredDiscard, float priority)
    {
        try
        {
            var textureData = await assetService.GetTextureAsync(textureId, desiredDiscard, priority: priority).ConfigureAwait(false);
            if (textureData == null) return null;

            // Image/mipmap build happens on this (worker) thread, matching the threading rule in
            // AGENTS.md -- only the final Resource creation + cache Put below touches the main
            // thread, via CallDeferred.
            var image = Image.CreateFromData(textureData.Width, textureData.Height, false, Image.Format.Rgba8, textureData.Rgba);
            if (generateMipmaps) image?.GenerateMipmaps();

            var tcs = new TaskCompletionSource<ImageTexture?>();
            Godot.Callable.From(() =>
            {
                // Re-check: a differently-triggered Put for this id (shouldn't normally happen
                // given the single-flight dict above, but costs nothing to guard) may have
                // already landed between the await above and this deferred callback running.
                var raced = Get(textureId) as ImageTexture;
                if (raced != null)
                {
                    image?.Dispose();
                    tcs.SetResult(raced);
                    return;
                }

                if (image == null)
                {
                    tcs.SetResult(null);
                    return;
                }

                var tex = ImageTexture.CreateFromImage(image);
                if (tex != null)
                {
                    long size = (long)textureData.Width * textureData.Height * 4;
                    Put(textureId, tex, size, initialRefCount);
                }
                tcs.SetResult(tex);
                image.Dispose();
            }).CallDeferred();

            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _inflightTextureUploads.TryRemove(textureId, out _);
        }
    }

    private void EvictIfNeeded()
    {
        if (_currentSize <= _maxSize) return;

        var node = _lruList.First;
        while (node != null && _currentSize > _maxSize)
        {
            var next = node.Next;
            var entry = node.Value;

            if (entry.RefCount <= 0)
            {
                _lruList.Remove(node);
                _cache.Remove(entry.Id);
                _currentSize -= entry.Size;
                // Explicitly dispose the C# wrapper so its finalizer won't run later (e.g. after RenderingServer is gone)
                if (GodotObject.IsInstanceValid(entry.Res))
                {
                    entry.Res.Dispose();
                }
            }
            node = next;
        }
    }

    /// <summary>Explicitly frees every still-cached Resource's native RID (ImageTexture/ArrayMesh
    /// both wrap RenderingServer-owned GPU objects). Call on app shutdown, before the engine tears
    /// down -- otherwise cleanup falls to whenever the .NET GC gets around to finalizing each
    /// object, which is not guaranteed to happen before RenderingServer itself is destroyed. A late
    /// finalizer calling back into a gone RenderingServer is exactly the documented cause of the
    /// "N RID allocations... leaked at exit" / "RenderingServer::get_singleton() is null" pair seen
    /// at shutdown (see CursorManager's identical fix for its own transient cursor texture -- this
    /// is the same failure mode for the long-lived cache instead).</summary>
    public void DisposeAll()
    {
        lock (_cache)
        {
            foreach (var entry in _cache.Values)
            {
                if (GodotObject.IsInstanceValid(entry.Res))
                {
                    entry.Res.Dispose();
                }
            }
            _cache.Clear();
            _lruList.Clear();
            _currentSize = 0;
        }
    }
}
