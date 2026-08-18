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
        bool rejectDegraded = false)
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
            () => FetchAndUploadTextureAsync(id, assetService, generateMipmaps, initialRefCount, screenPixelArea, priority, rejectDegraded),
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
                // Budgeted rather than CallDeferred: SetImage is a full GPU texture upload on the
                // main thread, and a session routinely completes hundreds of these (816 in one
                // measured OSGrid session). Flushed unbudgeted they land in whatever frame they
                // happen to finish in, several at a time, and spike it. Refine lane because the
                // object is already on screen -- a slightly soft texture for another frame or two
                // costs nothing, whereas delaying an object that has not appeared yet is visible.
                MainThreadWorkQueue.Enqueue(MainThreadWorkQueue.Lane.Refine, () =>
                {
                    if (!GodotObject.IsInstanceValid(cached)) return;
                    cached.SetImage(image);
                    if (discard <= 0) _uploadedForPixelArea.TryRemove(textureId, out _);
                    Logger.Info($"[GpuSharpen] {textureId.ToString()[..8]} now discard={discard} " +
                                $"uploaded={finalW}x{finalH}");
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
        Guid textureId, SLNG.Assets.AssetService assetService, bool generateMipmaps, int initialRefCount, float screenPixelArea, float priority, bool rejectDegraded)
    {
        try
        {
            // desiredDiscard: 0 here, deliberately -- always fetch/decode the complete asset.
            // See this method's/GetOrUploadTextureAsync's doc comments for why network-side
            // truncation is disabled; the downsample below is purely local/post-decode.
            var textureData = await assetService.GetTextureAsync(textureId, desiredDiscard: 0, priority: priority, rejectDegraded: rejectDegraded).ConfigureAwait(false);
            if (textureData == null) return null;

            // Image/mipmap build happens on this (worker) thread, matching the threading rule in
            // AGENTS.md -- only the final Resource creation + cache Put below touches the main
            // thread, via CallDeferred.
            var image = Image.CreateFromData(textureData.Width, textureData.Height, false, Image.Format.Rgba8, textureData.Rgba);

            // Bleed visible colour outwards into the fully-transparent texels before anything
            // downsamples this image. A transparent texel still HAS an RGB value, and in real SL
            // content it is routinely arbitrary garbage left over from whatever the artist painted
            // under the alpha mask -- bright orange in the case that motivated this. Both bilinear
            // filtering and GenerateMipmaps below average RGB and A as independent channels, so
            // that invisible garbage gets mixed into every partially-transparent edge texel and
            // resurfaces as coloured speckles along the silhouette. It is most obvious on alpha-
            // heavy content viewed at a distance (more mip levels in play): black hair fringed
            // with orange dots. FixAlphaEdges is Godot's own remedy for exactly this -- it is what
            // the engine's texture importer applies by default as "Fix Alpha Border" -- but
            // nothing applies it to textures we build at runtime, so it has to happen here.
            // Deliberately unconditional rather than gated on a DetectAlpha() check: that verdict
            // is unreliable (see the notes in AvatarRenderer.ApplyAlphaCutout) and this is a no-op
            // on an image with no transparent texels anyway.
            image?.FixAlphaEdges();

            // FEAT-PERF-02: shrink the fully-decoded image before it ever reaches the GPU, for a
            // distant/small object that doesn't need full resolution on screen. Each discard
            // level halves both dimensions (SL/OpenSim discard semantics -- see
            // J2kByteSizeEstimator's doc comment), floored at 8px so GenerateMipmaps always has
            // a sane base level to work from.
            //
            // The level is derived here, not by the caller, because it depends on THIS texture's
            // decoded resolution. Ported from the real viewer's LLViewerLODTexture::
            // processTextureStats (llviewertexture.cpp):
            //     discard = floor( log(mTexelsPerImage / mMaxVirtualSize) / log(4) )
            // i.e. compare the texture's texel count against the pixel area it actually covers on
            // screen, in log-4 space because one discard level is a 4x area reduction. Full
            // resolution results whenever the texture has no more texels than it has screen pixels
            // to fill -- the "roughly one texel per pixel" criterion. The previous code instead had
            // the caller pick a level from hardcoded `radius / distance` thresholds that never saw
            // the texture's resolution at all, so a 64x64 and a 1024x1024 texture on the same prim
            // were shrunk identically, and even a large nearby prim was routinely halved or
            // quartered (user-reported "mega blurry", 2026-08-01).
            if (image != null && screenPixelArea > 0f)
            {
                int discard = ComputeDiscardLevel(image.GetWidth(), image.GetHeight(), screenPixelArea);

                // What actually reaches the GPU, once per texture. The sculpt pipeline has been
                // measured equal to the viewer's, so the remaining difference has to be between
                // the decoded image and the sampled texel -- and this is the only non-trivial step
                // in between. It also separates the two halves of the report that have been
                // treated as one fault: "blurry" would be a large discard here, "misplaced" would
                // not show up at all.
                if (_uploadSizeLogged.TryAdd(textureId, 0))
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
                    // Remember what this upload was sized for, so approaching the object later can
                    // detect that a sharper level is warranted -- see TryUpgradeCachedTexture.
                    // Only downsampled uploads are tracked; a full-res one can never improve.
                    _uploadedForPixelArea[textureId] = screenPixelArea;
                }
            }

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
                    // Actual (possibly downsampled -- see above) dimensions, not textureData's
                    // original ones, so the VRAM budget this cache enforces reflects what's
                    // really on the GPU.
                    long size = (long)tex.GetWidth() * tex.GetHeight() * 4;
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
