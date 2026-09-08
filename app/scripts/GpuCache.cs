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
        // caller produced from a gap-filled decode -- but only a DEGRADED upload is a problem.
        // The old blanket `rejectDegraded ? null` made every avatar face bypass this GPU cache
        // entirely and re-decode its texture from disk on every material rebuild (terse updates /
        // animation changes rebuild avatar faces constantly): ~90 ms J2K decode x thousands of
        // repeat requests = a multi-minute wait for a familiar, fully-cached scene (live,
        // 2026-09-03, [TexPipe] req climbing past 2600 with ~99.9% disk-cache hits). A CLEAN
        // cached upload is safe for everyone; only re-fetch when this id's cached upload is
        // recorded as coming from a degraded decode.
        var cached = Get(textureId) as ImageTexture;
        bool bypassedDegraded = cached != null && rejectDegraded && _uploadFromDegraded.ContainsKey(textureId);
        if (bypassedDegraded) cached = null;
        MaybeDumpGpuCacheStats(cached != null, bypassedDegraded);
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

    /// <summary>Texture ids whose currently-cached <see cref="ImageTexture"/> was built from a
    /// gap-filled (degraded) decode. A <c>rejectDegraded</c> caller re-fetches these through
    /// AssetService; a clean cached upload is reusable by everyone. Cleared when the entry is
    /// evicted or re-uploaded clean.</summary>
    private readonly ConcurrentDictionary<Guid, byte> _uploadFromDegraded = new();

    // Diagnostic (2026-09-03): is the GPU texture cache actually serving repeat requests?
    // [TexPipe] req kept climbing into the thousands for a static scene even after the
    // rejectDegraded-bypass fix; this says whether GetOrUploadTextureAsync hits its own _cache.
    private int _gpuGet, _gpuGetHit, _gpuGetBypassDegraded;

    // Time-gated, NOT every-N-gets. The first version dumped every 200 gets on the assumption that
    // a session makes a few thousand; a live v0.20.52 session made **7.2 million** (ObjectRenderer
    // re-offers every used texture id of every culled object at 4 Hz), so it wrote 45 061 of the
    // log's 49 273 lines and buried everything else. One line every 10 s is enough to watch a
    // trend and cannot swamp the log however hot the call site turns out to be.
    private static readonly System.Diagnostics.Stopwatch _gpuStatsClock = System.Diagnostics.Stopwatch.StartNew();
    private long _gpuStatsNextMs;
    private const long GpuStatsIntervalMs = 10_000;

    private void MaybeDumpGpuCacheStats(bool hit, bool bypassedDegraded)
    {
        int n = System.Threading.Interlocked.Increment(ref _gpuGet);
        if (hit) System.Threading.Interlocked.Increment(ref _gpuGetHit);
        if (bypassedDegraded) System.Threading.Interlocked.Increment(ref _gpuGetBypassDegraded);

        long now = _gpuStatsClock.ElapsedMilliseconds;
        long due = System.Threading.Interlocked.Read(ref _gpuStatsNextMs);
        if (now < due) return;
        // Claim the slot so a burst of concurrent gets emits one line, not one per thread.
        if (System.Threading.Interlocked.CompareExchange(ref _gpuStatsNextMs, now + GpuStatsIntervalMs, due) != due) return;
        int entries, degraded, pinned; long sizeMb, pinnedMb;
        lock (_cache)
        {
            entries = _cache.Count;
            sizeMb = _currentSize >> 20;
            // pinned = entries EvictIfNeeded can never reclaim (RefCount > 0). AvatarRenderer has
            // no AddRef/ReleaseRef bookkeeping and Puts every avatar face texture with
            // initialRefCount: 1 at FULL resolution, so this number is the whole question: if
            // pinnedMB approaches the budget, the cache has no room left for object textures, they
            // are evicted the moment they land, and ObjectRenderer's 4 Hz texture re-offer
            // re-decodes them forever -- which is what [TexPipe] req=8800 on a static scene looks
            // like. See docs/specs/BUG-NET-11-*.md.
            pinned = 0; long pinnedBytes = 0;
            foreach (var e in _cache.Values)
            {
                if (e.RefCount > 0) { pinned++; pinnedBytes += e.Size; }
            }
            pinnedMb = pinnedBytes >> 20;
        }
        degraded = _uploadFromDegraded.Count;
        Console.Error.WriteLine($"[GpuCache] get={n} hit={_gpuGetHit} bypassDegraded={_gpuGetBypassDegraded} " +
            $"entries={entries} sizeMB={sizeMb}/{_maxSize >> 20} pinned={pinned} pinnedMB={pinnedMb} " +
            $"degradedIds={degraded} mainQueue={MainThreadWorkQueue.Depth}");
    }

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
                // Pass the NEW (larger) screenPixelArea, not 0: this is a re-decode for a closer
                // view, and the decoder should still skip the levels even that closer view cannot
                // show. Sharpening a distant texture by one step should not cost a full 47.6 ms
                // decode when 13.1 ms buys everything the screen can display.
                var textureData = await assetService.GetTextureAsync(textureId, desiredDiscard: 0, priority: priority, screenPixelArea: screenPixelArea).ConfigureAwait(false);
                if (textureData == null) return;

                // BUG-NET-11: same split as FetchAndUploadTextureAsync -- the image build happens
                // here on the worker, the queued main-thread item does nothing but hand the
                // finished pixels to the live ImageTexture.
                var prepared = await PrepareImageAsync(textureId, textureData, generateMipmaps, screenPixelArea)
                    .ConfigureAwait(false);
                if (prepared.Image == null) return;

                MainThreadWorkQueue.Enqueue(MainThreadWorkQueue.Lane.Refine, () =>
                {
                    var image = prepared.Image;
                    if (!GodotObject.IsInstanceValid(cached))
                    {
                        image.Dispose();
                        return;
                    }

                    try
                    {
                        int finalW = image.GetWidth(), finalH = image.GetHeight();
                        cached.SetImage(image);

                        // UploadedForPixelArea is 0 exactly when the re-decode came out at full
                        // resolution, i.e. there is nothing left to sharpen -- drop the entry so
                        // this texture stops being an upgrade candidate for the rest of the session.
                        if (prepared.UploadedForPixelArea <= 0f) _uploadedForPixelArea.TryRemove(textureId, out _);
                        Logger.Info($"[GpuSharpen] {textureId.ToString()[..8]} now uploaded={finalW}x{finalH}");
                    }
                    catch (Exception ex)
                    {
                        GD.PrintErr($"[GpuCache] texture {textureId} sharpen failed: {ex.Message}");
                    }
                    finally
                    {
                        image.Dispose();
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

    /// <summary>Each texture's <see cref="Image.AlphaMode"/>, computed ON THE WORKER THREAD while
    /// the decoded image is already in hand.
    ///
    /// <para>It exists because asking for it later is ruinously expensive: <c>ImageTexture.GetImage()</c>
    /// pulls the whole texture back from VRAM and <c>DetectAlpha()</c> then scans every pixel, and
    /// <c>ObjectRenderer.ApplyAlphaCutout</c> was doing exactly that on the MAIN thread, once per
    /// textured face. Measured live 2026-09-03: <c>[WorkCost] prim.legacy_default_face n=430
    /// totalMs=2035.6 avgMs=4.73</c> over a 5 s window -- <b>41 % of all wall clock</b>, the single
    /// largest cost in the client, and roughly 50x what the texture uploads it was competing with
    /// cost (37.5 ms). Computed here it is free: the pixels are already decoded, on a worker, and
    /// the answer never changes for a given texture id.</para>
    ///
    /// <para>Deliberately NOT cleared on eviction. It is one enum per texture id, and a texture that
    /// comes back is the same texture.</para></summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Image.AlphaMode> _alphaModes = new();

    /// <summary>The alpha mode of an already-uploaded texture, without touching the GPU.
    /// False when this id has not been uploaded through this cache yet.</summary>
    public static bool TryGetAlphaMode(Guid textureId, out Image.AlphaMode mode)
        => _alphaModes.TryGetValue(textureId, out mode);

    // One implementation, shared with AssetService's pre-decode reduce-level choice: two copies of
    // this arithmetic that disagreed would decode a texture small and then treat it as full
    // resolution (permanently blurry), or the reverse.
    private static int ComputeDiscardLevel(int width, int height, float screenPixelArea)
        => SLNG.Assets.TextureLod.DiscardLevelFor(width, height, screenPixelArea);

    /// <summary>The CPU half of a texture upload: decode-buffer to <see cref="Image"/>, alpha-edge
    /// fix, optional LOD downsample, mipmaps. Everything here is pure pixel work on a
    /// <see cref="Image"/>'s own byte array with no RenderingServer involvement, so it belongs on a
    /// worker thread -- see the call site for what leaving it on the main thread cost.</summary>
    private readonly record struct PreparedImage(Image? Image, float UploadedForPixelArea);

    /// <summary>Bounds the concurrent CPU image work the same way AssetService bounds J2K decode.
    /// An unbounded <c>Task.Run</c> per texture is what made the decode path's measured "91 ms"
    /// mostly thread-pool queueing rather than real work (BUG-NET-11, v0.20.48); do not repeat it
    /// one stage later. Two cores left for the Godot main thread + GC.</summary>
    private static readonly SemaphoreSlim _imagePrepGate =
        new(Math.Max(2, System.Environment.ProcessorCount - 2));

    private static async Task<PreparedImage> PrepareImageAsync(
        Guid textureId, SLNG.Assets.TextureData textureData, bool generateMipmaps, float screenPixelArea)
    {
        // Task.Run unconditionally, even though FetchAndUploadTextureAsync is normally already on a
        // worker: GetTextureAsync returns a COMPLETED task on an AssetService memory-cache hit, so
        // the await above resumes inline on whatever thread called GetOrUploadTextureAsync -- and
        // ObjectRenderer calls it straight from _Process's cull sweep. Without this hop that case
        // would run the whole image build on the main thread with no budget at all, which is worse
        // than the queued version it replaces.
        await _imagePrepGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                Image? image = null;
                float uploadedFor = 0f;
                try
                {
                    image = Image.CreateFromData(textureData.Width, textureData.Height, false,
                        Image.Format.Rgba8, textureData.Rgba);
                    image?.FixAlphaEdges();

                    // The decoder may already have reduced this (SourceWidth > Width). Such an
                    // upload is NOT full resolution and must stay eligible for sharpening, even
                    // when the leftover local discard below works out to 0 -- otherwise a texture
                    // first seen from far away is decoded small and then treated as if it were the
                    // whole asset, i.e. permanently blurry.
                    if (textureData.SourceWidth > textureData.Width || textureData.SourceHeight > textureData.Height)
                        uploadedFor = screenPixelArea;

                    if (image != null && screenPixelArea > 0f)
                    {
                        // Computed on the ALREADY-REDUCED image, so it yields exactly the discard
                        // levels the decoder did not cover (the formula drops by 2 per reduce
                        // factor, which is the same 4x-per-level relationship).
                        int discard = ComputeDiscardLevel(image.GetWidth(), image.GetHeight(), screenPixelArea);
                        // Behind --diag. It is one line per texture, which on a real region means
                        // a couple of thousand -- fine when it went to a terminal nobody was
                        // reading, but it now reaches godot.log (see ConsoleToGodotLog) and there
                        // it buries the handful of lines that say why something is broken. This is
                        // per-object asset logging, exactly what Diagnostics exists to gate.
                        if (Diagnostics.Enabled && _uploadSizeLogged.TryAdd(textureId, 0))
                            Console.Error.WriteLine($"[GpuUpload] {textureId} asset={textureData.SourceWidth}x{textureData.SourceHeight} " +
                                $"decoded={image.GetWidth()}x{image.GetHeight()} " +
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
                            uploadedFor = screenPixelArea;
                        }
                    }

                    // Before mipmaps, so the scan covers the base level only -- the mips are
                    // derived from it and add nothing but work. See _alphaModes for why this is
                    // computed here and not where it is used.
                    if (image != null) _alphaModes[textureId] = image.DetectAlpha();

                    if (generateMipmaps && image != null) image.GenerateMipmaps();
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"[GpuUpload] Failed to process texture {textureId}: {ex.Message}");
                    image?.Dispose();
                    image = null;
                    uploadedFor = 0f;
                }
                return new PreparedImage(image, uploadedFor);
            }).ConfigureAwait(false);
        }
        finally
        {
            _imagePrepGate.Release();
        }
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
                // screenPixelArea is now passed THROUGH to the decoder (BUG-NET-11): it skips the
                // wavelet levels this object cannot show, which is 47.6 ms -> 13.1 ms at quarter
                // size and 3.8 ms at a sixteenth on real assets. desiredDiscard stays 0 -- that
                // parameter is the disabled NETWORK-side truncation, a different mechanism that
                // fails because Magick will not read a codestream that ends early; a reduce level
                // is a decoder option on the complete bytes. The local downsample below still runs
                // on whatever is left over, because one reduce factor covers two discard levels.
                : await assetService.GetTextureAsync(textureId, desiredDiscard: 0, priority: priority, rejectDegraded: rejectDegraded, screenPixelArea: screenPixelArea).ConfigureAwait(false);
            if (textureData == null) return null;

            // Record whether this upload came from a gap-filled decode, so a later rejectDegraded
            // caller knows to re-fetch it (and a clean one is reusable by all). A rejectDegraded
            // caller can never land here with IsDegraded == true (AssetService returns null), so
            // this only ever MARKS from a lenient caller and CLEARS when a clean decode replaces it.
            if (textureData.IsDegraded) _uploadFromDegraded[textureId] = 0;
            else _uploadFromDegraded.TryRemove(textureId, out _);

            // FATAL BUG, live on Aditi: this used to be Godot.Callable.From(() => {...}).CallDeferred()
            // -- a delegate-backed Callable's deferred dispatch is main-thread-only in Godot .NET
            // (see Boot.cs:70's comment on the exact same trap), and FetchAndUploadTextureAsync
            // is reached from asset-decode worker threads via GetOrUploadTextureAsync, not the
            // main thread. Crashed the whole process with a fatal
            // System.AccessViolationException inside godotsharp_callable_call_deferred. The fix
            // already existed two methods above (TryUpgradeCachedTexture's sharpen path) --
            // MainThreadWorkQueue, built for exactly this -- and this call site was simply never
            // migrated to it.
            // BUG-NET-11: everything except the actual GPU upload runs HERE, on the worker
            // thread, not inside the main-thread work item below. Image.CreateFromData +
            // FixAlphaEdges + Resize + GenerateMipmaps are pure CPU work on a PackedByteArray --
            // Godot's Image is one of the data types explicitly safe to build off-thread -- but
            // they were all inside the queued item, which made ONE texture cost ~10-30 ms of main
            // thread for a 1024x1024. MainThreadWorkQueue's budget is 3 ms/frame and it always
            // runs at least one item per lane per frame, so an item that big means exactly ONE
            // texture upload per frame: at 30-60 fps a scene with ~8800 texture requests (measured
            // live 2026-09-03, [TexPipe] req=8800) takes 2.5-5 minutes to finish appearing no
            // matter how fast the decode is. That is the "textures come in extremely slowly even
            // though they're all cached" report -- the queue, not the decoder, was the serialising
            // stage. Leaving only ImageTexture.CreateFromImage + Put on the main thread cuts the
            // per-item cost to a fraction of a millisecond, so the same 3 ms budget now drains
            // many uploads per frame. It is also what AGENTS.md's non-negotiable #2 asks for.
            var prepared = await PrepareImageAsync(textureId, textureData, generateMipmaps, screenPixelArea)
                .ConfigureAwait(false);

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
                var image = prepared.Image;

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

                ImageTexture? tex = null;
                try
                {
                    if (image != null)
                    {
                        tex = ImageTexture.CreateFromImage(image);
                        if (tex != null)
                        {
                            // Only recorded once the upload actually exists, so a raced/failed
                            // build can never leave TryUpgradeCachedTexture thinking a texture
                            // was uploaded downsampled when nothing was uploaded at all.
                            if (prepared.UploadedForPixelArea > 0f)
                                _uploadedForPixelArea[textureId] = prepared.UploadedForPixelArea;

                            long size = (long)tex.GetWidth() * tex.GetHeight() * 4;
                            Put(textureId, tex, size, initialRefCount);
                        }
                    }
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"[GpuUpload] Failed to upload texture {textureId}: {ex.Message}");
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

    /// <summary>BUG-AVATAR-01: drop a cached entry outright, ignoring its refcount, so the next
    /// <see cref="GetOrUploadTextureAsync"/> re-downloads and re-decodes it from scratch. Used by
    /// "Avatar neu backen" / Ctrl+Alt+R to force the self bake textures to re-fetch -- a stale,
    /// stuck or previously-failed bake channel is the "blank avatar" symptom that button exists
    /// for -- and, like the reference viewer's <c>forceBakeAllTextures</c>, to give the visible
    /// "kurz grau, dann frisch" the user sees in Firestorm. An entry still referenced by live
    /// nodes is un-indexed here but left for <see cref="ReleaseRef"/> to dispose when the owner
    /// rebuilds and drops its ref; only an unreferenced entry is disposed immediately.</summary>
    public void Forget(Guid id)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(id, out var entry))
            {
                if (entry.Node != null) _lruList.Remove(entry.Node);
                _cache.Remove(id);
                _uploadFromDegraded.TryRemove(id, out _);
                _currentSize -= entry.Size;
                if (entry.RefCount <= 0 && GodotObject.IsInstanceValid(entry.Res))
                {
                    entry.Res.Dispose();
                }
            }
            _pendingRefDelta.Remove(id);
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
                _uploadFromDegraded.TryRemove(entry.Id, out _);
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
