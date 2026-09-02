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
    /// <para>FEAT-PERF-02 Phase 2: <paramref name="screenPixelArea"/> drives a LOCAL post-decode
    /// downsample, not the network fetch -- AssetService is always asked for the full asset
    /// (discard 0) regardless of this value. Network-side discard (asking the simulator for
    /// fewer bytes via HTTP Range) was tried and disabled: Magick.NET does not tolerate a
    /// deliberately-truncated J2C stream, see ObjectRenderer.ComputeTextureLod's doc comment for
    /// the live-tested failure mode. Shrinking the already-fully-decoded Image before it reaches
    /// the GPU sidesteps that decoder bug entirely and still delivers a real VRAM reduction for
    /// distant/small objects -- it just doesn't save any network bandwidth (full asset is always
    /// downloaded), unlike the disabled network-discard path.
    /// <para>The caller passes the object's on-screen area in PIXELS rather than a ready-made
    /// discard level, because the correct level depends on the decoded texture's own resolution,
    /// which only this method knows. 0 (the default) means "unknown / don't downsample".</para>
    /// <para>A downsampled texture is no longer stuck that way: a later request from closer up
    /// (larger <paramref name="screenPixelArea"/>) re-uploads it sharper in place -- see
    /// <see cref="TryUpgradeCachedTexture"/>. The reverse does NOT happen; nothing ever
    /// re-downsamples a texture once it has been sharpened, so a shared texture settles at the
    /// resolution its closest/largest viewer needed and stays there for the session.</para>
    /// </summary>
    public Task<ImageTexture?> GetOrUploadTextureAsync(
        Guid textureId,
        SLNG.Assets.AssetService? assetService,
        bool generateMipmaps,
        int initialRefCount = 0,
        float screenPixelArea = 0f,
        float priority = 0f,
        bool rejectDegraded = false,
        int? bakeChannel = null,
        Guid bakeAgentId = default)
    {
        if (textureId == Guid.Empty) return Task.FromResult<ImageTexture?>(null);

        // A rejectDegraded caller (the avatar) must not be handed an upload that some OTHER
        // caller produced from a gap-filled decode. This early return is a third cache layer on
        // top of AssetService's memory and disk caches, and it bypasses AssetService entirely --
        // which is why the avatar showed speckle noise while the logs recorded no degraded decode
        // and no rejectDegraded give-up for it at all: the bytes were never re-examined, only the
        // finished upload was reused. Route such callers through AssetService, where the contract
        // is actually enforced.
        var cached = rejectDegraded ? null : Get(textureId) as ImageTexture;
        if (cached != null)
        {
            // A texture first seen small/distant was uploaded downsampled. Walking up to it used
            // to leave it blurry for the rest of the session, because this early return handed
            // back whatever was cached and nothing ever revisited the decision -- the "never
            // rebuilt at a different discard level" limitation this class used to document as a
            // deliberate scope limit. That reads as permanently soft textures on exactly the
            // nearby objects the user is looking at (live-reported 2026-08-01 on a foreground
            // pillar). The real viewer instead re-evaluates continuously and loads a sharper level
            // when an object's on-screen size grows (LLViewerLODTexture::processTextureStats ->
            // "current_discard < mDesiredDiscardLevel" handling), so do the same here.
            TryUpgradeCachedTexture(textureId, cached, assetService, generateMipmaps, screenPixelArea, priority);
            return Task.FromResult(cached)!;
        }

        if (assetService == null) return Task.FromResult<ImageTexture?>(null);

        var lazy = _inflightTextureUploads.GetOrAdd(textureId, id => new Lazy<Task<ImageTexture?>>(
            () => FetchAndUploadTextureAsync(id, assetService, generateMipmaps, initialRefCount, screenPixelArea, priority, rejectDegraded, bakeChannel, bakeAgentId),
            LazyThreadSafetyMode.ExecutionAndPublication));
        return lazy.Value;
    }

    /// <summary>Screen pixel area each cached texture's CURRENT upload was sized for, so a later
    /// request from closer up can tell that a sharper level is now warranted. Only holds entries
    /// for textures that were actually downsampled -- one uploaded at full resolution can never
    /// be improved, so it is never a candidate.</summary>
    private readonly ConcurrentDictionary<Guid, float> _uploadedForPixelArea = new();

    /// <summary>Fire-and-forget in-place sharpening of an already-cached texture, when the object
    /// requesting it now covers enough of the screen to deserve a lower discard level. Re-decodes
    /// (usually straight from AssetService's own decoded-data cache, so no network) and pushes the
    /// better image into the SAME <see cref="ImageTexture"/> via SetImage -- every material already
    /// referencing it picks the sharper version up automatically, so nothing has to re-wire
    /// materials or juggle refcounts.</summary>
    private void TryUpgradeCachedTexture(
        Guid textureId, ImageTexture cached, SLNG.Assets.AssetService? assetService,
        bool generateMipmaps, float screenPixelArea, float priority)
    {
        if (assetService == null || screenPixelArea <= 0f) return;
        if (!_uploadedForPixelArea.TryGetValue(textureId, out float builtFor)) return; // already full-res
        // Require a clear step up before paying for a re-decode: one discard level is a 4x area
        // change, so anything less than that can't lower the level and would just churn.
        if (screenPixelArea < builtFor * 4f)
        {
            // Deliberately silent. The first version of this logged every near-miss, which the
            // 4 Hz re-offer turned into 178k lines in one session -- the re-offer fires per texture
            // per tick forever in a static scene, so "growth that fell short" is the steady state,
            // not an event. Actual upgrades below are rare and worth a line; near-misses are not.
            return;
        }
        // Claim the upgrade so concurrent faces of the same object don't all start one.
        if (!_uploadedForPixelArea.TryUpdate(textureId, screenPixelArea, builtFor)) return;

        Logger.Info($"[GpuSharpen] {textureId.ToString()[..8]} {builtFor:0} -> {screenPixelArea:0} px^2 -- re-decoding");

        _ = Task.Run(async () =>
        {
            try
            {
                var textureData = await assetService.GetTextureAsync(textureId, desiredDiscard: 0, priority: priority).ConfigureAwait(false);
                if (textureData == null) return;

                MainThreadWorkQueue.Enqueue(MainThreadWorkQueue.Lane.Refine, () =>
                {
                    if (!GodotObject.IsInstanceValid(cached)) return;
                    
                    try
                    {
                        var image = Image.CreateFromData(textureData.Width, textureData.Height, false, Image.Format.Rgba8, textureData.Rgba);
                        image?.FixAlphaEdges();
                        if (image == null) return;
        
                        int discard = ComputeDiscardLevel(image.GetWidth(), image.GetHeight(), screenPixelArea);
                        if (discard > 0)
                        {
                            int targetW = Math.Max(8, image.GetWidth() >> discard);
                            int targetH = Math.Max(8, image.GetHeight() >> discard);
                            if (targetW < image.GetWidth() || targetH < image.GetHeight())
                                image.Resize(targetW, targetH, Image.Interpolation.Lanczos);
                        }
                        if (generateMipmaps) image.GenerateMipmaps();
        
                        int finalW = image.GetWidth(), finalH = image.GetHeight();
                        cached.SetImage(image);
                        image.Dispose();
                        
                        if (discard <= 0) _uploadedForPixelArea.TryRemove(textureId, out _);
                        Logger.Info($"[GpuSharpen] {textureId.ToString()[..8]} now discard={discard} uploaded={finalW}x{finalH}");
                    }
                    catch (Exception ex)
                    {
                        GD.PrintErr($"[GpuCache] texture {textureId} sharpen failed: {ex.Message}");
                    }
                }, label: "texture.sharpen");
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[GpuCache] texture {textureId} sharpen failed: {ex.Message}");
            }
        });
    }

    /// <summary>The real viewer's texel-to-screen-pixel discard criterion -- see the call site in
    /// <see cref="FetchAndUploadTextureAsync"/> for the full derivation and source citation.</summary>
    // Textures already reported by [GpuUpload].
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _uploadSizeLogged = new();

    private static int ComputeDiscardLevel(int width, int height, float screenPixelArea)
    {
        double texels = (double)width * height;
        int discard = (int)Math.Floor(Math.Log(texels / Math.Max(screenPixelArea, 1f)) / Math.Log(4.0));
        return Math.Clamp(discard, 0, SLNG.Net.J2kByteSizeEstimator.MaxDiscardLevel);
    }

    private async Task<ImageTexture?> FetchAndUploadTextureAsync(
        Guid textureId, SLNG.Assets.AssetService assetService, bool generateMipmaps, int initialRefCount, float screenPixelArea, float priority, bool rejectDegraded, int? bakeChannel = null, Guid bakeAgentId = default)
    {
        try
        {
            // desiredDiscard: 0 here, deliberately -- always fetch/decode the complete asset.
            // See this method's/GetOrUploadTextureAsync's doc comments for why network-side
            // truncation is disabled; the downsample below is purely local/post-decode.
            //
            // BUG-AVATAR-02: a bake channel goes through GetBakeTextureAsync, SL's dedicated
            // bake-texture host -- the generic per-face path (GetTextureAsync) got a flat HTTP 403
            // AccessDenied for every single bake channel, confirmed against a real Firestorm
            // capture of the same texture id succeeding from a different host entirely. See
            // GridSession.FetchBakeTextureDataAsync's doc comment for the full story.
            var textureData = bakeChannel.HasValue
                ? await assetService.GetBakeTextureAsync(textureId, bakeChannel.Value, priority, bakeAgentId).ConfigureAwait(false)
                : await assetService.GetTextureAsync(textureId, desiredDiscard: 0, priority: priority, rejectDegraded: rejectDegraded).ConfigureAwait(false);
            if (textureData == null) return null;

            // FATAL BUG, live on Aditi: this used to be Godot.Callable.From(() => {...}).CallDeferred()
            // -- a delegate-backed Callable's deferred dispatch is main-thread-only in Godot .NET
            // (see Boot.cs:70's comment on the exact same trap), and FetchAndUploadTextureAsync
            // is reached from asset-decode worker threads via GetOrUploadTextureAsync, not the
            // main thread. Crashed the whole process with a fatal
            // System.AccessViolationException inside godotsharp_callable_call_deferred. The fix
            // already existed two methods above (TryUpgradeCachedTexture's sharpen path) --
            // MainThreadWorkQueue, built for exactly this -- and this call site was simply never
            // migrated to it.
            var tcs = new TaskCompletionSource<ImageTexture?>();
            MainThreadWorkQueue.Enqueue(MainThreadWorkQueue.Lane.Visual, () =>
            {
                // Re-check: a differently-triggered Put for this id (shouldn't normally happen
                // given the single-flight dict above, but costs nothing to guard) may have
                // already landed between the await above and this deferred callback running.
                var raced = Get(textureId) as ImageTexture;
                if (raced != null)
                {
                    tcs.SetResult(raced);
                    return;
                }

                Image? image = null;
                ImageTexture? tex = null;
                try
                {
                    image = Image.CreateFromData(textureData.Width, textureData.Height, false, Image.Format.Rgba8, textureData.Rgba);
                    image?.FixAlphaEdges();
                    
                    if (image != null && screenPixelArea > 0f)
                    {
                        int discard = ComputeDiscardLevel(image.GetWidth(), image.GetHeight(), screenPixelArea);
                        // Behind --diag. It is one line per texture, which on a real region means
                        // a couple of thousand -- fine when it went to a terminal nobody was
                        // reading, but it now reaches godot.log (see ConsoleToGodotLog) and there
                        // it buries the handful of lines that say why something is broken. This is
                        // per-object asset logging, exactly what Diagnostics exists to gate.
                        if (Diagnostics.Enabled && _uploadSizeLogged.TryAdd(textureId, 0))
                            Console.Error.WriteLine($"[GpuUpload] {textureId} source={image.GetWidth()}x{image.GetHeight()} " +
                                $"screenPixelArea={screenPixelArea:F0} -> discard={discard} " +
                                $"uploaded={(discard > 0 ? $"{Math.Max(8, image.GetWidth() >> discard)}x{Math.Max(8, image.GetHeight() >> discard)}" : "full")}");
        
                        if (discard > 0)
                        {
                            int targetW = Math.Max(8, image.GetWidth() >> discard);
                            int targetH = Math.Max(8, image.GetHeight() >> discard);
                            if (targetW < image.GetWidth() || targetH < image.GetHeight())
                            {
                                image.Resize(targetW, targetH, Image.Interpolation.Lanczos);
                            }
                            _uploadedForPixelArea[textureId] = screenPixelArea;
                        }
                    }
        
                    if (generateMipmaps && image != null) image.GenerateMipmaps();
                    
                    if (image != null)
                    {
                        tex = ImageTexture.CreateFromImage(image);
                        if (tex != null)
                        {
                            long size = (long)tex.GetWidth() * tex.GetHeight() * 4;
                            Put(textureId, tex, size, initialRefCount);
                        }
                    }
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"[GpuUpload] Failed to process texture {textureId}: {ex.Message}");
                }
                finally
                {
                    image?.Dispose();
                }

                tcs.SetResult(tex);
            }, label: "texture.upload");

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
