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

        // FEAT-PERF-25: what the shrink pass ranks on, kept here so its scan never calls into Godot.
        public int Width, Height;              // the texture's pixels now (0: not a texture)
        public int SourceWidth, SourceHeight;  // the asset's own size, when the upload knew it
        public bool Mipmaps;
        public bool EverHadArea;               // some caller gave a screen area: the texture has LOD information
        public long LastSeenMs;                // the last request for it
        public float RecentArea;               // largest screen area asked for within AreaWindowMs...
        public long RecentAreaMs;              // ...and when it was last asked for
        public long ShrinkBlockedUntilMs;      // a shrink that could not be done is not tried again before this
        public long LastSharpenMs;
        public long LastShrinkMs;
        public long SharpenGuardUntilMs;       // shrunk from the visible tier: no sharpen-for-room before this
    }

    /// <summary>FEAT-PERF-25: the shape of a texture going into the cache (see <see cref="CacheEntry"/>).</summary>
    private readonly record struct TextureShape(int Width, int Height, int SourceWidth, int SourceHeight, bool Mipmaps, float ScreenPixelArea);

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

    // FEAT-PERF-04: no longer readonly -- SetBudget applies the graphics-page slider at runtime.
    private long _maxSize;

    // FEAT-PERF-04: texture ids that must never be shrunk by the back-pressure pass -- avatar
    // faces and bake channels (rejectDegraded / bakeChannel callers). A parcel full of scenery
    // filling the cache must not soften someone's face at conversation distance.
    private readonly ConcurrentDictionary<Guid, byte> _noShrink = new();

    // BUG-PERF-13: all avatar textures (worn meshes and bakes). Distant avatar textures are reducible
    // and can be shrunk when over budget; only self and the nearest N avatars are protected in _noShrink.
    private readonly ConcurrentDictionary<Guid, byte> _avatarTextures = new();
    private readonly ConcurrentDictionary<Guid, byte> _bakeTextures = new();

    // BUG-PERF-09: textures with a shrink in flight, and the bytes each is expected to give back.
    // The shrink now prepares on a worker, so several can be pending at once; Tick has to count what
    // they will free or it keeps picking more than the budget needs.
    private readonly ConcurrentDictionary<Guid, PendingShrink> _shrinkPending = new();
    private readonly record struct PendingShrink(long Saving, long StartedMs, bool Visible = false);

    // FEAT-PERF-25: bake textures of the protected (self + nearest full) avatars. Kept apart from
    // _noShrink, which only ever holds the own avatar's bakes (BUG-PERF-13's contract); a remote bake is
    // spared by the shrink pass while its wearer is one of the nearest, and is shrinkable again once not.
    private readonly ConcurrentDictionary<Guid, byte> _protectedBakes = new();

    // FEAT-PERF-25: where a bake came from, so a shrunk bake can be fetched sharp again (bakes come from
    // the bake host, not the per-face texture path) and a shrink can find its pixels.
    private readonly ConcurrentDictionary<Guid, (int Channel, Guid Agent)> _bakeSource = new();

    private const double LowWater = 0.85;              // hysteresis: only relax below this fraction of budget
    private const int MaxLodBias = 2;                  // at most 2 extra discard levels (16x) from back-pressure; also the cap on the per-upload admission discard
    private const long ShrinkFloorBytes = 512 * 1024;  // don't bother shrinking anything already this small
    private const int ShrinkPerTick = 2;              // mirrors MainThreadWorkQueue's Refine-lane 2/frame cap
    private const int MaxShrinksInFlight = 16;        // prepared-on-a-worker shrinks waiting for the Refine lane
    private const long BiasChangeCooldownMs = 3000;   // don't pump the bias

    // FEAT-PERF-25: the shrink pass ranks what it gives back, instead of taking the first two entries
    // at the head of the LRU list -- see RebuildShrinkCandidates.
    private const long AreaWindowMs = 6000;            // ObjectRenderer offers every 2 s, AvatarRenderer every 1 s
    private const long ScanIntervalMs = 500;           // a ranked candidate list lasts this long...
    private const long ScanRefreshMs = 2000;           // ...or this long when it is not used up
    private const int MaxCandidates = 96;
    private const long ShrinkTimeoutMs = 20_000;       // an in-flight shrink older than this is given up
    private const long ShrinkBlockedMs = 60_000;       // a texture that could not be shrunk is left alone this long
    private const long SharpenShrinkGuardMs = 30_000;  // never shrink what was sharpened this recently
    private const double VisibleShrinkOver = 1.05;     // right-sized small textures are shrunk only this far over budget
    private const long NearOvershootMinBytes = 32L << 20;
    private const long ShrinkStatsIntervalMs = 10_000;
    private static readonly System.Diagnostics.Stopwatch _biasClock = System.Diagnostics.Stopwatch.StartNew();
    private long _nextBiasChangeMs;

    // BUG-PERF-09: admission control. _reservedBytes is what has been admitted (prepared on a worker
    // or waiting for the main thread to upload it) but is not in _currentSize yet. resident + reserved
    // is the PROJECTED size an arriving texture is judged against -- see AdmitAndReserve. Guarded by
    // _admitLock, never held together with _cache's lock.
    private readonly object _admitLock = new();
    private long _reservedBytes;
    private int _admitCut;                             // textures that took extra discard since the last stats line
    private long _admitSavedBytes;                     // ...and the bytes that spared
    private bool _exemptOverBudgetLogged;

    // The screen area a texture was REQUESTED for when admission made it smaller than that, so a
    // re-sharpen can tell "the camera came closer" (needs room made: see _roomWanted) from "this was
    // cut at upload and there may be room now" (waits for room, asks for nothing).
    private readonly ConcurrentDictionary<Guid, float> _admissionRequestedArea = new();

    // Bytes the most recent sharpen that the camera's approach asked for was short of. Tick turns it
    // into shrinks of the least recently used textures, so near textures can sharpen in a full cache.
    private long _roomWanted;

    // FEAT-PERF-25: the ranked shrink candidates of the last scan, best first, and how far Tick has got
    // through them. Main thread, under _cache's lock.
    private readonly List<CacheEntry> _shrinkCandidates = new();
    private int _candidateCursor;
    private long _lastScanMs = long.MinValue / 2;
    private readonly PriorityQueue<CacheEntry, double> _scanFree = new();
    private readonly PriorityQueue<CacheEntry, double> _scanVisible = new();
    private readonly List<(CacheEntry entry, ImageTexture tex)> _toShrink = new();

    // FEAT-PERF-25: bytes the last scan found it could give back without a visible loss (cold, or more
    // texels than the screen shows). A NEAR texture may be admitted that far over the budget -- it is
    // what the user is looking at, and the shrink pass pays the overshoot back from those bytes.
    private long _reclaimableBytes;

    // FEAT-PERF-25: what the shrink pass did, for the [GpuShrink] line. Counters are reset per line;
    // the skip table is the last scan's. Workers touch the counters, so Interlocked.
    private enum Skip { Mesh, Unreferenced, Small, NoLod, Protected, Pending, Blocked, Sharpened, Undersized, Count }
    private readonly long[] _scanSkipBytes = new long[(int)Skip.Count];
    private readonly int[] _scanSkipCount = new int[(int)Skip.Count];
    private int _scanSeen, _scanFreeCount, _scanVisibleCount;
    private long _scanVisibleBytes;
    private int _statScans, _statStarted, _statDone, _statNoSource, _statTimedOut, _statRaced, _statFailed;
    private long _statFreedBytes, _statOverMs, _statRoomWantedMax;
    private long _nextShrinkStatsMs = ShrinkStatsIntervalMs;
    private long _lastTickMs;

    // A sharpen that ended without uploading anything (no room after all, or it failed) is not retried
    // until this time (ms on _biasClock). Without it such a texture was re-decoded at every 4 Hz
    // re-offer: 42,000 sharpen decodes in 6 minutes for 1,700 textures (BUG-PERF-09 in-world run).
    private readonly ConcurrentDictionary<Guid, long> _sharpenRetryAtMs = new();
    private const long SharpenRetryCooldownMs = 5000;

    // FEAT-PERF-17: OS memory reported by DXGI probe or fallback
    private long _osBudgetBytes;
    private long _osUsageBytes;
    private bool _autoBudget = true;
    private long _lastLoggedBudget;

    // The one AssetService every renderer shares. A shrink needs it to find the texture's decoded
    // pixels; it is remembered from the upload calls rather than passed to Tick.
    private SLNG.Assets.AssetService? _assets;

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

    /// <summary>FEAT-PERF-04: bytes the cache currently holds (textures + meshes). For the live
    /// "used" readout next to the texture-memory slider.</summary>
    public long CurrentSizeBytes
    {
        get { lock (_cache) return _currentSize; }
    }

    /// <summary>BUG-PERF-16: True when current cache size exceeds maximum budget.</summary>
    public bool IsOverBudget
    {
        get { lock (_cache) return _currentSize > _maxSize; }
    }

    /// <summary>Bytes the cache is allowed to hold (textures + meshes).</summary>
    public long MaxSizeBytes => Interlocked.Read(ref _maxSize);

    public bool AutoBudget
    {
        get => _autoBudget;
        set => _autoBudget = value;
    }

    public void ReportOsMemory(long budgetBytes, long usageBytes)
    {
        Interlocked.Exchange(ref _osBudgetBytes, budgetBytes);
        Interlocked.Exchange(ref _osUsageBytes, usageBytes);
    }

    public void ReportOsMemory(long budgetBytes, long usageBytes, bool auto)
    {
        _autoBudget = auto;
        ReportOsMemory(budgetBytes, usageBytes);
    }

    /// <summary>FEAT-PERF-04: change the texture/mesh budget at runtime (graphics-page slider). A
    /// lower budget takes effect immediately -- eviction runs now, and the per-frame
    /// <see cref="Tick"/> raises the LOD bias / shrinks resident textures until back under it.</summary>
    public void SetBudget(long maxSizeInBytes)
    {
        long clamped = Math.Max(64L * 1024 * 1024, maxSizeInBytes);
        long oldSize;
        lock (_cache)
        {
            if (clamped == _maxSize) return;
            oldSize = _maxSize;
            _maxSize = clamped;
            EvictIfNeeded();
        }

        long diff = Math.Abs(clamped - _lastLoggedBudget);
        if (_lastLoggedBudget == 0 || diff >= 128L * 1024 * 1024 || (_lastLoggedBudget > 0 && (double)diff / _lastLoggedBudget >= 0.05))
        {
            _lastLoggedBudget = clamped;
            Console.Error.WriteLine($"[GpuCache] budget set to {clamped >> 20} MB");
        }
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

    public Resource? Get(Guid id) => Lookup(id, 0f);

    /// <summary><see cref="Get"/>, plus FEAT-PERF-25's bookkeeping: when the texture was last asked for
    /// and the largest screen area it was asked for lately -- what the shrink pass ranks on.</summary>
    private Resource? Lookup(Guid id, float screenPixelArea)
    {
        lock (_cache)
        {
            if (!_cache.TryGetValue(id, out var entry)) return null;
            if (entry.Node != null)
            {
                _lruList.Remove(entry.Node);
                _lruList.AddLast(entry.Node);
            }
            long now = _biasClock.ElapsedMilliseconds;
            entry.LastSeenMs = now;
            NoteArea(entry, screenPixelArea, now);
            return entry.Res;
        }
    }

    /// <summary>The largest area within <see cref="AreaWindowMs"/>: a texture shared by a near and a far
    /// object is as near as its nearest user, and an area that stops being asked for ages out.</summary>
    private static void NoteArea(CacheEntry entry, float screenPixelArea, long now)
    {
        if (screenPixelArea <= 0f) return;
        entry.EverHadArea = true;
        if (screenPixelArea >= entry.RecentArea || now - entry.RecentAreaMs > AreaWindowMs)
        {
            entry.RecentArea = screenPixelArea;
            entry.RecentAreaMs = now;
        }
    }

    /// <summary>FEAT-PERF-25: AvatarRenderer's report of how large an avatar is on screen, for all of the
    /// textures it wears (avatar textures are not re-offered the way world textures are). Once a second
    /// per shown avatar.</summary>
    public void NoteAreas(HashSet<Guid> textureIds, float screenPixelArea)
    {
        if (screenPixelArea <= 0f || textureIds.Count == 0) return;
        long now = _biasClock.ElapsedMilliseconds;
        lock (_cache)
        {
            foreach (var id in textureIds)
            {
                if (!_cache.TryGetValue(id, out var entry)) continue;
                entry.LastSeenMs = now;
                NoteArea(entry, screenPixelArea, now);
            }
        }
    }

    public void Put(Guid id, Resource res, long size, int initialRefCount = 0) => Put(id, res, size, initialRefCount, default);

    private void Put(Guid id, Resource res, long size, int initialRefCount, TextureShape shape)
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
            long now = _biasClock.ElapsedMilliseconds;
            var entry = new CacheEntry
            {
                Id = id,
                Res = res,
                Size = size,
                RefCount = startRefCount,
                Width = shape.Width,
                Height = shape.Height,
                SourceWidth = shape.SourceWidth,
                SourceHeight = shape.SourceHeight,
                Mipmaps = shape.Mipmaps,
                LastSeenMs = now,
            };
            NoteArea(entry, shape.ScreenPixelArea, now);
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
        Guid bakeAgentId = default,
        bool isAvatar = false,
        bool isSelf = false)
    {
        if (textureId == Guid.Empty) return Task.FromResult<ImageTexture?>(null);
        if (assetService != null && !ReferenceEquals(_assets, assetService)) _assets = assetService;

        // BUG-PERF-13: Bake textures (bakeChannel != null) stay full-res and exempt from distance LOD.
        // Only the OWN avatar's textures go into _noShrink; remote bakes stay full-res at upload
        // but must NOT be in _noShrink so they can be evicted when unreferenced or shrunk when over budget.
        if (bakeChannel.HasValue)
        {
            screenPixelArea = 0f;
            _avatarTextures.TryAdd(textureId, 0);
            _bakeTextures.TryAdd(textureId, 0);
            _bakeSource[textureId] = (bakeChannel.Value, bakeAgentId);
            if (isSelf)
            {
                _noShrink.TryAdd(textureId, 0);
            }
        }
        else if (isAvatar)
        {
            _avatarTextures.TryAdd(textureId, 0);
            if (isSelf)
            {
                _noShrink.TryAdd(textureId, 0);
            }
        }

        // A rejectDegraded caller (the avatar) must not be handed an upload that some OTHER
        // caller produced from a gap-filled decode -- but only a DEGRADED upload is a problem.
        // The old blanket `rejectDegraded ? null` made every avatar face bypass this GPU cache
        // entirely and re-decode its texture from disk on every material rebuild (terse updates /
        // animation changes rebuild avatar faces constantly): ~90 ms J2K decode x thousands of
        // repeat requests = a multi-minute wait for a familiar, fully-cached scene (live,
        // 2026-09-03, [TexPipe] req climbing past 2600 with ~99.9% disk-cache hits). A CLEAN
        // cached upload is safe for everyone; only re-fetch when this id's cached upload is
        // recorded as coming from a degraded decode.
        // BUG-PERF-10: timed apart (MainThreadWorkQueue.Part; a no-op off the main thread), because a
        // texture request made from a queue item or the cull sweep is main-thread time, hit or miss.
        ImageTexture? cached;
        bool bypassedDegraded;
        using (MainThreadWorkQueue.Part("tex.lookup"))
        {
            cached = Lookup(textureId, screenPixelArea) as ImageTexture;
            bypassedDegraded = cached != null && rejectDegraded && _uploadFromDegraded.ContainsKey(textureId);
            if (bypassedDegraded) cached = null;
            MaybeDumpGpuCacheStats(cached != null, bypassedDegraded);
        }
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
            using (MainThreadWorkQueue.Part("tex.upgrade"))
                TryUpgradeCachedTexture(textureId, cached, assetService, generateMipmaps, screenPixelArea, priority);
            return Task.FromResult(cached)!;
        }

        if (assetService == null) return Task.FromResult<ImageTexture?>(null);

        // The start of a fetch runs inline up to its first await that does not complete at once.
        using (MainThreadWorkQueue.Part("tex.miss"))
        {
            var lazy = _inflightTextureUploads.GetOrAdd(textureId, id => new Lazy<Task<ImageTexture?>>(
                () => FetchAndUploadTextureAsync(id, assetService, generateMipmaps, initialRefCount, screenPixelArea, priority, rejectDegraded, bakeChannel, bakeAgentId),
                LazyThreadSafetyMode.ExecutionAndPublication));
            return lazy.Value;
        }
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
        int entries, degraded, pinned; long sizeMb, pinnedMb, exemptBytes = 0, avatarBytes = 0, reducibleBytes = 0;
        lock (_cache)
        {
            entries = _cache.Count;
            sizeMb = _currentSize >> 20;
            // pinned = entries EvictIfNeeded can never reclaim (RefCount > 0).
            pinned = 0; long pinnedBytes = 0;
            foreach (var e in _cache.Values)
            {
                if (e.RefCount > 0) { pinned++; pinnedBytes += e.Size; }
                if (_noShrink.ContainsKey(e.Id) || _protectedBakes.ContainsKey(e.Id)) exemptBytes += e.Size;
                if (_avatarTextures.ContainsKey(e.Id))
                {
                    avatarBytes += e.Size;
                    if (!_noShrink.ContainsKey(e.Id) && !_protectedBakes.ContainsKey(e.Id) && e.Size > ShrinkFloorBytes)
                    {
                        reducibleBytes += e.Size;
                    }
                }
            }
            pinnedMb = pinnedBytes >> 20;
        }
        degraded = _uploadFromDegraded.Count;

        // State CHANGES, not readings, so they stay outside the --diag gate below.
        bool exemptOver = exemptBytes > Interlocked.Read(ref _maxSize);
        if (exemptOver != _exemptOverBudgetLogged)
        {
            _exemptOverBudgetLogged = exemptOver;
            Console.Error.WriteLine(exemptOver
                ? $"[GpuCache] avatar/bake textures that are never shrunk ({exemptBytes >> 20} MB) exceed the whole texture budget ({_maxSize >> 20} MB) -- every world texture is now admitted at the cheapest level"
                : $"[GpuCache] avatar/bake textures that are never shrunk ({exemptBytes >> 20} MB) are back under the texture budget ({_maxSize >> 20} MB)");
        }
        int cut; long saved; long reserved;
        lock (_admitLock)
        {
            cut = _admitCut; saved = _admitSavedBytes; reserved = _reservedBytes;
            _admitCut = 0; _admitSavedBytes = 0;
        }
        if (cut > 0)
            Console.Error.WriteLine($"[GpuCache] admission: {cut} textures took extra discard in the last {GpuStatsIntervalMs / 1000} s " +
                $"(spared {saved >> 20} MB), resident {sizeMb} + in flight {reserved >> 20} of {_maxSize >> 20} MB");

        // A periodic stats dump, which is what the perf overlay is for. The budget and LOD-bias
        // lines below/above stay: those report a state CHANGE, not a reading.
        if (!Diagnostics.Enabled) return;
        long osBudMb = Interlocked.Read(ref _osBudgetBytes) >> 20;
        long osUseMb = Interlocked.Read(ref _osUsageBytes) >> 20;
        string autoStr = _autoBudget ? "true" : "false";
        Console.Error.WriteLine($"[GpuCache] get={n} hit={_gpuGetHit} bypassDegraded={_gpuGetBypassDegraded} " +
            $"entries={entries} sizeMB={sizeMb}/{_maxSize >> 20} inFlightMB={reserved >> 20} pinned={pinned} pinnedMB={pinnedMb} " +
            $"noShrinkMB={exemptBytes >> 20} avatarMB={avatarBytes >> 20} reducibleMB={reducibleBytes >> 20} " +
            $"lodBias={SLNG.Assets.TextureLod.GlobalLodBias} degradedIds={degraded} mainQueue={MainThreadWorkQueue.Depth} " +
            $"osBudgetMB={osBudMb} osUsageMB={osUseMb} auto={autoStr}");
    }

    /// <summary>BUG-PERF-09: records that a resident texture's pixels were replaced in place, so the
    /// cache's total follows what the card holds. Main thread (it follows a SetImage).</summary>
    private void SetResidentSize(Guid id, Resource res, long newSize, int width, int height, bool sharpened)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(id, out var live) && ReferenceEquals(live.Res, res))
            {
                _currentSize += newSize - live.Size;
                live.Size = newSize;
                live.Width = width;
                live.Height = height;
                long now = _biasClock.ElapsedMilliseconds;
                if (sharpened) live.LastSharpenMs = now;
                else live.LastShrinkMs = now;
            }
        }
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
        // A shrink is already rewriting this texture's pixels.
        if (_shrinkPending.ContainsKey(textureId)) return;

        // BUG-PERF-09: a sharper level is more bytes, and only worth a re-decode when the cache has
        // room for them. A texture admission made small stays eligible (see PrepareImage), so this
        // check -- not the built-for area -- is what keeps a full cache from re-decoding the same
        // texture four times a second just to throw the result away. Lock-free first: the common
        // answer while the cache is full is "no room at all".
        if (_sharpenRetryAtMs.TryGetValue(textureId, out long retryAt) && _biasClock.ElapsedMilliseconds < retryAt) return;

        long budget = Interlocked.Read(ref _maxSize);
        long projected = Interlocked.Read(ref _currentSize) + ReservedBytesSnapshot();
        // Two reasons a texture can be eligible. The camera came closer than the area it was
        // requested for: that is someone looking at it, and the least recently used textures give up
        // bytes for it (Tick). Or it was only cut at upload (or shrunk) and the request has not changed:
        // a FAR one of those waits for the cache to fall back below the low-water mark and asks for
        // nothing -- thousands of those, all asking, would keep the shrink pass running for ever, and a
        // sharpen that fills the cache to the brim would only be shrunk again.
        //
        // FEAT-PERF-25: a NEAR one does not wait. It was cut because the cache was full of something
        // else, and with the shrink pass holding the cache at its budget "below low water" never came:
        // the wall in front of the camera stayed blurry for the session. It asks for room like a camera
        // approach, and may go over the budget by what the shrink pass can give back without a visible
        // loss (NearCeiling) -- the far and the cold pay for what the user is looking at.
        bool cutAtUpload = _admissionRequestedArea.TryGetValue(textureId, out float requestedFor)
                           && screenPixelArea < requestedFor * 4f;
        bool near = screenPixelArea >= SLNG.Assets.TextureLod.BiasExemptAreaPx;
        bool waitsForRoom = cutAtUpload && !near;
        long limit = waitsForRoom ? (long)(budget * LowWater) : near ? NearCeiling(budget) : budget;
        if (waitsForRoom && projected >= limit) return; // the common blocked case, answered without a lock

        long replaced;
        long guardUntil;
        lock (_cache)
        {
            bool found = _cache.TryGetValue(textureId, out var resident);
            replaced = found ? resident!.Size : 0;
            guardUntil = found ? resident!.SharpenGuardUntilMs : 0;
        }
        if (replaced <= 0) return;
        // FEAT-PERF-25: shrunk from the visible tier a moment ago -- only a real approach sharpens it now.
        if (cutAtUpload && _biasClock.ElapsedMilliseconds < guardUntil) return;
        // One level up is 4x the bytes. If not even that fits, the decode would be thrown away; if it
        // fits, admission in PrepareImage cuts a bigger jump down to whatever does.
        long shortBy = projected + replaced * 3 - limit;
        if (shortBy > 0)
        {
            // The growth, not the shortfall: Tick frees until resident + in flight + this fits the budget.
            if (!waitsForRoom) NoteRoomWanted(replaced * 3);
            return;
        }

        // Claim the upgrade so concurrent faces of the same object don't all start one.
        if (!_uploadedForPixelArea.TryUpdate(textureId, screenPixelArea, builtFor)) return;

        _ = Task.Run(async () =>
        {
            // BUG-PERF-11: logged from the worker. This is reached from the cull sweep, and a teleport
            // starts over a thousand of these in one 5 s window (1,523 at Millenium); a GD.Print takes the
            // engine's log lock and the main thread queued behind the workers' own prints there
            // (frame/tex.upgrade: 1,014 ms in the window, one call 249 ms).
            Logger.Info($"[GpuSharpen] {textureId.ToString()[..8]} {builtFor:0} -> {screenPixelArea:0} px^2 -- re-decoding");
            try
            {
                // Pass the NEW (larger) screenPixelArea, not 0: this is a re-decode for a closer
                // view, and the decoder should still skip the levels even that closer view cannot
                // show. Sharpening a distant texture by one step should not cost a full 47.6 ms
                // decode when 13.1 ms buys everything the screen can display.
                // FEAT-PERF-25: a bake comes from the bake host (BUG-AVATAR-02); a shrunk far bake is
                // sharpened again from there, or from the memory cache that path fills.
                var textureData = _bakeSource.TryGetValue(textureId, out var bake)
                    ? await assetService.GetBakeTextureAsync(textureId, bake.Channel, priority, bake.Agent).ConfigureAwait(false)
                    : await assetService.GetTextureAsync(textureId, desiredDiscard: 0, priority: priority, screenPixelArea: screenPixelArea).ConfigureAwait(false);
                if (textureData == null)
                {
                    _uploadedForPixelArea.TryUpdate(textureId, builtFor, screenPixelArea);
                    _sharpenRetryAtMs[textureId] = _biasClock.ElapsedMilliseconds + SharpenRetryCooldownMs;
                    return;
                }

                // BUG-NET-11: same split as FetchAndUploadTextureAsync -- the image build happens
                // here on the worker, the queued main-thread item does nothing but hand the
                // finished pixels to the live ImageTexture.
                var prepared = await PrepareImageAsync(textureId, textureData, generateMipmaps, screenPixelArea, replaced)
                    .ConfigureAwait(false);
                if (prepared.Image == null)
                {
                    // FEAT-PERF-25: "nothing sharper" only stands when the LOD bias played no part. Under a
                    // raised bias the level the area asks for is the bias's, and letting the claim stand
                    // left the texture waiting for the camera to come 4x closer again after the bias was
                    // long gone -- the "stays blurry for a long time" of the 3.5 GB run.
                    bool biasInvolved = SLNG.Assets.TextureLod.GlobalLodBias > 0
                                        && screenPixelArea < SLNG.Assets.TextureLod.BiasExemptAreaPx;
                    if (!prepared.NothingSharper || biasInvolved)
                    {
                        // Nothing was uploaded because admission found no room (or the build failed):
                        // the texture is still as small as it was, so it stays a sharpen candidate --
                        // but not one to try again at the next re-offer.
                        _uploadedForPixelArea.TryUpdate(textureId, builtFor, screenPixelArea);
                        _sharpenRetryAtMs[textureId] = _biasClock.ElapsedMilliseconds + SharpenRetryCooldownMs;
                    }
                    // else: nothing sharper exists for this area, so the claim stands; the area has to
                    // grow another 4x before this is looked at again.
                    return;
                }

                MainThreadWorkQueue.Enqueue(MainThreadWorkQueue.Lane.Refine, () =>
                {
                    var image = prepared.Image;
                    if (!GodotObject.IsInstanceValid(cached))
                    {
                        image.Dispose();
                        ReleaseReservation(prepared.ReservedBytes);
                        return;
                    }

                    try
                    {
                        int finalW = image.GetWidth(), finalH = image.GetHeight();
                        cached.SetImage(image);
                        // BUG-PERF-09: the pixels changed, so the entry's size did. It used to stay at
                        // the downsampled size for ever, and the budget never saw a sharpened texture.
                        SetResidentSize(textureId, cached,
                            SLNG.Assets.TextureAdmission.TextureBytes(finalW, finalH, generateMipmaps), finalW, finalH, sharpened: true);

                        // UploadedForPixelArea is 0 exactly when the re-decode came out at full
                        // resolution, i.e. there is nothing left to sharpen -- drop the entry so
                        // this texture stops being an upgrade candidate for the rest of the session.
                        // Otherwise record what the new pixels actually serve: an admission cut can
                        // leave the sharper version below the area that was claimed above.
                        if (prepared.UploadedForPixelArea <= 0f) _uploadedForPixelArea.TryRemove(textureId, out _);
                        else _uploadedForPixelArea[textureId] = prepared.UploadedForPixelArea;
                        if (prepared.ExtraDiscard > 0) _admissionRequestedArea[textureId] = screenPixelArea;
                        else _admissionRequestedArea.TryRemove(textureId, out _);
                        _sharpenRetryAtMs.TryRemove(textureId, out _);
                        Logger.Info($"[GpuSharpen] {textureId.ToString()[..8]} now uploaded={finalW}x{finalH}");
                    }
                    catch (Exception ex)
                    {
                        GD.PrintErr($"[GpuCache] texture {textureId} sharpen failed: {ex.Message}");
                    }
                    finally
                    {
                        image.Dispose();
                        ReleaseReservation(prepared.ReservedBytes);
                    }
                }, label: "texture.sharpen");
            }
            catch (Exception ex)
            {
                // The claim above stands only for a sharpen that happened.
                _uploadedForPixelArea.TryUpdate(textureId, builtFor, screenPixelArea);
                _sharpenRetryAtMs[textureId] = _biasClock.ElapsedMilliseconds + SharpenRetryCooldownMs;
                GD.PrintErr($"[GpuCache] texture {textureId} sharpen failed: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// BUG-PERF-13: Checks whether a texture is currently resident in GpuCache.
    /// </summary>
    public bool IsResident(Guid textureId)
    {
        lock (_cache) return _cache.ContainsKey(textureId);
    }

    /// <summary>
    /// BUG-PERF-13: Updates the set of protected avatar textures (self + nearest N avatars).
    /// Avatar textures not in protectedIds are allowed to be shrunk by the VRAM shrink pass.
    /// </summary>
    public void UpdateProtectedAvatarTextures(ISet<Guid> protectedIds)
    {
        foreach (var id in _avatarTextures.Keys)
        {
            if (_bakeTextures.ContainsKey(id))
            {
                // FEAT-PERF-25: a bake of a protected avatar is spared too (a remote bake used to be
                // shrinkable at conversation distance, and nothing ever sharpened it again). One that was
                // shrunk while its wearer was far is restored once the wearer is among the nearest.
                if (protectedIds.Contains(id))
                {
                    _protectedBakes.TryAdd(id, 0);
                    if (_uploadedForPixelArea.ContainsKey(id)) RestoreBake(id);
                }
                else
                {
                    _protectedBakes.TryRemove(id, out _);
                }
                continue;
            }
            if (protectedIds.Contains(id))
            {
                _noShrink.TryAdd(id, 0);
            }
            else
            {
                _noShrink.TryRemove(id, out _);
            }
        }
    }

    /// <summary>FEAT-PERF-25: sharpen a shrunk bake back to full size (its wearer became one of the
    /// nearest). Through the sharpen path, so it waits for room exactly like any near texture.</summary>
    private void RestoreBake(Guid textureId)
    {
        ImageTexture? tex;
        bool mips;
        lock (_cache)
        {
            if (!_cache.TryGetValue(textureId, out var e) || e.Res is not ImageTexture t) return;
            tex = t;
            mips = e.Mipmaps;
        }
        TryUpgradeCachedTexture(textureId, tex, _assets, mips, RestoreArea, priority: 1f);
    }

    // Larger than any texture's texel count: "the full asset".
    private const float RestoreArea = 1e12f;

    /// <summary>
    /// BUG-PERF-13: Unregisters avatar texture ids from avatar-specific tracking tables
    /// (_avatarTextures, _bakeTextures, _noShrink) so eviction can reclaim them.
    /// If <paramref name="evictIfUnreferenced"/> is true and the texture's RefCount is 0,
    /// it is evicted immediately to free VRAM.
    /// </summary>
    public void UnregisterAvatarTexture(Guid textureId, bool evictIfUnreferenced = false)
    {
        bool wasBake = _bakeTextures.TryRemove(textureId, out _);
        _avatarTextures.TryRemove(textureId, out _);
        _noShrink.TryRemove(textureId, out _);
        _protectedBakes.TryRemove(textureId, out _);

        if (evictIfUnreferenced || wasBake)
        {
            lock (_cache)
            {
                if (_cache.TryGetValue(textureId, out var entry) && entry.RefCount <= 0)
                {
                    if (entry.Node != null) _lruList.Remove(entry.Node);
                    _cache.Remove(textureId);
                    _uploadFromDegraded.TryRemove(textureId, out _);
                    _currentSize -= entry.Size;
                    if (GodotObject.IsInstanceValid(entry.Res))
                    {
                        entry.Res.Dispose();
                    }
                }
                EvictIfNeeded();
            }
        }
    }

    /// <summary>
    /// BUG-PERF-13: Re-offers an avatar texture for sharpening when its screen pixel area increases.
    /// </summary>
    public void TryUpgradeTexture(Guid textureId, SLNG.Assets.AssetService? assetService, bool generateMipmaps, float screenPixelArea, float priority = 0.5f)
    {
        if (assetService == null || screenPixelArea <= 0f) return;
        // FEAT-PERF-25: a bake is uploaded whole and is only a candidate once a shrink made it smaller
        // (TryUpgradeCachedTexture returns at once for one that was not); it is fetched from the bake host.
        ImageTexture? tex;
        lock (_cache)
        {
            tex = _cache.TryGetValue(textureId, out var e) ? e.Res as ImageTexture : null;
        }
        if (tex != null)
        {
            TryUpgradeCachedTexture(textureId, tex, assetService, generateMipmaps, screenPixelArea, priority);
        }
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

    /// <summary>BUG-RENDER-11: whether a texture's alpha channel is a 1-bit-ish CUTOUT (foliage,
    /// hair cards, lace, chain-link) rather than a genuine translucency GRADIENT (glass, smoke,
    /// a soft fade). Computed on the worker thread next to <see cref="_alphaModes"/>.
    ///
    /// <para>Godot's <c>Image.DetectAlpha()</c> answers <c>Blend</c> for a texture with even ONE
    /// texel of intermediate alpha, so every anti-aliased cutout edge reads as "needs blending"
    /// and <c>ObjectRenderer.ApplyAlphaCutout</c> put it in the sorted transparent pass -- where a
    /// world prim with no depth write z-fights itself and pops in/out as the camera turns (the
    /// reported grass/foliage "flipping"; measured 2026: 257 world-prim faces on that path in one
    /// SL region). The real viewer is far more permissive: a legacy alpha face is drawn as an
    /// alpha MASK (<c>PASS_ALPHA_MASK</c>, depth-writing, no sort) unless
    /// <c>LLFace::canRenderAsMask()</c> says otherwise, and that asks
    /// <c>LLImageGL::analyzeAlphaData()</c> -- "a mask unless &gt;1/48 of samples are mid-range,
    /// or every sample is clumped in one half of the range without reaching the extreme". This is
    /// a faithful port of that function (llimagegl.cpp:2191).</para>
    ///
    /// <para>Same lifetime rules as <see cref="_alphaModes"/>: one bool per texture id, never
    /// cleared on eviction.</para></summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, bool> _alphaMaskable = new();

    /// <summary>BUG-PERF-07: the three numbers <c>AvatarRenderer.ClassifyAlpha</c> decides an avatar or
    /// HUD face's transparency on, measured over mip 0 of the uploaded image ON THE WORKER, next to
    /// <see cref="_alphaModes"/> and for the same reason.
    ///
    /// <para>ClassifyAlpha used to get them by <c>ImageTexture.GetImage()</c> -- a full read-back from
    /// VRAM -- plus a copy and a scan of every pixel, per textured face, on the main thread. While
    /// the texture await was genuinely asynchronous that ran on a pool thread and nobody saw it; once
    /// FEAT-PERF-11 made textures ready at once, the material build completed inline inside the rig
    /// queue item, and on a teleport to Amrum it was <c>avatar.rig.materials</c> = 24.7 s of 28.5 s,
    /// about 8 ms per rig. Same lifetime rules as <see cref="_alphaModes"/>, plus: a texture whose
    /// pixels are replaced in place (sharpened, shrunk) gets its numbers replaced too.</para></summary>
    public readonly record struct AlphaStats(int MinAlpha, float FracMid, float FracClear);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, AlphaStats> _alphaStats = new();

    /// <summary>BUG-PERF-07: see <see cref="AlphaStats"/>. False when this id has not been uploaded
    /// through this cache yet.</summary>
    public static bool TryGetAlphaStats(Guid textureId, out AlphaStats stats)
        => _alphaStats.TryGetValue(textureId, out stats);

    /// <summary>The minimum alpha, and the fractions of texels with mid-range alpha (16 &lt; a &lt; 239)
    /// and with clear alpha (a &lt;= 16), over mip 0 of tightly packed RGBA8 -- exactly the scan
    /// ClassifyAlpha ran on the read-back image.</summary>
    internal static AlphaStats MeasureAlphaStats(byte[] rgba, int w, int h)
    {
        int mip0Bytes = Math.Min(rgba.Length, w * h * 4);
        int pixelCount = mip0Bytes / 4;

        int min = 255, midCount = 0, clearCount = 0;
        for (int i = 3; i < mip0Bytes; i += 4)
        {
            byte a = rgba[i];
            if (a < min) min = a;
            if (a > 16 && a < 239) midCount++;
            if (a <= 16) clearCount++;
        }
        return new AlphaStats(min,
            pixelCount > 0 ? (float)midCount / pixelCount : 0f,
            pixelCount > 0 ? (float)clearCount / pixelCount : 0f);
    }

    /// <summary>The alpha mode of an already-uploaded texture, without touching the GPU.
    /// False when this id has not been uploaded through this cache yet.</summary>
    public static bool TryGetAlphaMode(Guid textureId, out Image.AlphaMode mode)
        => _alphaModes.TryGetValue(textureId, out mode);

    /// <summary>BUG-RENDER-11: true if this texture's alpha qualifies as a 1-bit cutout mask
    /// (viewer parity, see <see cref="_alphaMaskable"/>). False when the id has not been uploaded
    /// through this cache yet -- callers fall back to the <see cref="Image.AlphaMode"/> guess.</summary>
    public static bool TryGetIsAlphaMaskable(Guid textureId, out bool maskable)
        => _alphaMaskable.TryGetValue(textureId, out maskable);

    /// <summary>Faithful port of <c>LLImageGL::analyzeAlphaData</c> (llimagegl.cpp:2191): a
    /// histogram of quantised alpha, plus a 2x2 box-sampled copy of it that "mid-skews" the data
    /// so a high-frequency alpha map (which aliases badly when masked) is less likely to be
    /// treated as a mask. <paramref name="rgba"/> is tightly packed RGBA8, alpha at byte 3,
    /// stride 4.</summary>
    private static bool AnalyzeAlphaMaskable(byte[] rgba, int w, int h)
    {
        const int AlphaOffset = 3, AlphaStride = 4;
        long length = (long)w * h;
        ulong alphatotal = 0;
        Span<uint> sample = stackalloc uint[16];

        // The 2x2 box-sample path needs even dimensions (the viewer asserts it); a resized LOD or
        // an odd source falls back to the plain single-pass histogram.
        if (w >= 2 && h >= 2 && (w & 1) == 0 && (h & 1) == 0)
        {
            int rowStride = w * AlphaStride;
            for (int y = 0; y < h; y += 2)
            {
                int rowStart = y * rowStride + AlphaOffset;
                for (int x = 0; x < w; x += 2)
                {
                    int c = rowStart + x * AlphaStride;
                    uint s1 = rgba[c];
                    uint s2 = rgba[c + rowStride];
                    uint s3 = rgba[c + AlphaStride];
                    uint s4 = rgba[c + AlphaStride + rowStride];
                    alphatotal += s1 + s2 + s3 + s4;
                    sample[(int)(s1 / 16)]++;
                    sample[(int)(s2 / 16)]++;
                    sample[(int)(s3 / 16)]++;
                    sample[(int)(s4 / 16)]++;
                    uint asum = s1 + s2 + s3 + s4;
                    alphatotal += asum;
                    sample[(int)(asum / (16 * 4))] += 4;
                }
            }
            length *= 2; // everything was sampled twice
        }
        else
        {
            for (long i = 0; i < length; i++)
            {
                uint s1 = rgba[i * AlphaStride + AlphaOffset];
                alphatotal += s1;
                sample[(int)(s1 / 16)]++;
            }
        }

        uint midrangetotal = 0;
        for (int i = 2; i < 13; i++) midrangetotal += sample[i];
        uint lowerhalftotal = 0;
        for (int i = 0; i < 8; i++) lowerhalftotal += sample[i];
        uint upperhalftotal = 0;
        for (int i = 8; i < 16; i++) upperhalftotal += sample[i];

        if (midrangetotal > length / 48 ||
            (lowerhalftotal == length && alphatotal != 0) ||
            (upperhalftotal == length && alphatotal != (ulong)(255 * length)))
        {
            return false; // not suitable for masking -- a real gradient / intentional fade
        }
        return true; // 1-bit-ish cutout
    }

    // One implementation, shared with AssetService's pre-decode reduce-level choice: two copies of
    // this arithmetic that disagreed would decode a texture small and then treat it as full
    // resolution (permanently blurry), or the reverse.
    private static int ComputeDiscardLevel(int width, int height, float screenPixelArea)
        => SLNG.Assets.TextureLod.DiscardLevelFor(width, height, screenPixelArea);

    /// <summary>The CPU half of a texture upload: decode-buffer to <see cref="Image"/>, alpha-edge
    /// fix, optional LOD downsample, mipmaps. Everything here is pure pixel work on a
    /// <see cref="Image"/>'s own byte array with no RenderingServer involvement, so it belongs on a
    /// worker thread -- see the call site for what leaving it on the main thread cost.</summary>
    /// <param name="ReservedBytes">BUG-PERF-09: what this upload added to the reserved (admitted, not
    /// yet resident) total. Whoever takes the image hands it back with
    /// <see cref="ReleaseReservation"/> once the image is resident or discarded.</param>
    /// <param name="ExtraDiscard">...and how many levels smaller than the screen asked for admission
    /// made the image (0: none).</param>
    /// <param name="NothingSharper">...and, for a re-upload that returned no image, that this is
    /// because the level the area asks for is no larger than the texture already is (the area is below
    /// the 5-level floor, or the asset is small) -- as opposed to admission finding no room.</param>
    private readonly record struct PreparedImage(Image? Image, float UploadedForPixelArea, long ReservedBytes, int ExtraDiscard = 0, bool NothingSharper = false,
        int SourceWidth = 0, int SourceHeight = 0);

    /// <summary>Bounds the concurrent CPU image work the same way AssetService bounds J2K decode.
    /// An unbounded <c>Task.Run</c> per texture is what made the decode path's measured "91 ms"
    /// mostly thread-pool queueing rather than real work (BUG-NET-11, v0.20.48); do not repeat it
    /// one stage later. Two cores left for the Godot main thread + GC.</summary>
    private static readonly SemaphoreSlim _imagePrepGate =
        new(Math.Max(2, System.Environment.ProcessorCount - 2));

    private static volatile bool _shuttingDown;
    private static int _imagePrepsRunning;

    /// <summary>True once the client has started to quit. Worker threads that call into Godot
    /// (<c>Image.CreateFromData</c> and friends) must not start new work after this.</summary>
    public static bool ShuttingDown => _shuttingDown;

    /// <summary>Stop the worker-side image preparation before the engine tears down, and wait
    /// for whatever is already running to finish.
    ///
    /// <para>The image build calls into Godot from a thread-pool thread. If the engine is already
    /// finalizing its C# interop when such a call lands, it dies inside
    /// <c>UnmanagedGetManaged</c> with an <c>AccessViolationException</c> -- a fatal error on
    /// every exit that happens while textures are still arriving (seen on quit during a texture
    /// storm, ~70 images in flight). Setting the flag first and then waiting for the counter is
    /// the safe order: a worker increments, THEN checks the flag, so it either sees the flag and
    /// skips, or is counted and waited for.</para>
    ///
    /// <para>Bounded: the work is tens of milliseconds per texture, so a stuck worker must not be
    /// able to hold the window open.</para></summary>
    public static void BeginShutdown(int maxWaitMs = 2000)
    {
        _shuttingDown = true;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref _imagePrepsRunning) > 0 && clock.ElapsedMilliseconds < maxWaitMs)
            Thread.Sleep(5);
        // BUG-PERF-06: the mesh workers call into Godot as well.
        EngineWorkerGate.Close(maxWaitMs);
    }

    /// <summary>Runs CPU image work on a pool thread inside the shared image-prep gate. The hop is
    /// unconditional, even though callers are normally on a worker already: GetTextureAsync returns a
    /// COMPLETED task on an AssetService memory-cache hit, so the await in front of this resumes
    /// inline on whatever thread called GetOrUploadTextureAsync -- and ObjectRenderer calls it
    /// straight from _Process's cull sweep. Without the hop that case would run the whole image
    /// build on the main thread with no budget at all, which is worse than the queued version it
    /// replaces.</summary>
    private static async Task<T> RunOnImageWorker<T>(Func<T> work, T whenShuttingDown)
    {
        await _imagePrepGate.WaitAsync().ConfigureAwait(false);
        Interlocked.Increment(ref _imagePrepsRunning);
        try
        {
            // Counted first, checked second -- see BeginShutdown. The engine is going away;
            // no texture is worth an AccessViolation on the way out.
            return await Task.Run(() => _shuttingDown ? whenShuttingDown : work()).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _imagePrepsRunning);
            _imagePrepGate.Release();
        }
    }

    /// <param name="replacedBytes">BUG-PERF-09: for a re-upload of a texture that is already resident
    /// (a sharpen), the bytes it holds now. Admission then judges the NET growth, and an upload that
    /// would not be larger than what is there returns no image at all.</param>
    private Task<PreparedImage> PrepareImageAsync(
        Guid textureId, SLNG.Assets.TextureData textureData, bool generateMipmaps, float screenPixelArea,
        long replacedBytes = 0)
        => RunOnImageWorker(
            () => PrepareImage(textureId, textureData, generateMipmaps, screenPixelArea, replacedBytes),
            new PreparedImage(null, 0f, 0));

    private PreparedImage PrepareImage(
        Guid textureId, SLNG.Assets.TextureData textureData, bool generateMipmaps, float screenPixelArea,
        long replacedBytes)
    {
        Image? image = null;
        float uploadedFor = 0f;
        bool resized = false;
        long reserved = 0;
        int extra = 0;
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

            if (image != null)
            {
                int w = image.GetWidth(), h = image.GetHeight();
                int discard = 0;
                if (screenPixelArea > 0f)
                {
                    // Computed on the ALREADY-REDUCED image, so it yields exactly the discard
                    // levels the decoder did not cover (the formula drops by 2 per reduce
                    // factor, which is the same 4x-per-level relationship).
                    discard = ComputeDiscardLevel(w, h, screenPixelArea);
                    // Behind --diag. It is one line per texture, which on a real region means
                    // a couple of thousand -- fine when it went to a terminal nobody was
                    // reading, but it now reaches godot.log (see ConsoleToGodotLog) and there
                    // it buries the handful of lines that say why something is broken. This is
                    // per-object asset logging, exactly what Diagnostics exists to gate.
                    if (Diagnostics.Enabled && _uploadSizeLogged.TryAdd(textureId, 0))
                        Console.Error.WriteLine($"[GpuUpload] {textureId} asset={textureData.SourceWidth}x{textureData.SourceHeight} " +
                            $"decoded={w}x{h} " +
                            $"screenPixelArea={screenPixelArea:F0} -> discard={discard} " +
                            $"uploaded={(discard > 0 ? $"{SLNG.Assets.TextureLod.DimensionAfterDiscard(w, discard)}x{SLNG.Assets.TextureLod.DimensionAfterDiscard(h, discard)}" : "full")}");
                }

                // BUG-PERF-09: admission. The level the screen asks for is only the first half of
                // the answer; the second is whether the cache can hold the result. Judged HERE, per
                // texture, against resident + already-admitted bytes -- not against the global bias,
                // which a burst outruns by thousands of textures. Avatar / bake / terrain / UI
                // textures (no screen area, or exempt) are counted but never reduced.
                if (replacedBytes > 0 && SLNG.Assets.TextureAdmission.TextureBytes(
                        SLNG.Assets.TextureLod.DimensionAfterDiscard(w, discard),
                        SLNG.Assets.TextureLod.DimensionAfterDiscard(h, discard), generateMipmaps) <= replacedBytes)
                {
                    // A sharpen whose target is no larger than what is resident, whatever the budget:
                    // the area is below the level floor, or the asset itself is that small.
                    image.Dispose();
                    return new PreparedImage(null, 0f, 0, 0, NothingSharper: true);
                }

                bool admit = screenPixelArea > 0f && !_noShrink.ContainsKey(textureId) && !_protectedBakes.ContainsKey(textureId);
                int maxExtra = admit ? Math.Min(MaxLodBias, SLNG.Assets.TextureLod.MaxDiscardLevel - discard) : 0;
                // FEAT-PERF-25: a near texture is not cut for the far ones already resident (NearCeiling).
                bool near = admit && screenPixelArea >= SLNG.Assets.TextureLod.BiasExemptAreaPx;
                extra = AdmitAndReserve(
                    SLNG.Assets.TextureLod.DimensionAfterDiscard(w, discard),
                    SLNG.Assets.TextureLod.DimensionAfterDiscard(h, discard),
                    generateMipmaps, Math.Max(0, maxExtra), replacedBytes, near, out long finalBytes, out reserved);

                if (replacedBytes > 0 && finalBytes <= replacedBytes)
                {
                    // A sharpen that admission cut back to what is already there. Nothing to upload.
                    image.Dispose();
                    return new PreparedImage(null, 0f, 0);
                }

                int total = discard + extra;
                if (total > 0)
                {
                    int targetW = SLNG.Assets.TextureLod.DimensionAfterDiscard(w, total);
                    int targetH = SLNG.Assets.TextureLod.DimensionAfterDiscard(h, total);
                    if (targetW < w || targetH < h)
                    {
                        image.Resize(targetW, targetH, Image.Interpolation.Lanczos);
                        resized = true;
                    }
                    // A texture admission made smaller than its area asked for is recorded as built
                    // for the area it actually serves, so the re-sharpen rule fires once there is room.
                    uploadedFor = extra > 0
                        ? SLNG.Assets.TextureAdmission.BuiltForArea(screenPixelArea, extra)
                        : screenPixelArea;
                }

                // Before mipmaps, so the scan covers the base level only -- the mips are
                // derived from it and add nothing but work. See _alphaModes for why this is
                // computed here and not where it is used.
                _alphaModes[textureId] = image.DetectAlpha();
                // BUG-RENDER-11: viewer-faithful mask/gradient verdict, same worker, same
                // already-decoded pixels. Only meaningful when there IS an alpha channel.
                _alphaMaskable[textureId] = _alphaModes[textureId] != Image.AlphaMode.None
                    && AnalyzeAlphaMaskable(image.GetData(), image.GetWidth(), image.GetHeight());
                // BUG-PERF-07: the decoded buffer IS mip 0 of the upload unless it was
                // resized -- FixAlphaEdges rewrites the colour of near-clear texels and
                // never their alpha (core/io/image.cpp, 4.7-stable) -- so no copy is
                // needed for the common case.
                _alphaStats[textureId] = resized
                    ? MeasureAlphaStats(image.GetData(), image.GetWidth(), image.GetHeight())
                    : MeasureAlphaStats(textureData.Rgba, textureData.Width, textureData.Height);
            }

            if (generateMipmaps && image != null) image.GenerateMipmaps();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[GpuUpload] Failed to process texture {textureId}: {ex.Message}");
            image?.Dispose();
            image = null;
            uploadedFor = 0f;
            ReleaseReservation(reserved);
            reserved = 0;
            extra = 0;
        }
        return new PreparedImage(image, uploadedFor, reserved, extra,
            SourceWidth: textureData.SourceWidth > 0 ? textureData.SourceWidth : textureData.Width,
            SourceHeight: textureData.SourceHeight > 0 ? textureData.SourceHeight : textureData.Height);
    }

    /// <summary>BUG-PERF-09: picks the admission level for one texture and reserves its bytes, in one
    /// step under one lock, so the workers that prepare textures in parallel cannot each see the same
    /// headroom and all spend it. <paramref name="width"/> x <paramref name="height"/> is the size at
    /// the level the screen asks for; the answer is how many MORE levels it takes to fit. The bytes of
    /// the result are reserved whatever the answer (an exempt texture is still part of the projected
    /// size), minus <paramref name="replacedBytes"/> for an in-place re-upload.</summary>
    private int AdmitAndReserve(
        int width, int height, bool mipmaps, int maxExtra, long replacedBytes, bool near,
        out long finalBytes, out long reserved)
    {
        long wanted;
        int extra;
        lock (_admitLock)
        {
            long budget = Interlocked.Read(ref _maxSize);
            long projected = Interlocked.Read(ref _currentSize) - replacedBytes + _reservedBytes;
            // FEAT-PERF-25: judged against the near ceiling, not the budget, for a texture the user is
            // close to -- see NearCeiling. The far one in the corner still takes the cut.
            long ceiling = near ? NearCeiling(budget) : budget;
            extra = maxExtra > 0
                ? SLNG.Assets.TextureAdmission.ExtraDiscardFor(projected, ceiling, width, height, mipmaps, maxExtra)
                : 0;
            finalBytes = SLNG.Assets.TextureAdmission.TextureBytes(
                SLNG.Assets.TextureLod.DimensionAfterDiscard(width, extra),
                SLNG.Assets.TextureLod.DimensionAfterDiscard(height, extra), mipmaps);
            reserved = Math.Max(0, finalBytes - replacedBytes);
            _reservedBytes += reserved;

            long requestedBytes = SLNG.Assets.TextureAdmission.TextureBytes(width, height, mipmaps);
            wanted = near && extra > 0 ? requestedBytes - finalBytes : 0;
            if (extra > 0)
            {
                _admitCut++;
                _admitSavedBytes += requestedBytes - finalBytes;
            }
        }
        // A near texture that still had to be cut asks for the room its sharpen will need, so the shrink
        // pass makes it before the next offer instead of the texture waiting for low water.
        if (wanted > 0) NoteRoomWanted(wanted);
        return extra;
    }

    private long ReservedBytesSnapshot() => Interlocked.Read(ref _reservedBytes);

    private void NoteRoomWanted(long bytes)
    {
        long seen;
        do { seen = Interlocked.Read(ref _roomWanted); }
        while (bytes > seen && Interlocked.CompareExchange(ref _roomWanted, bytes, seen) != seen);
        do { seen = Interlocked.Read(ref _statRoomWantedMax); }
        while (bytes > seen && Interlocked.CompareExchange(ref _statRoomWantedMax, bytes, seen) != seen);
    }

    /// <summary>FEAT-PERF-25: how far over the budget a NEAR texture may be admitted or sharpened: by what
    /// the last scan found it could give back without a visible loss, at most an eighth of the budget
    /// (at least 32 MB when the scan found that much). Bounded, so a burst of near textures cannot run the card into paging
    /// before the shrink pass has paid the overshoot back.</summary>
    private long NearCeiling(long budget)
        => budget + Math.Min(Interlocked.Read(ref _reclaimableBytes), Math.Max(NearOvershootMinBytes, budget / 8));

    private void ReleaseReservation(long bytes)
    {
        if (bytes <= 0) return;
        lock (_admitLock) _reservedBytes = Math.Max(0, _reservedBytes - bytes);
    }

    /// <summary>The main-thread half of an upload: the GPU upload (<c>ImageTexture.CreateFromImage</c>)
    /// and the cache entry, plus giving the admission reservation back. Returns the cached texture,
    /// or null if there was nothing to upload.</summary>
    private ImageTexture? CommitPreparedTexture(
        Guid textureId, PreparedImage prepared, float screenPixelArea, bool generateMipmaps, int initialRefCount)
    {
        var image = prepared.Image;

        // Re-check: a differently-triggered Put for this id (shouldn't normally happen
        // given the single-flight dict above, but costs nothing to guard) may have
        // already landed between the await above and this deferred callback running.
        var raced = Get(textureId) as ImageTexture;
        if (raced != null)
        {
            image?.Dispose();
            ReleaseReservation(prepared.ReservedBytes);
            return raced;
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
                    // was uploaded downsampled when nothing was uploaded at all. A full-size
                    // upload clears whatever an evicted earlier upload of the id left behind.
                    if (prepared.UploadedForPixelArea > 0f)
                        _uploadedForPixelArea[textureId] = prepared.UploadedForPixelArea;
                    else
                        _uploadedForPixelArea.TryRemove(textureId, out _);
                    if (prepared.ExtraDiscard > 0) _admissionRequestedArea[textureId] = screenPixelArea;
                    else _admissionRequestedArea.TryRemove(textureId, out _);

                    // BUG-PERF-09: with its mip chain -- the base level alone is 25% under
                    // what the card holds, and the budget is compared with the card.
                    int texW = tex.GetWidth(), texH = tex.GetHeight();
                    long size = SLNG.Assets.TextureAdmission.TextureBytes(texW, texH, generateMipmaps);
                    Put(textureId, tex, size, initialRefCount,
                        new TextureShape(texW, texH, prepared.SourceWidth, prepared.SourceHeight, generateMipmaps, screenPixelArea));
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
            // After Put, so the bytes are never in neither total: for the length of this call
            // they are in both, which can only make admission more careful.
            ReleaseReservation(prepared.ReservedBytes);
        }
        return tex;
    }

    /// <summary>BUG-PERF-09 self test seam: the whole upload minus the network and the queue, on the
    /// calling (main) thread. Returns what the cache ended up holding.</summary>
    internal (ImageTexture? Texture, float UploadedFor, int Extra) SelfTestUpload(
        Guid textureId, SLNG.Assets.TextureData data, float screenPixelArea, bool generateMipmaps = true, int initialRefCount = 0)
    {
        var prepared = PrepareImage(textureId, data, generateMipmaps, screenPixelArea, 0);
        var tex = CommitPreparedTexture(textureId, prepared, screenPixelArea, generateMipmaps, initialRefCount);
        return (tex, prepared.UploadedForPixelArea, prepared.ExtraDiscard);
    }

    /// <summary>Self test seam: prepare a re-upload of a resident texture, as a sharpen would. No image
    /// means admission left it where it is (<c>NothingSharper</c> false) or the area asks for nothing
    /// larger than what is there (true).</summary>
    internal (bool HasImage, bool NothingSharper, int Width, int Height) SelfTestPrepareReUpload(
        Guid textureId, SLNG.Assets.TextureData data, float screenPixelArea, long replacedBytes)
    {
        var prepared = PrepareImage(textureId, data, true, screenPixelArea, replacedBytes);
        if (prepared.Image == null) return (false, prepared.NothingSharper, 0, 0);
        var result = (true, false, prepared.Image.GetWidth(), prepared.Image.GetHeight());
        prepared.Image.Dispose();
        ReleaseReservation(prepared.ReservedBytes);
        return result;
    }

    internal long SelfTestReservedBytes => ReservedBytesSnapshot();

    internal void SelfTestMarkNoShrink(Guid textureId) => _noShrink.TryAdd(textureId, 0);

    internal bool SelfTestIsNoShrink(Guid textureId) => _noShrink.ContainsKey(textureId);
    internal bool SelfTestIsAvatarTexture(Guid textureId) => _avatarTextures.ContainsKey(textureId);
    internal bool SelfTestIsBakeTexture(Guid textureId) => _bakeTextures.ContainsKey(textureId);
    internal int SelfTestGetRefCount(Guid textureId)
    {
        lock (_cache) return _cache.TryGetValue(textureId, out var e) ? e.RefCount : 0;
    }
    internal void SelfTestMarkAvatarTexture(Guid textureId, bool isBake, bool isSelf)
    {
        _avatarTextures.TryAdd(textureId, 0);
        if (isBake) _bakeTextures.TryAdd(textureId, 0);
        if (isSelf) _noShrink.TryAdd(textureId, 0);
    }

    /// <summary>Self test seam: <see cref="PrepareShrinkImage"/>, whose pixels must equal the
    /// read-back shrink's.</summary>
    internal static (Image? Image, AlphaStats Stats) SelfTestPrepareShrink(SLNG.Assets.TextureData decoded, int nw, int nh, bool mips)
    {
        var prepared = PrepareShrinkImage(decoded, nw, nh, mips);
        return (prepared.Image, prepared.Stats);
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
                tcs.SetResult(CommitPreparedTexture(textureId, prepared, screenPixelArea, generateMipmaps, initialRefCount)),
                label: "texture.upload");
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

    /// <summary>FEAT-PERF-04: per-frame VRAM back-pressure. Eviction alone cannot bind the budget on
    /// a dense region -- the whole visible scene is legitimately referenced, so nothing is
    /// reclaimable. Two levers that act on what is resident / what goes in next:
    /// <list type="number">
    /// <item>a global LOD bias (<see cref="SLNG.Assets.TextureLod.GlobalLodBias"/>) added to every
    ///   NEW upload of something small on screen, raised above budget and lowered (with hysteresis)
    ///   below <see cref="LowWater"/>;</item>
    /// <item>a shrink pass: re-halve resident textures in place (<c>ImageTexture.SetImage</c>),
    ///   <see cref="ShrinkPerTick"/> per frame, until back under <see cref="LowWater"/>.</item>
    /// </list>
    /// <para>BUG-PERF-09: neither lever can keep up with a burst, so the first line of defence is
    /// admission at upload time (<see cref="AdmitAndReserve"/>), which sizes each arriving texture
    /// against resident + in-flight bytes. This pass is what remains: it starts from the PROJECTED
    /// size, counts what shrinks already in flight will give back, and also makes room for a near
    /// texture that is waiting to sharpen (<see cref="_roomWanted"/>).</para>
    /// <para>FEAT-PERF-25: which textures give back is ranked (<see cref="RebuildShrinkCandidates"/>),
    /// a shrink that cannot be done is remembered instead of retried every frame, and one that never
    /// finishes is given up -- in the 3.5 GB run the pass did not shrink a single texture.</para>
    /// Call once per frame from the main thread.</summary>
    public void Tick()
    {
        long now = _biasClock.ElapsedMilliseconds;
        long frameMs = _lastTickMs == 0 ? 0 : Math.Min(now - _lastTickMs, 1000);
        _lastTickMs = now;
        var toShrink = _toShrink;
        toShrink.Clear();
        long projected;

        lock (_cache)
        {
            EvictIfNeeded();
            bool over = _currentSize > _maxSize;
            bool under = _currentSize < (long)(_maxSize * LowWater);

            if (now >= _nextBiasChangeMs)
            {
                if (over && SLNG.Assets.TextureLod.GlobalLodBias < MaxLodBias)
                {
                    SLNG.Assets.TextureLod.GlobalLodBias++;
                    _nextBiasChangeMs = now + BiasChangeCooldownMs;
                    Console.Error.WriteLine($"[GpuCache] over budget ({_currentSize >> 20}/{_maxSize >> 20} MB) -- LOD bias -> {SLNG.Assets.TextureLod.GlobalLodBias}");
                }
                else if (under && SLNG.Assets.TextureLod.GlobalLodBias > 0)
                {
                    SLNG.Assets.TextureLod.GlobalLodBias--;
                    _nextBiasChangeMs = now + BiasChangeCooldownMs;
                    Console.Error.WriteLine($"[GpuCache] under {LowWater:P0} budget -- LOD bias -> {SLNG.Assets.TextureLod.GlobalLodBias}");
                }
            }

            // BUG-PERF-09: shrinks are in flight on workers now, so "over" has to mean over AFTER
            // what they are about to give back, or every frame picks the same excess again.
            //
            // FEAT-PERF-25: and one that never comes back is given up. A shrink parked for ever (its
            // decode never got a slot) used to hold its place under MaxShrinksInFlight for the session.
            long pendingSavings = 0;
            foreach (var kv in _shrinkPending)
            {
                if (now - kv.Value.StartedMs > ShrinkTimeoutMs)
                {
                    if (_shrinkPending.TryRemove(kv.Key, out _))
                    {
                        Interlocked.Increment(ref _statTimedOut);
                        if (_cache.TryGetValue(kv.Key, out var stale)) stale.ShrinkBlockedUntilMs = now + ShrinkBlockedMs;
                    }
                    continue;
                }
                pendingSavings += kv.Value.Saving;
            }

            // The projected size also counts what is admitted and not uploaded yet: a burst of
            // admissions that hit the cap overshoots in reserved bytes first, and that is the signal
            // to start giving back, not the later moment those uploads land.
            long wanted = Interlocked.Exchange(ref _roomWanted, 0);   // a near texture waiting for room
            projected = _currentSize + ReservedBytesSnapshot() - pendingSavings;
            long effective = projected + wanted;
            // Over the budget: fall to the low-water mark. Only making room for a sharpen: just that.
            long target = projected > _maxSize ? (long)(_maxSize * LowWater) : _maxSize;
            if (projected > _maxSize) _statOverMs += frameMs;

            bool needRoom = effective > _maxSize;
            bool candidatesUsedUp = _candidateCursor >= _shrinkCandidates.Count;
            // Ranked again when the list is used up, or has grown stale; and, while the cache is close to
            // its budget, once a second anyway -- admission's near ceiling lives on the reclaimable figure.
            bool rescan = needRoom
                ? (candidatesUsedUp && now - _lastScanMs >= ScanIntervalMs) || now - _lastScanMs >= ScanRefreshMs
                : projected > (long)(_maxSize * LowWater) && now - _lastScanMs >= 1000;
            if (rescan) RebuildShrinkCandidates(now, allowVisible: projected > (long)(_maxSize * VisibleShrinkOver));

            if (needRoom)
            {
                // _shrinkPending already holds the ones picked in this loop (TryAdd below).
                while (toShrink.Count < ShrinkPerTick
                       && _shrinkPending.Count < MaxShrinksInFlight
                       && effective > target
                       && _candidateCursor < _shrinkCandidates.Count)
                {
                    var e = _shrinkCandidates[_candidateCursor++];
                    // Ranked up to ScanRefreshMs ago: still the same live entry, still in use, still allowed?
                    if (!_cache.TryGetValue(e.Id, out var live) || !ReferenceEquals(live, e)) continue;
                    if (e.RefCount <= 0 || e.ShrinkBlockedUntilMs > now || e.Res is not ImageTexture tex) continue;
                    if (_noShrink.ContainsKey(e.Id) || _protectedBakes.ContainsKey(e.Id)) continue;
                    long saving = e.Size - e.Size / 4;                        // one halving of each side
                    if (!_shrinkPending.TryAdd(e.Id, new PendingShrink(saving, now, IsVisibleTier(e, now)))) continue;
                    toShrink.Add((e, tex));
                    effective -= saving;
                }
            }
        }

        foreach (var (entry, tex) in toShrink) StartShrink(entry, tex, now);
        toShrink.Clear();

        if (now >= _nextShrinkStatsMs)
        {
            _nextShrinkStatsMs = now + ShrinkStatsIntervalMs;
            EmitShrinkStats(projected);
        }
    }

    /// <summary>FEAT-PERF-25: ranks what the shrink pass may give back. Under <c>_cache</c>'s lock.
    ///
    /// <para>The pass used to walk the LRU list from its head and take the first two textures that were
    /// big enough, in use and not exempt. Every offer moves a texture to the tail, and ObjectRenderer
    /// offers every shown object every 2 s whether it is 3 m or 150 m away -- so the order said nothing
    /// about distance, and what came first was as likely the floor under the avatar as a roof across the
    /// sim. Now every entry is looked at (every 0.5 s while over budget, not per frame) and the best
    /// <see cref="MaxCandidates"/> are kept:</para>
    /// <list type="bullet">
    /// <item><b>free</b>: not asked for within <see cref="AreaWindowMs"/> (out of draw distance, a parked
    ///   avatar's outfit) or carrying at least 4x the texels its largest recent screen area can show --
    ///   halving those costs nothing visible, and the area it serves stays below the sharpen threshold,
    ///   so it does not come straight back;</item>
    /// <item><b>visible</b>: right-sized for what the screen shows, near ones last. Only when nothing is
    ///   free and the cache is <see cref="VisibleShrinkOver"/> over -- the budget fell under what is in
    ///   view (another program took video memory) and paging would cost more than one level. Such a
    ///   texture does not ask for room to sharpen again for <see cref="SharpenShrinkGuardMs"/>, so two of
    ///   them cannot trade places every frame;</item>
    /// <item>never: textures already smaller than their screen area asks, protected avatars (self + the
    ///   nearest full ones, their bakes too), textures with no LOD information (terrain, UI -- nobody can
    ///   say how large they are on screen), anything sharpened in the last 30 s or blocked after a failed
    ///   shrink.</item>
    /// </list>
    /// <para>Within a tier: bytes x texels-per-screen-pixel x time since last asked for -- large, far and
    /// stale first.</para></summary>
    private void RebuildShrinkCandidates(long now, bool allowVisible)
    {
        _lastScanMs = now;
        _shrinkCandidates.Clear();
        _candidateCursor = 0;
        _scanFree.Clear();
        _scanVisible.Clear();
        Array.Clear(_scanSkipBytes);
        Array.Clear(_scanSkipCount);
        int seen = 0, freeCount = 0, visibleCount = 0;
        long reclaimable = 0, visibleBytes = 0;

        foreach (var e in _cache.Values)
        {
            seen++;
            Skip? skip = null;
            if (e.Width <= 0 || e.Res is not ImageTexture) skip = Skip.Mesh;
            else if (e.RefCount <= 0) skip = Skip.Unreferenced;
            else if (e.Size <= ShrinkFloorBytes || (e.Width <= 8 && e.Height <= 8)) skip = Skip.Small;
            else if (_shrinkPending.ContainsKey(e.Id)) skip = Skip.Pending;
            else if (e.ShrinkBlockedUntilMs > now) skip = Skip.Blocked;
            else if (e.LastSharpenMs > 0 && now - e.LastSharpenMs < SharpenShrinkGuardMs) skip = Skip.Sharpened;
            else if (_noShrink.ContainsKey(e.Id) || _protectedBakes.ContainsKey(e.Id)) skip = Skip.Protected;
            else if (!e.EverHadArea && !_avatarTextures.ContainsKey(e.Id)) skip = Skip.NoLod;
            if (skip is { } s0)
            {
                _scanSkipBytes[(int)s0] += e.Size;
                _scanSkipCount[(int)s0]++;
                continue;
            }

            double excess = Excess(e, now);
            if (excess < 1.0)
            {
                // Already smaller than its screen area asks for: halving it is a loss the user sees.
                _scanSkipBytes[(int)Skip.Undersized] += e.Size;
                _scanSkipCount[(int)Skip.Undersized]++;
                continue;
            }

            double ageSeconds = Math.Clamp((now - e.LastSeenMs) / 1000.0, 0.0, 120.0);
            double score = e.Size * Math.Min(excess, 256.0) * (1.0 + ageSeconds / 10.0);
            if (excess >= 4.0)
            {
                freeCount++;
                reclaimable += e.Size - e.Size / 4;
                OfferCandidate(_scanFree, e, score);
            }
            else
            {
                // Right-sized for what the screen shows. Near ones last of all.
                if (e.RecentArea >= SLNG.Assets.TextureLod.BiasExemptAreaPx) score /= 16.0;
                visibleCount++;
                visibleBytes += e.Size;
                OfferCandidate(_scanVisible, e, score);
            }
        }

        var source = _scanFree.Count > 0 ? _scanFree : allowVisible ? _scanVisible : null;
        if (source != null)
        {
            // The heap pops the lowest score first; the list wants the best first.
            while (source.TryDequeue(out var e, out _)) _shrinkCandidates.Add(e);
            _shrinkCandidates.Reverse();
        }
        _scanFree.Clear();
        _scanVisible.Clear();

        _scanSeen = seen;
        _scanFreeCount = freeCount;
        _scanVisibleCount = visibleCount;
        _scanVisibleBytes = visibleBytes;
        _statScans++;
        Interlocked.Exchange(ref _reclaimableBytes, reclaimable);
    }

    /// <summary>Texels per pixel of the largest screen area the texture was asked for within
    /// <see cref="AreaWindowMs"/>; one not asked for within it (off screen, out of draw distance, a parked
    /// outfit) counts as 64 -- unseen, so as free to shrink as anything.</summary>
    private static double Excess(CacheEntry e, long now)
    {
        bool fresh = e.RecentArea > 0f && now - e.RecentAreaMs <= AreaWindowMs;
        return fresh ? (double)e.Width * e.Height / Math.Max(e.RecentArea, 16f) : 64.0;
    }

    private static bool IsVisibleTier(CacheEntry e, long now) => Excess(e, now) < 4.0;

    /// <summary>Keeps the <see cref="MaxCandidates"/> highest scores in a min-heap.</summary>
    private static void OfferCandidate(PriorityQueue<CacheEntry, double> heap, CacheEntry e, double score)
    {
        if (heap.Count < MaxCandidates) heap.Enqueue(e, score);
        else if (heap.TryPeek(out _, out double lowest) && score > lowest) heap.EnqueueDequeue(e, score);
    }

    /// <summary>BUG-PERF-09: starts re-halving one resident texture. The pixels come from what is
    /// already on this machine and the resize, the alpha numbers and the mip chain are built on a
    /// worker; the main thread only does the <c>SetImage</c> (<see cref="ApplyShrink"/>). Reading the
    /// texture back out of VRAM is banned on this path (BUG-PERF-15).
    ///
    /// <para>FEAT-PERF-25: the pixels are asked for at the size the shrink needs, not the full asset --
    /// a reduced decode of the cached .j2c when the decoded caches do not have it (<see
    /// cref="SLNG.Assets.AssetService.GetShrinkSourceAsync"/>, its own worker slots, never behind the load
    /// path), and for a bake the bake host as a last resort. A texture that still has no pixels is left
    /// alone for <see cref="ShrinkBlockedMs"/> so the next scan picks something else.</para>
    ///
    /// <para>The Refine lane's cap of 2 items per frame is NOT lifted for these. The cap exists
    /// because <c>SetImage</c> with a new size frees and recreates the texture's GPU storage, and
    /// doing that to too many in-use <see cref="ImageTexture"/>s in one frame crashed the Vulkan
    /// backend (Signal 11, see <see cref="MainThreadWorkQueue.Pump"/>). A shrink is exactly that
    /// operation, and nothing here has shown the crash to be gone.</para></summary>
    private void StartShrink(CacheEntry entry, ImageTexture tex, long now)
    {
        var assets = _assets;
        int w = entry.Width, h = entry.Height;
        int nw = Math.Max(8, w >> 1), nh = Math.Max(8, h >> 1);
        if (assets == null || !GodotObject.IsInstanceValid(tex) || (nw >= w && nh >= h))
        {
            lock (_cache) entry.ShrinkBlockedUntilMs = now + ShrinkBlockedMs;
            _shrinkPending.TryRemove(entry.Id, out _);
            return;
        }
        bool mips = entry.Mipmaps;
        Guid id = entry.Id;
        int sourceW = entry.SourceWidth, sourceH = entry.SourceHeight;
        float priority = entry.Size / (1024f * 1024f);   // the larger saving first
        bool isBake = _bakeSource.TryGetValue(id, out var bake);
        Interlocked.Increment(ref _statStarted);

        _ = Task.Run(async () =>
        {
            try
            {
                var decoded = await assets.GetShrinkSourceAsync(id, nw, nh, sourceW, sourceH, priority).ConfigureAwait(false);
                if (decoded == null && isBake)
                    decoded = await assets.GetBakeTextureAsync(id, bake.Channel, 0f, bake.Agent).ConfigureAwait(false);
                var prepared = decoded != null && decoded.Width >= nw && decoded.Height >= nh
                    ? await RunOnImageWorker(() => PrepareShrinkImage(decoded, nw, nh, mips), default(PreparedShrink)).ConfigureAwait(false)
                    : default;
                if (prepared.Image == null)
                {
                    Interlocked.Increment(ref _statNoSource);
                    lock (_cache) entry.ShrinkBlockedUntilMs = _biasClock.ElapsedMilliseconds + ShrinkBlockedMs;
                    _shrinkPending.TryRemove(id, out _);
                    Logger.Info($"[GpuCache] shrink {id.ToString()[..8]} skipped: no local pixels ({w}x{h} kept, retried in {ShrinkBlockedMs / 1000} s)");
                    return;
                }
                MainThreadWorkQueue.Enqueue(MainThreadWorkQueue.Lane.Refine,
                    () => ApplyShrink(entry, tex, prepared, w, h, nw, nh, mips), label: "texture.shrink");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _statFailed);
                GD.PrintErr($"[GpuCache] shrink {id} failed: {ex.Message}");
                lock (_cache) entry.ShrinkBlockedUntilMs = _biasClock.ElapsedMilliseconds + ShrinkBlockedMs;
                _shrinkPending.TryRemove(id, out _);
            }
        });
    }

    private readonly record struct PreparedShrink(Image? Image, AlphaStats Stats);

    /// <summary>The worker half of a shrink: the same pixel work an upload at the smaller size would
    /// have done (alpha-edge fix, Lanczos resize, mips), from the decoded copy.</summary>
    private static PreparedShrink PrepareShrinkImage(SLNG.Assets.TextureData decoded, int nw, int nh, bool mips)
    {
        Image? image = null;
        try
        {
            image = Image.CreateFromData(decoded.Width, decoded.Height, false, Image.Format.Rgba8, decoded.Rgba);
            if (image == null) return default;
            image.FixAlphaEdges();
            image.Resize(nw, nh, Image.Interpolation.Lanczos);
            var stats = MeasureAlphaStats(image.GetData(), nw, nh);
            if (mips) image.GenerateMipmaps();
            return new PreparedShrink(image, stats);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[GpuCache] shrink prepare failed: {ex.Message}");
            image?.Dispose();
            return default;
        }
    }

    /// <summary>The main-thread half of a shrink prepared on a worker: push the finished pixels into
    /// the SAME <see cref="ImageTexture"/> so every material keeps working with no re-wiring, and book
    /// the new size. Refine lane, 2 per frame -- see <see cref="StartShrink"/>.</summary>
    private void ApplyShrink(CacheEntry entry, ImageTexture tex, PreparedShrink prepared, int w, int h, int nw, int nh, bool mips)
    {
        var img = prepared.Image!;
        try
        {
            if (!GodotObject.IsInstanceValid(tex)) { Interlocked.Increment(ref _statRaced); return; }
            // Evicted, or evicted and replaced, since the shrink was picked.
            bool resident;
            lock (_cache) resident = _cache.TryGetValue(entry.Id, out var live) && ReferenceEquals(live.Res, tex);
            if (!resident) { Interlocked.Increment(ref _statRaced); return; }
            // Something else (a sharpen that ended smaller) got there first.
            if (tex.GetWidth() <= nw && tex.GetHeight() <= nh) { Interlocked.Increment(ref _statRaced); return; }

            long before;
            lock (_cache) before = entry.Size;
            tex.SetImage(img);
            // BUG-PERF-07: the texture's pixels change here, so its alpha numbers do too.
            _alphaStats[entry.Id] = prepared.Stats;
            long after = SLNG.Assets.TextureAdmission.TextureBytes(nw, nh, mips);
            SetResidentSize(entry.Id, tex, after, nw, nh, sharpened: false);
            MarkShrunk(entry.Id, nw, nh);
            if (_shrinkPending.TryGetValue(entry.Id, out var pending) && pending.Visible)
                lock (_cache) entry.SharpenGuardUntilMs = _biasClock.ElapsedMilliseconds + SharpenShrinkGuardMs;
            Interlocked.Increment(ref _statDone);
            Interlocked.Add(ref _statFreedBytes, Math.Max(0, before - after));
            Logger.Info($"[GpuCache] shrank {entry.Id.ToString()[..8]} {w}x{h} -> {nw}x{nh}");
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _statFailed);
            GD.PrintErr($"[GpuCache] shrink {entry.Id} failed: {ex.Message}");
        }
        finally
        {
            img.Dispose();
            _shrinkPending.TryRemove(entry.Id, out _);
        }
    }

    /// <summary>Mark a shrunk texture improvable, but only on a genuine close-up:
    /// <see cref="TryUpgradeCachedTexture"/> requires screenPixelArea >= 4x this, i.e. the object must
    /// fill roughly its full (shrunk) texel count on screen before it pays for a re-decode --
    /// otherwise a shrink while over budget would immediately trigger a re-sharpen and churn.
    ///
    /// <para>FEAT-PERF-25: and recorded as REQUESTED for that same area, so a re-offer that has not come
    /// 4x closer is treated like an admission cut: a far one waits for the cache to fall below low water
    /// and asks for nothing, a near one asks for room. Without it a texture shrunk from the visible tier
    /// counted as "the camera came closer" at its very next offer and made the pass shrink another one
    /// for it.</para></summary>
    private void MarkShrunk(Guid id, int nw, int nh)
    {
        _uploadedForPixelArea[id] = (float)nw * nh / 4f;
        _admissionRequestedArea[id] = (float)nw * nh;
    }

    /// <summary>FEAT-PERF-25: one line per 10 s while the cache is over its budget or the pass did
    /// anything -- to the perf sidecar always, to godot.log under --diag. What it says: how long the
    /// cache was over, what the pass gave back and why it gave back no more (the last scan's skip
    /// table, in MB per reason).</summary>
    private void EmitShrinkStats(long projected)
    {
        long overMs = _statOverMs;
        int scans = _statScans;
        _statOverMs = 0;
        _statScans = 0;
        int started = Interlocked.Exchange(ref _statStarted, 0);
        int done = Interlocked.Exchange(ref _statDone, 0);
        int noSource = Interlocked.Exchange(ref _statNoSource, 0);
        int timedOut = Interlocked.Exchange(ref _statTimedOut, 0);
        int raced = Interlocked.Exchange(ref _statRaced, 0);
        int failed = Interlocked.Exchange(ref _statFailed, 0);
        long freed = Interlocked.Exchange(ref _statFreedBytes, 0);
        long roomWanted = Interlocked.Exchange(ref _statRoomWantedMax, 0);
        if (overMs == 0 && started == 0 && done == 0 && noSource == 0 && timedOut == 0) return;

        long oldestMs = 0;
        long now = _biasClock.ElapsedMilliseconds;
        foreach (var kv in _shrinkPending) oldestMs = Math.Max(oldestMs, now - kv.Value.StartedMs);

        var skips = new System.Text.StringBuilder();
        for (int i = 0; i < (int)Skip.Count; i++)
        {
            if (_scanSkipCount[i] == 0) continue;
            if (skips.Length > 0) skips.Append(' ');
            skips.Append(((Skip)i).ToString().ToLowerInvariant()).Append('=').Append(_scanSkipBytes[i] >> 20)
                 .Append(" MB/").Append(_scanSkipCount[i]);
        }

        UI.StatsOverlay.EmitPerfLine(
            $"[GpuShrink] last {ShrinkStatsIntervalMs / 1000} s: over budget {overMs / 1000.0:0.0} s, projected {projected >> 20} of {Interlocked.Read(ref _maxSize) >> 20} MB, " +
            $"lodBias={SLNG.Assets.TextureLod.GlobalLodBias} | started={started} done={done} freed={freed >> 20} MB noSource={noSource} " +
            $"timedOut={timedOut} raced={raced} failed={failed} inFlight={_shrinkPending.Count} oldest={oldestMs} ms roomWanted={roomWanted >> 20} MB | " +
            $"scans={scans} last scan: seen={_scanSeen} free={_scanFreeCount} visible={_scanVisibleCount} ({_scanVisibleBytes >> 20} MB) " +
            $"reclaimable={Interlocked.Read(ref _reclaimableBytes) >> 20} MB, skipped {skips}");
    }

    /// <summary>FEAT-PERF-25 self test seam: rank the shrink candidates now, as Tick would over budget,
    /// and return them best first. <paramref name="allowVisible"/> as if the cache were far over.</summary>
    internal List<Guid> SelfTestShrinkCandidates(bool allowVisible)
    {
        lock (_cache)
        {
            RebuildShrinkCandidates(_biasClock.ElapsedMilliseconds, allowVisible);
            var ids = new List<Guid>(_shrinkCandidates.Count);
            foreach (var e in _shrinkCandidates) ids.Add(e.Id);
            return ids;
        }
    }

    /// <summary>Self test seam: pretend a texture was last asked for <paramref name="msAgo"/> ago, at
    /// <paramref name="screenPixelArea"/> (0: no area within the window).</summary>
    internal void SelfTestSetSeen(Guid textureId, long msAgo, float screenPixelArea)
    {
        lock (_cache)
        {
            if (!_cache.TryGetValue(textureId, out var e)) return;
            long now = _biasClock.ElapsedMilliseconds;
            e.LastSeenMs = now - msAgo;
            e.RecentArea = screenPixelArea;
            e.RecentAreaMs = screenPixelArea > 0f ? now - msAgo : now - 10 * AreaWindowMs;
            e.EverHadArea |= screenPixelArea > 0f;
        }
    }

    internal void SelfTestBlockShrink(Guid textureId)
    {
        lock (_cache)
            if (_cache.TryGetValue(textureId, out var e)) e.ShrinkBlockedUntilMs = _biasClock.ElapsedMilliseconds + ShrinkBlockedMs;
    }

    internal long SelfTestReclaimableBytes => Interlocked.Read(ref _reclaimableBytes);

    /// <summary>Self test seam: the whole shrink of one resident texture, synchronously -- the worker half
    /// from <paramref name="decoded"/>, then the main-thread half. Returns the new size, or (0, 0).</summary>
    internal (int Width, int Height) SelfTestShrinkNow(Guid textureId, SLNG.Assets.TextureData decoded)
    {
        CacheEntry? entry;
        ImageTexture? tex;
        lock (_cache)
        {
            if (!_cache.TryGetValue(textureId, out entry) || entry.Res is not ImageTexture t) return (0, 0);
            tex = t;
        }
        int w = entry.Width, h = entry.Height, nw = Math.Max(8, w >> 1), nh = Math.Max(8, h >> 1);
        long now = _biasClock.ElapsedMilliseconds;
        if (!_shrinkPending.TryAdd(textureId, new PendingShrink(entry.Size - entry.Size / 4, now, IsVisibleTier(entry, now)))) return (0, 0);
        var prepared = PrepareShrinkImage(decoded, nw, nh, entry.Mipmaps);
        if (prepared.Image == null) { _shrinkPending.TryRemove(textureId, out _); return (0, 0); }
        ApplyShrink(entry, tex, prepared, w, h, nw, nh, entry.Mipmaps);
        return (tex.GetWidth(), tex.GetHeight());
    }

    /// <summary>Self test seam: what a shrink left behind for the sharpen rule -- the area the upload is
    /// now built for, the area it counts as requested for, and whether the sharpen guard is up.</summary>
    internal (float BuiltFor, float RequestedFor, bool Guarded) SelfTestSharpenState(Guid textureId)
    {
        _uploadedForPixelArea.TryGetValue(textureId, out float builtFor);
        _admissionRequestedArea.TryGetValue(textureId, out float requestedFor);
        bool guarded;
        lock (_cache) guarded = _cache.TryGetValue(textureId, out var e) && e.SharpenGuardUntilMs > _biasClock.ElapsedMilliseconds;
        return (builtFor, requestedFor, guarded);
    }

    /// <summary>FEAT-PERF-25: the cache's bytes by kind, for the one-shot [VramBreakdown] line.</summary>
    public (long TextureBytes, int Textures, long MeshBytes, int Meshes, long AvatarBytes, long ProtectedBytes) SizeBreakdown()
    {
        long texBytes = 0, meshBytes = 0, avatarBytes = 0, protectedBytes = 0;
        int textures = 0, meshes = 0;
        lock (_cache)
        {
            foreach (var e in _cache.Values)
            {
                if (e.Res is ImageTexture)
                {
                    texBytes += e.Size;
                    textures++;
                    if (_avatarTextures.ContainsKey(e.Id)) avatarBytes += e.Size;
                    if (_noShrink.ContainsKey(e.Id) || _protectedBakes.ContainsKey(e.Id)) protectedBytes += e.Size;
                }
                else
                {
                    meshBytes += e.Size;
                    meshes++;
                }
            }
        }
        return (texBytes, textures, meshBytes, meshes, avatarBytes, protectedBytes);
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
                _avatarTextures.TryRemove(entry.Id, out _);
                _bakeTextures.TryRemove(entry.Id, out _);
                _noShrink.TryRemove(entry.Id, out _);
                _protectedBakes.TryRemove(entry.Id, out _);
                _bakeSource.TryRemove(entry.Id, out _);
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
            _shrinkCandidates.Clear();
            _candidateCursor = 0;
            _currentSize = 0;
        }
    }
}
