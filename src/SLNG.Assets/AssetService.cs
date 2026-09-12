using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using LibreMetaverse;
using LibreMetaverse.Assets;
using LibreMetaverse.Rendering;
using Microsoft.Extensions.Caching.Memory;
using SLNG.Core;
using SLNG.Net;

namespace SLNG.Assets;

/// <summary>
/// Downloads and decodes LLMesh assets via the networking layer, returning an
/// engine-neutral <see cref="MeshData"/>. No LibreMetaverse type crosses this boundary.
/// Decoding runs on a worker thread; results are cached per mesh id.
/// </summary>
public class AssetService
{
    private static readonly Guid TextureTransparentId = new("8dcd4a48-2d37-4909-9f78-f7a9eb4ef903");
    private static readonly Guid TextureWhiteId = new("5748decc-f629-461c-9a36-a35a221fe21f");
    private static readonly TextureData TextureTransparentData = new(1, 1, new byte[] { 0, 0, 0, 0 });
    private static readonly TextureData TextureWhiteData = new(1, 1, new byte[] { 255, 255, 255, 255 });

    private readonly GridSession _session;
    private readonly string _cacheDir;
    private readonly MemoryCache _memCache;

    // FEAT-PERF-02: separate small cache (no size limit needed -- entries are a trivial marker,
    // not decoded pixel data) for texture ids that just exhausted every retry attempt. See
    // GetTextureAsync's doc comment for why this exists.
    private readonly MemoryCache _recentTextureFailures = new(new MemoryCacheOptions());

    // Texture ids the sim answered 403/401 for (TextureFetchResult.Gone). Unlike
    // _recentTextureFailures (45 s, for a maybe-transient failure) this is a permission decision
    // that will not change this session -- keep it for the whole session so a face that references
    // a denied texture stops re-requesting it every 45 s (which flickers, and spams
    // LibreMetaverse's own texture logger). A restart clears it.
    private readonly ConcurrentDictionary<Guid, byte> _goneTextures = new();

    private readonly ConcurrentDictionary<Guid, Task<MeshData?>> _inflightMeshes = new();
    // Lazy<Task<T>>, not a bare Task<T> -- see GetTextureAsync's comment for why this specific
    // dictionary needs a real single-execution guarantee under a concurrent first-touch race.
    private readonly ConcurrentDictionary<Guid, Lazy<Task<TextureData?>>> _inflightTextures = new();

    // Diagnostic (2026-09-03, "why are textures slow, they should be cached"): where the texture
    // pipeline actually spends its work. Dumped every 200 requests as [TexPipe]. diskCacheHit vs
    // httpFetch tells "familiar scene should pop" from "genuinely new textures"; the avg J2K
    // decode time tells whether a cache hit is even cheap.
    private int _texPipeReq, _texPipeCacheHit, _texPipeHttp;
    private int _texPipeReduced, _texPipeReduceRetry;
    private long _texPipeCacheDecodeTicks;
    private readonly ConcurrentDictionary<Guid, byte> _texPipeDistinct = new();

    private void MaybeDumpTexPipe(Guid id)
    {
        _texPipeDistinct.TryAdd(id, 0);
        int n = _texPipeReq;
        if (n % 200 != 0) return;
        int hit = _texPipeCacheHit, http = _texPipeHttp;
        double avgMs = hit > 0
            ? (double)_texPipeCacheDecodeTicks / hit / System.Diagnostics.Stopwatch.Frequency * 1000.0
            : 0;
        // distinct << req  => the same textures are being re-decoded (a cache upstream isn't
        // sticking); distinct ~ req => the scene genuinely has that many textures.
        Console.Error.WriteLine($"[TexPipe] req={n} distinct={_texPipeDistinct.Count} diskCacheHit={hit} " +
            $"(avg {avgMs:0}ms J2K decode) httpFetch={http} reduced={_texPipeReduced} " +
            $"reduceRetry={_texPipeReduceRetry} inflight={_inflightTextures.Count}");
    }

    // Ids already reported by [TextureGiveUp]. The texture path has several distinct ways to end
    // in null -- exhausted attempts, a degraded sculpt map by design, and the negative-failure
    // cache short-circuiting before anything is even tried -- and all of them reached the renderer
    // as the same blank surface.
    private readonly ConcurrentDictionary<Guid, byte> _giveUpLogged = new();
    private static readonly object _coreJ2kLogLock = new();

    /// <summary>Routes CoreJ2K's own diagnostics away from the console. Its decoder chatters
    /// "Codestream truncated in tile N" (plus LOG/INFO noise) on every partially-received
    /// asset, which on a busy region drowns the log -- one session had 6752 of 6786 lines from
    /// this single message. Redirecting <see cref="Console"/> around the decode call, which the
    /// CoreJ2K call site below already does, does NOT catch it: CoreJ2K writes through its own
    /// FacilityManager logger, not Console. Truncated codestreams are already handled properly
    /// by the caller (flagged degraded, retried, and refused outright for sculpt maps), so the
    /// message carries no information the pipeline isn't acting on. ERROR is still forwarded,
    /// since that indicates something the retry logic may not cover.</summary>
    private sealed class QuietJ2kLogger : CoreJ2K.j2k.util.IMsgLogger
    {
        public void printmsg(int severity, string msg)
        {
            if (severity == CoreJ2K.j2k.util.MsgLogger_Fields.ERROR)
                Console.Error.WriteLine($"[CoreJ2K] {msg}");
        }

        public void println(string str, int flind, int ind) { }
        public void flush() { }
    }

    static AssetService()
    {
        CoreJ2K.j2k.util.FacilityManager.DefaultMsgLogger = new QuietJ2kLogger();

        // Register CoreJ2K's SkiaSharp backend EXPLICITLY. Without this,
        // J2kImage.DecodeToImage<SKBitmap> throws "No image creator registered for target type
        // SkiaSharp.SKBitmap" -- and since the whole call sat behind a bare `catch { }`, the
        // CoreJ2K fallback silently did not exist. Every texture Magick.NET refused therefore
        // rendered blank, even though CoreJ2K reads those files perfectly well.
        //
        // That is exactly the class of asset SL is full of: a progressive J2C that simply stops,
        // which Magick.NET (OpenJPEG) rejects with "Tile part length size inconsistent with
        // stream length". Measured on OSGrid 2026-08-02 -- 14 textures in a single view, several
        // objects fully white.
        //
        // It looked environment-dependent for a long time: the same bytes decoded fine in a plain
        // console process and failed inside Godot. The reason is that the registration normally
        // happens from CoreJ2K.Skia's module initializer, which only runs once the CLR actually
        // loads that assembly -- something a console harness that touches the type does, and the
        // client, which only ever names SKBitmap as a generic argument, does not. Registering
        // here removes the dependency on load order entirely.
        RegisterSkiaImageCreator();
    }

    /// <summary>Registers CoreJ2K's SkiaSharp image creator by reflection.
    ///
    /// Reflection rather than a direct call because the creator types are not public API in
    /// CoreJ2K.Skia 2.3.3 -- they are meant to self-register from the assembly's module
    /// initializer. Forcing the assembly to load is the actual goal here; the explicit Register
    /// call is the belt to that braces. Failing loudly matters: a silently missing registration
    /// is what made every Magick-rejected texture render blank.</summary>
    private static void RegisterSkiaImageCreator()
    {
        try
        {
            var asm = System.Reflection.Assembly.Load("CoreJ2K.Skia");
            System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(asm.ManifestModule.ModuleHandle);

            foreach (var t in asm.GetTypes())
            {
                if (!t.Name.Contains("SKBitmap") || !t.Name.Contains("ImageCreator")) continue;
                var inst = System.Activator.CreateInstance(t, nonPublic: true);
                if (inst == null) continue;
                foreach (var m in typeof(CoreJ2K.Util.ImageFactory).GetMethods())
                {
                    if (m.Name != "Register") continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType.IsInstanceOfType(inst))
                    {
                        m.Invoke(null, new[] { inst });
                        return;
                    }
                }
            }
            Console.Error.WriteLine("[AssetService] CoreJ2K.Skia loaded but no SKBitmap image creator could be registered");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[AssetService] failed to register CoreJ2K's Skia backend: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private readonly ConcurrentDictionary<Guid, Task<PbrMaterialData?>> _inflightMaterials = new();
    private readonly ConcurrentDictionary<Guid, Task<AnimationData?>> _inflightAnimations = new();
    private readonly ConcurrentDictionary<(PrimShape Shape, MeshDetailLevel Lod), Task<MeshData?>> _inflightPrimMeshes = new();
    // Keyed by (map id, sculpt type) — NOT by map id alone. The type byte carries the stitching
    // mode plus the Invert (0x40) / Mirror (0x80) flags, and the same sculpt map is routinely
    // reused within one linkset with different flags (e.g. a tree's left and right branch share
    // one map, one of them mirrored). Keying the in-flight table by id alone made the second
    // request join the first's task and silently receive the FIRST prim's geometry — a mirrored
    // branch rendered unmirrored, so the linkset came apart. See ObjectRenderer.KeyForSculpt for
    // the matching GPU-side key.
    private readonly ConcurrentDictionary<(Guid Id, byte Type), Task<MeshData?>> _inflightSculptMeshes = new();

    public AssetService(GridSession session, string cacheDirectory)
    {
        _session = session;
        _cacheDir = cacheDirectory;
        if (!string.IsNullOrEmpty(_cacheDir) && !Directory.Exists(_cacheDir))
        {
            Directory.CreateDirectory(_cacheDir);
        }

        var opts = new MemoryCacheOptions
        {
            SizeLimit = 256 * 1024 * 1024 // 256 MB RAM cache
        };
        _memCache = new MemoryCache(opts);

        // FEAT-PERF-02: unambiguous proof of which build is actually running -- this class has no
        // Godot-side BuildMarker mechanism (SLNG.Assets stays engine-agnostic, no GD.Print), and
        // the throttle capacities aren't otherwise visible anywhere at runtime. Printed once here
        // so a live client log can be checked against the values in this file's source directly,
        // instead of trusting a rebuild happened.
        Console.WriteLine($"[AssetService] decorative fetch slots={_textureFetchThrottle.Capacity} sculpt fetch slots={_sculptFetchThrottle.Capacity}");
    }

    /// <summary>
    /// Fetches and decodes a mesh by UUID, or null if it cannot be decoded. Concurrent
    /// requests for the same id share a single fetch/decode.
    /// </summary>
    /// <summary>
    /// Generates (and caches) real prim geometry for a procedural <see cref="PrimShape"/> at the
    /// given <see cref="MeshDetailLevel"/>. Meshing runs on a worker thread; identical
    /// (shape, lod) pairs share one cached mesh. Returns null if the shape can't be meshed
    /// (caller falls back to a placeholder).
    ///
    /// <paramref name="lod"/> matters most for curved profiles (sphere/torus/ring): the side
    /// count LibreMetaverse's mesher uses is directly tied to this level (6/12/24 sides for
    /// Low/Medium/High+). Callers should pick it from the object's on-screen size (scale and
    /// distance to camera) -- a large or heavily-scaled curved element meshed at the default
    /// Medium renders as visibly faceted/angular ("origami") instead of a smooth curve. See
    /// <see cref="MeshDetailLevel"/>.
    /// </summary>
    public Task<MeshData?> GetPrimMeshAsync(PrimShape shape, MeshDetailLevel lod = MeshDetailLevel.Medium)
    {
        var key = (shape, lod);
        if (_memCache.TryGetValue(key, out MeshData? cached))
        {
            return Task.FromResult(cached);
        }
        return _inflightPrimMeshes.GetOrAdd(key, async k =>
        {
            try
            {
                var result = await Task.Run(() => PrimMeshService.Generate(k.Shape, ToLibreMetaverseDetailLevel(k.Lod))).ConfigureAwait(false);
                if (result != null)
                {
                    long size = EstimateMeshSize(result);
                    _memCache.Set(k, result, new MemoryCacheEntryOptions { Size = size, SlidingExpiration = TimeSpan.FromMinutes(10) });
                }
                return result;
            }
            finally
            {
                _inflightPrimMeshes.TryRemove(k, out _);
            }
        });
    }

    private static LibreMetaverse.Rendering.DetailLevel ToLibreMetaverseDetailLevel(MeshDetailLevel lod) => lod switch
    {
        MeshDetailLevel.Low => LibreMetaverse.Rendering.DetailLevel.Low,
        MeshDetailLevel.Medium => LibreMetaverse.Rendering.DetailLevel.Medium,
        MeshDetailLevel.High => LibreMetaverse.Rendering.DetailLevel.High,
        MeshDetailLevel.Highest => LibreMetaverse.Rendering.DetailLevel.Highest,
        _ => LibreMetaverse.Rendering.DetailLevel.Medium,
    };

    /// <summary>
    /// Generates (and caches) geometry for a sculpted prim from its sculpt-map texture. The map
    /// is fetched/decoded through the normal texture path; meshing runs on a worker thread.
    /// Returns null if the map can't be fetched/decoded or meshed.
    /// </summary>
    public Task<MeshData?> GetSculptMeshAsync(Guid sculptId, byte sculptType)
    {
        object cacheKey = $"sculptmesh:{sculptId}:{sculptType}";
        if (_memCache.TryGetValue(cacheKey, out MeshData? cached))
        {
            return Task.FromResult(cached);
        }
        return _inflightSculptMeshes.GetOrAdd((sculptId, sculptType), async k =>
        {
            var id = k.Id;
            try
            {
                var map = await GetTextureAsync(id, isSculpt: true).ConfigureAwait(false);
                if (map == null) return null;

                var result = await Task.Run(() =>
                    PrimMeshService.GenerateSculpt(map.Rgba, map.Width, map.Height, k.Type)).ConfigureAwait(false);

                if (result != null)
                {
                    long size = EstimateMeshSize(result);
                    _memCache.Set(cacheKey, result, new MemoryCacheEntryOptions { Size = size, SlidingExpiration = TimeSpan.FromMinutes(10) });
                }
                return result;
            }
            finally
            {
                _inflightSculptMeshes.TryRemove(k, out _);
            }
        });
    }

    private static long EstimateMeshSize(MeshData mesh)
    {
        long total = 0;
        foreach (var sub in mesh.Submeshes)
            total += (long)sub.Positions.Length * 32 + (long)sub.Indices.Length * 4;
        return total > 0 ? total : 1;
    }

    public Task<MeshData?> GetMeshAsync(Guid meshId)
    {
        if (_memCache.TryGetValue(meshId, out MeshData? cached))
        {
            return Task.FromResult(cached);
        }
        return _inflightMeshes.GetOrAdd(meshId, async id =>
        {
            try
            {
                var result = await FetchAndDecodeMeshAsync(id).ConfigureAwait(false);
                if (result != null)
                {
                    long size = 1024 * 10; // rough 10KB estimate per mesh
                    _memCache.Set(id, result, new MemoryCacheEntryOptions { Size = size, SlidingExpiration = TimeSpan.FromMinutes(10) });
                }
                return result;
            }
            finally
            {
                _inflightMeshes.TryRemove(id, out _);
            }
        });
    }

    private async Task<MeshData?> FetchAndDecodeMeshAsync(Guid meshId)
    {
        string? cacheFile = string.IsNullOrEmpty(_cacheDir) ? null : System.IO.Path.Combine(_cacheDir, meshId.ToString() + ".mesh");

        if (cacheFile != null && File.Exists(cacheFile))
        {
            byte[]? cached = null;
            try { cached = await File.ReadAllBytesAsync(cacheFile).ConfigureAwait(false); } catch { }
            if (cached != null && cached.Length > 0)
            {
                var decodedFromCache = await Task.Run(() => Decode(meshId, cached)).ConfigureAwait(false);
                if (decodedFromCache != null) return decodedFromCache;
                // Cached bytes don't decode -- most likely a previously-truncated fetch (see
                // below) that got written to disk before this retry logic existed, or the cache
                // file is otherwise corrupt. Drop it so the fetch below has a clean shot, instead
                // of returning null forever every time this mesh loads.
                try { File.Delete(cacheFile); } catch { }
            }
        }

        if (!_session.IsConnected) return null;

        // FEAT-PERF-02-style retry: LibreMetaverse's own internal RequestMeshAsync HTTP fetch is
        // susceptible to the exact same burst-load truncation OpenSim's embedded HTTP server
        // showed for our own texture GetTexture fetches (see GridSession.
        // FetchTextureViaHttpRangeAsync's doc comment) -- except LibreMetaverse doesn't detect it
        // itself: AssetMesh.Decode() catches the resulting DecompressOSD InvalidDataException,
        // logs "Failed to decode mesh asset", and returns false, which FacetedMesh.
        // TryDecodeFromAsset (called from Decode() below) just turns into a plain null. A single
        // failed attempt used to be permanent -- worse than the pre-fix texture bug, since the
        // truncated bytes were cached to disk unconditionally BEFORE decoding was ever attempted,
        // so every later load of that mesh kept re-reading and re-failing on the same corrupt
        // bytes forever. Re-fetching (a fresh HTTP request) has a real chance of getting a
        // complete stream, same reasoning as the texture retry loop; caching is now gated on a
        // verified-successful decode.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            byte[]? bytes;
            try
            {
                bytes = await _session.FetchMeshDataAsync(meshId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AssetService] Mesh fetch failed for {meshId} (attempt {attempt + 1}/3): {ex.Message}");
                bytes = null;
            }

            if (bytes is { Length: > 0 })
            {
                MeshData? result = null;
                try
                {
                    result = await Task.Run(() => Decode(meshId, bytes)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AssetService] Failed to decode mesh {meshId} (attempt {attempt + 1}/3): {ex.Message}");
                }

                if (result != null)
                {
                    if (cacheFile != null)
                    {
                        try { await File.WriteAllBytesAsync(cacheFile, bytes).ConfigureAwait(false); } catch { }
                    }
                    return result;
                }
            }

            if (attempt < 2) await Task.Delay(250).ConfigureAwait(false);
        }

        return null;
    }

    private static MeshData? Decode(Guid meshId, byte[] bytes)
    {
        var asset = new AssetMesh(new UUID(meshId), bytes);
        var prim = new Primitive { Scale = Vector3.One };
        prim.Textures = new Primitive.TextureEntry(UUID.Zero); // Prevent nullref in TryDecodeFromAsset
        if (!FacetedMesh.TryDecodeFromAsset(prim, asset, DetailLevel.Highest, out var faceted) || faceted is null)
        {
            return null;
        }

        // LibreMetaverse's own weight parser mis-reads vertices with exactly four influences
        // (it keeps scanning for a 0xFF sentinel the SL writer only emits when count < 4 —
        // verified against llmodel.cpp/llvolume.cpp), corrupting every following vertex's
        // weights in that submesh. Re-decode each face's weights from the raw submesh binary
        // with the viewer's exact reader semantics (see MeshSkinWeightDecoder). Faces are
        // matched by Face.ID, which is the original index into the LOD's submesh array.
        var lodArray = asset.MeshData["high_lod"] as LibreMetaverse.StructuredData.OSDArray;

        var submeshes = new List<MeshSubmesh>(faceted.Faces.Count);

        foreach (var face in faceted.Faces)
        {
            if (face.Vertices == null || face.Indices == null || face.Indices.Count == 0)
            {
                continue;
            }

            int vertexCount = face.Vertices.Count;
            var positions = new System.Numerics.Vector3[vertexCount];
            var normals = new System.Numerics.Vector3[vertexCount];
            var uvs = new System.Numerics.Vector2[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                var v = face.Vertices[i];
                if (float.IsNaN(v.Position.X) || float.IsNaN(v.Position.Y) || float.IsNaN(v.Position.Z) ||
                    float.IsInfinity(v.Position.X) || float.IsInfinity(v.Position.Y) || float.IsInfinity(v.Position.Z) ||
                    float.IsNaN(v.Normal.X) || float.IsNaN(v.Normal.Y) || float.IsNaN(v.Normal.Z) ||
                    float.IsInfinity(v.Normal.X) || float.IsInfinity(v.Normal.Y) || float.IsInfinity(v.Normal.Z))
                {
                    Console.WriteLine($"[AssetService] Detected NaN/Infinity in vertex for face {face.ID}, rejecting mesh.");
                    return null;
                }
                positions[i] = new System.Numerics.Vector3(v.Position.X, v.Position.Y, v.Position.Z);
                normals[i] = new System.Numerics.Vector3(v.Normal.X, v.Normal.Y, v.Normal.Z);
                uvs[i] = new System.Numerics.Vector2(v.TexCoord.X, v.TexCoord.Y);
            }

            var indices = new int[face.Indices.Count];
            for (int i = 0; i < indices.Length; i++)
            {
                indices[i] = face.Indices[i];
            }

            // Per-vertex skin weights for rigged mesh. Parallel to Vertices; null on
            // unrigged faces. Joint indices reference the mesh-wide SkinData.JointNames.
            VertexBoneWeights[]? weights = null;
            if (face.Weights != null && face.Weights.Count == vertexCount)
            {
                // Prefer our viewer-exact re-decode of the raw Weights binary (see above);
                // fall back to LibreMetaverse's parse only if the raw block isn't reachable.
                byte[]? weightsBin = null;
                if (lodArray != null && face.ID >= 0 && face.ID < lodArray.Count &&
                    lodArray[face.ID] is LibreMetaverse.StructuredData.OSDMap subMap &&
                    subMap.TryGetValue("Weights", out var wOsd) &&
                    wOsd.Type == LibreMetaverse.StructuredData.OSDType.Binary)
                {
                    weightsBin = wOsd.AsBinary();
                }

                if (weightsBin != null)
                {
                    weights = MeshSkinWeightDecoder.Decode(weightsBin, vertexCount);
                }
                else
                {
                    weights = new VertexBoneWeights[vertexCount];
                    for (int i = 0; i < vertexCount; i++)
                    {
                        var w = face.Weights[i];
                        weights[i] = new VertexBoneWeights(
                            w.Joint0, w.Joint1, w.Joint2, w.Joint3,
                            w.Weight0, w.Weight1, w.Weight2, w.Weight3);
                    }
                }
            }

            submeshes.Add(new MeshSubmesh(positions, normals, uvs, indices, face.ID, weights));
        }

        if (submeshes.Count == 0) return null;

        return new MeshData(submeshes, ConvertSkin(faceted.SkinData));
    }

    /// <summary>Converts LibreMetaverse skin data into the neutral <see cref="MeshSkin"/>, or
    /// null when the mesh is not rigged. LMV stores matrices as flat row-major float[16]
    /// (row-vector convention), which maps directly onto <see cref="System.Numerics.Matrix4x4"/>.
    /// <c>AltInverseBindMatrices</c> carry the mesh's joint-position overrides (their translation
    /// = the joint's overridden local position) — see the renderer's ApplyJointPositionOverrides,
    /// which also consumes <c>LockScaleIfJointPosition</c> (BUG-AVATAR-07).</summary>
    private static MeshSkin? ConvertSkin(MeshSkinData? skin)
    {
        if (skin?.JointNames == null || skin.JointNames.Length == 0) return null;

        int jointCount = skin.JointNames.Length;
        var inverseBinds = new System.Numerics.Matrix4x4[jointCount];
        var ibm = skin.InverseBindMatrices;
        var altIbm = skin.AltInverseBindMatrices;
        System.Numerics.Matrix4x4[]? altInverseBinds = (altIbm != null && altIbm.Length > 0) ? new System.Numerics.Matrix4x4[jointCount] : null;

        for (int j = 0; j < jointCount; j++)
        {
            int o = j * 16;
            inverseBinds[j] = (ibm != null && ibm.Length >= o + 16)
                ? ToMatrix(ibm, o)
                : System.Numerics.Matrix4x4.Identity;

            if (altInverseBinds != null)
            {
                altInverseBinds[j] = (altIbm != null && altIbm.Length >= o + 16)
                    ? ToMatrix(altIbm, o)
                    : new System.Numerics.Matrix4x4(); // Uninitialized matrix (M44=0) so fallback logic triggers
            }
        }

        var bindShape = (skin.BindShapeMatrix != null && skin.BindShapeMatrix.Length >= 16)
            ? ToMatrix(skin.BindShapeMatrix, 0)
            : System.Numerics.Matrix4x4.Identity;

        return new MeshSkin(skin.JointNames, inverseBinds, bindShape, skin.PelvisOffset, altInverseBinds,
            skin.LockScaleIfJointPosition);
    }

    private static System.Numerics.Matrix4x4 ToMatrix(float[] m, int o) => new(
        m[o + 0], m[o + 1], m[o + 2], m[o + 3],
        m[o + 4], m[o + 5], m[o + 6], m[o + 7],
        m[o + 8], m[o + 9], m[o + 10], m[o + 11],
        m[o + 12], m[o + 13], m[o + 14], m[o + 15]);

    /// <summary>
    /// Fetches and decodes a texture (JPEG2000) by UUID into engine-neutral RGBA, or null
    /// if it cannot be decoded. Concurrent requests for the same id share one fetch/decode.
    /// </summary>
    /// <param name="desiredDiscard">FEAT-PERF-02 Phase 2: SL/OpenSim J2K discard level to request
    /// -- 0 (default) is full resolution, higher values ask the simulator to send fewer bytes for
    /// a distant/small object (see docs/specs/FEAT-PERF-02-texture-loading-speed.md's Phase 2.1
    /// write-up). Ignored (forced to 0) when <paramref name="isSculpt"/> -- any truncation
    /// corrupts every vertex position, see the degraded-decode retry logic below.
    /// <para>Known limitation: the id-keyed single-flight dedup below means if two concurrent
    /// callers request the *same* never-before-cached texture id at *different* discard levels
    /// (e.g. two instances of the same object at different distances), only the first caller's
    /// discard is actually fetched -- the second gets that same result. There is also no
    /// "upgrade" path once a texture is cached/GPU-resident (see GpuCache.GetOrUploadTextureAsync):
    /// a texture first seen far away stays at that resolution for the rest of the session even if
    /// the same or another instance later gets closer. Fixing both needs a discard-aware cache
    /// key plus re-fetch-on-upgrade logic -- deliberately deferred (correctness/no-wrong-data
    /// first, smarter caching later), see the spec's Phase 2 notes.</para>
    /// </param>
    /// <param name="priority">FEAT-PERF-02: fetch-queue ordering hint, higher = fetched sooner
    /// when more textures are pending than there are fetch slots. Callers should pass the
    /// object's on-screen prominence (see ObjectRenderer.ComputeTextureLod) so what the
    /// camera is pointed at resolves before distant background scenery. Same first-caller-wins
    /// caveat as <paramref name="desiredDiscard"/>, plus: priority is captured when the fetch is
    /// enqueued and never re-evaluated -- see <see cref="PriorityGate"/>'s doc comment.</param>
    /// <param name="rejectDegraded">Refuse a gap-filled decode and return null instead. For an
    /// avatar bake, CoreJ2K's fill is not a blur but dense speckle noise across skin and clothing
    /// -- the "visibly wrong beats blank" trade-off the regular path makes was calibrated on
    /// Magick's much gentler degradation and does not hold here. Sculpt maps refuse for the same
    /// reason (a degraded map is a shard of geometry), which is why isSculpt already implies it.</param>
    /// <param name="screenPixelArea">How many screen pixels the object using this texture covers,
    /// or 0 for "unknown -- decode it fully". Non-zero lets the JPEG-2000 decoder skip wavelet
    /// levels the screen cannot show (<see cref="TextureLod"/>): measured on 40 real cached assets,
    /// a full decode is 47.6 ms against 13.1 ms at quarter size and 3.8 ms at a sixteenth, and that
    /// decode was the largest remaining cost in a scene load (BUG-NET-11). Only ever a REDUCTION --
    /// the caller still receives at least as many pixels as it asked to display, and
    /// <c>TextureData.SourceWidth/Height</c> say what the full asset would have been so a closer
    /// look can re-decode it sharper.</param>
    public Task<TextureData?> GetTextureAsync(Guid textureId, int desiredDiscard = 0, bool isSculpt = false, float priority = 0f, bool rejectDegraded = false, float screenPixelArea = 0f)
    {
        if (textureId == TextureTransparentId)
            return Task.FromResult<TextureData?>(TextureTransparentData);
        if (textureId == TextureWhiteId)
            return Task.FromResult<TextureData?>(TextureWhiteData);

        // Only FULL decodes are memoized (see FetchDecodeAndCacheTextureAsync), so a hit is always
        // the best available version -- handing it to a caller that asked for a reduced one is
        // strictly better than what it asked for, and it lets a near object's decode serve every
        // distant one afterwards.
        if (_memCache.TryGetValue(textureId, out TextureData? cached))
        {
            return Task.FromResult(cached);
        }

        // FEAT-PERF-02: short-lived negative cache for ids that just exhausted every retry
        // attempt. Without this, a texture id that's genuinely gone from the sim (a deleted/
        // missing asset -- common on older SL/OpenSim content, e.g. a shared freebie whose
        // texture reference outlived the texture itself) pays the SAME full 3-attempt/60s-per-
        // attempt retry cycle every single time something asks for it again. Live-tested: a
        // handful of dead texture ids on one busy event region logged 170-400+ repeat failures
        // EACH in a single session -- every one of those competed for the same scarce fetch-
        // throttle slots that textures which could actually succeed needed. This does not
        // change the eventual answer (still null after this cache expires and it's genuinely
        // retried), it only stops hammering a known-hopeless id in the meantime.
        if (_recentTextureFailures.TryGetValue(textureId, out _))
        {
            return Task.FromResult<TextureData?>(null);
        }

        // Session-permanent: the sim denied this texture (403/401). Retrying it -- or its LMV UDP
        // fallback -- cannot change a permission decision, and doing so every 45 s made a remote
        // avatar's hair face flicker (2026-09-03).
        if (_goneTextures.ContainsKey(textureId))
        {
            return Task.FromResult<TextureData?>(null);
        }

        int effectiveDiscard = isSculpt ? 0 : desiredDiscard;

        // Lazy<Task<T>> (ExecutionAndPublication), not a bare ConcurrentDictionary.GetOrAdd
        // factory -- GetOrAdd's factory delegate is not guaranteed single-execution under a
        // genuine concurrent first-touch race (only the *stored result* is deduplicated, so
        // several racing callers can each start a real fetch/decode before the dictionary
        // settles on one winner). Lazy<T> guarantees the factory below runs at most once per
        // key no matter how many callers hit .Value concurrently -- the property that matters
        // when many objects/faces reference the same never-before-seen texture at once (e.g. a
        // region populating on first login).
        // Sculpt maps are vertex coordinates, never pixels -- a reduced decode would silently drop
        // vertices, so they are always decoded whole (mirrors effectiveDiscard above).
        float effectiveArea = isSculpt ? 0f : screenPixelArea;

        // Keyed by id only, not (id, level): GpuCache already single-flights per texture id, so two
        // levels racing here is not a normal case, and if it happens both results are valid images
        // -- the renderer records the upload as reduced and TryUpgradeCachedTexture sharpens it.
        var lazy = _inflightTextures.GetOrAdd(textureId, id => new Lazy<Task<TextureData?>>(
            () => FetchDecodeAndCacheTextureAsync(id, effectiveDiscard, isSculpt, priority, rejectDegraded, effectiveArea), LazyThreadSafetyMode.ExecutionAndPublication));
        return lazy.Value;
    }

    /// <summary>BUG-AVATAR-02: fetches and decodes an avatar BAKE texture through SL's dedicated
    /// bake-texture host, not the generic per-face path <see cref="GetTextureAsync"/> uses --
    /// see <see cref="GridSession.FetchBakeTextureDataAsync"/>'s own doc comment for why bakes
    /// need an entirely different URL. Deliberately its own small method rather than a parameter
    /// threaded through the generic fetch/cache/dedup pipeline above: bake channels are at most
    /// ~11 per avatar per rebake, nowhere near the volume that pipeline's Lazy-dedup and
    /// negative-cache machinery exist for, so duplicating that complexity here isn't worth the
    /// risk of it interacting badly with the well-tested generic path.
    ///
    /// Falls back to <see cref="GetTextureAsync"/> when the bake-specific fetch returns nothing --
    /// off a Linden grid that's immediate (no network cost, <c>FetchBakeTextureDataAsync</c> short-
    /// circuits), and on a Linden grid it's a reasonable second attempt rather than giving up
    /// outright.
    ///
    /// <paramref name="bakeAgentId"/> is the id of the avatar wearing this bake -- it is part of
    /// the CDN URL path and must be the DISPLAYED avatar, not the local agent (see
    /// <see cref="GridSession.FetchBakeTextureDataAsync"/>).</summary>
    public async Task<TextureData?> GetBakeTextureAsync(Guid textureId, int bakeChannel, float priority = 0f, Guid bakeAgentId = default)
    {
        if (_memCache.TryGetValue(textureId, out TextureData? cached))
        {
            return cached;
        }

        var bakeResult = await _session.FetchBakeTextureDataAsync(textureId, bakeChannel, bakeAgentId).ConfigureAwait(false);
        if (bakeResult.Data is { Length: > 0 } bytes)
        {
            var decoded = await Task.Run(() => DecodeTexture(bytes, isSculpt: false)).ConfigureAwait(false);
            if (decoded != null)
            {
                if (!decoded.IsDegraded)
                {
                    long size = decoded.Width * decoded.Height * 4;
                    if (size <= 0) size = 1024;
                    _memCache.Set(textureId, decoded, new MemoryCacheEntryOptions { Size = size, SlidingExpiration = TimeSpan.FromMinutes(5) });
                }
                return decoded;
            }
        }

        return await GetTextureAsync(textureId, desiredDiscard: 0, priority: priority).ConfigureAwait(false);
    }

    private async Task<TextureData?> FetchDecodeAndCacheTextureAsync(Guid id, int desiredDiscard, bool isSculpt, float priority, bool rejectDegraded = false, float screenPixelArea = 0f)
    {
        try
        {
            var result = await FetchAndDecodeTextureAsync(id, desiredDiscard, isSculpt, priority, rejectDegraded, screenPixelArea).ConfigureAwait(false);
            // Mirror the disk cache's own guard (see FetchAndDecodeTextureAsync) -- a degraded
            // result can still be returned (better than nothing on the last retry attempt), but
            // must never be memoized. Caching it here would pin the bad decode in memory for a
            // full 5-minute sliding window, so every caller for this textureId during that
            // window -- including a same-process relogin, which does NOT restart the app or
            // clear this cache -- gets served the cached noise instead of ever getting a chance
            // to retry. Only a full app restart (a fresh, empty _memCache) let a later fetch
            // attempt succeed, which is why this looked like it needed a full relaunch to fix
            // rather than just logging back in.
            // Only a FULL decode is memoized. A reduced one is specific to how big the object was
            // on screen at that moment, and caching it would hand a blurry image to the next caller
            // -- including TryUpgradeCachedTexture, whose whole job is to fetch the sharp version.
            // Re-decoding a reduced texture costs 3.8-13 ms, so there is little to cache anyway.
            bool full = result != null && result.SourceWidth > 0 && result.Width >= result.SourceWidth;
            if (result != null && !result.IsDegraded && full)
            {
                long size = result.Width * result.Height * 4;
                if (size <= 0) size = 1024;
                _memCache.Set(id, result, new MemoryCacheEntryOptions { Size = size, SlidingExpiration = TimeSpan.FromMinutes(5) });
            }
            else if (result == null)
            {
                // Exhausted all 3 attempts (see FetchAndDecodeTextureAsync) -- don't let the next
                // caller pay that cost again immediately. 45s, not minutes: a texture that failed
                // because the sim/connection was briefly overloaded should still recover this
                // session; only truly-gone assets keep hitting this and keep getting deflected.
                _recentTextureFailures.Set(id, true, TimeSpan.FromSeconds(45));
            }
            return result;
        }
        finally
        {
            _inflightTextures.TryRemove(id, out _);
        }
    }

    // FEAT-PERF-02: two separate pools, not one shared 4-slot gate, so a burst of ordinary
    // decorative-texture fetches can never make a sculpt map (which blocks the object's *shape*,
    // not just its looks -- see GetSculptMeshAsync) queue behind them for up to 60s per attempt.
    // Decorative keeps the original 4 slots (the a14229d UDP-packet-drop-motivated cap) rather
    // than being cut to 3 to carve out the sculpt lane -- an earlier version of this split did
    // 3+1, but for a typical scene (mostly-or-all decorative textures, few/no sculpts) that's a
    // net THROUGHPUT REGRESSION: the sculpt slot sits idle while decorative fetches, the
    // overwhelming common case, lose a quarter of their concurrency. Sculpt gets its own
    // ADDITIONAL slot on top (total 5, not 4) instead. A modest +1 over the original cap is a
    // much smaller bet than the general "raise the cap" question, which stays a separate,
    // protocol-re-reviewed decision (FEAT-PERF-02 Phase 2.3).
    //
    // PriorityGate, not SemaphoreSlim: a plain semaphore admits strictly in arrival order, so
    // whatever the camera is actually pointed at waits behind an arbitrary amount of scenery that
    // merely happened to be requested first. See PriorityGate's doc comment.
    private static readonly PriorityGate _textureFetchThrottle = new PriorityGate(32);
    private static readonly PriorityGate _sculptFetchThrottle = new PriorityGate(4);

    // The disk-cache hit path was a BARE Task.Run per texture -- measured 2026-09-03: a familiar
    // scene is ~99.9% cache hits, but ~1600 J2K decodes fired at the thread pool at once
    // ([TexPipe] avg "91 ms" is mostly pool-queue wait, not decode). Bound them to the CPU so
    // decodes run at full speed without oversubscription, and honour `priority` so the textures
    // the camera is pointed at finish first. Leave 2 cores for the Godot main thread + GC.
    private static readonly PriorityGate _textureDecodeThrottle =
        new PriorityGate(Math.Max(2, Environment.ProcessorCount - 2));

    /// <summary>Picks a decoder reduce level from the codestream's own declared size, without
    /// decoding it. 0 (decode everything) whenever the caller gave no LOD hint, the asset is a
    /// sculpt map, or the header cannot be read -- "decode it whole" is always the safe answer.</summary>
    private static int ReduceFactorFromHeader(byte[] bytes, bool isSculpt, float screenPixelArea)
    {
        if (isSculpt || screenPixelArea <= 0f) return 0;
        if (!TryReadJ2kSize(bytes, out int w, out int h, out _)) return 0;
        return TextureLod.ReduceFactorFor(TextureLod.DiscardLevelFor(w, h, screenPixelArea));
    }

    private async Task<TextureData?> FetchAndDecodeTextureAsync(Guid textureId, int desiredDiscard, bool isSculpt, float priority, bool rejectDegraded = false, float screenPixelArea = 0f)
    {
        System.Threading.Interlocked.Increment(ref _texPipeReq);
        MaybeDumpTexPipe(textureId);

        // FEAT-PERF-02 Phase 2: the disk cache only ever holds complete (discard 0) assets --
        // both reading and writing are gated on desiredDiscard == 0 below. A partial/low-discard
        // fetch is intentionally truncated (see GridSession.FetchTextureDataAsync), not the
        // "whole texture" the cache file name promises; treating it as one would let a future
        // full-resolution request silently get served a blurry cached partial forever.
        string? cacheFile = string.IsNullOrEmpty(_cacheDir) ? null : System.IO.Path.Combine(_cacheDir, textureId.ToString() + "_v5.j2c");

        if (desiredDiscard == 0 && cacheFile != null && File.Exists(cacheFile))
        {
            byte[]? cached = null;
            try { cached = await File.ReadAllBytesAsync(cacheFile).ConfigureAwait(false); } catch { }
            if (cached != null && cached.Length > 0)
            {
                // BUG-NET-11: ask the decoder for only the wavelet levels this object can show.
                // The reduce factor comes from the codestream's OWN declared size (SIZ), read here
                // without decoding, because the right level depends on the asset's resolution and
                // only the header knows it before the work is done.
                int reduce = ReduceFactorFromHeader(cached, isSculpt, screenPixelArea);

                TextureData? decodedFromCache;
                await _textureDecodeThrottle.WaitAsync(priority).ConfigureAwait(false);
                var _texSw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    decodedFromCache = await Task.Run(() => DecodeTexture(cached, isSculpt, reduce)).ConfigureAwait(false);
                    // Only a FULL decode's verdict is trusted. A reduced decode reporting degraded
                    // could be the reduce path itself misbehaving, and believing it would delete a
                    // perfectly good cache file below -- so confirm at full resolution first.
                    if (reduce > 0 && (decodedFromCache == null || decodedFromCache.IsDegraded))
                    {
                        System.Threading.Interlocked.Increment(ref _texPipeReduceRetry);
                        decodedFromCache = await Task.Run(() => DecodeTexture(cached, isSculpt)).ConfigureAwait(false);
                    }
                    else if (reduce > 0)
                    {
                        System.Threading.Interlocked.Increment(ref _texPipeReduced);
                    }
                }
                finally { _textureDecodeThrottle.Release(); }
                System.Threading.Interlocked.Add(ref _texPipeCacheDecodeTicks, _texSw.ElapsedTicks);

                // The cache is only ever WRITTEN for a clean decode, so a degraded result here
                // means the same bytes now decode worse than when they were stored -- which is
                // exactly what happened when the CoreJ2K fallback started working: assets Magick
                // used to read are now gap-filled by CoreJ2K instead, and on an avatar bake that
                // fill is dense speckle noise rather than a blur.
                //
                // This early-return sits BEFORE the retry loop, so it used to bypass both the
                // rejectDegraded contract and every log line -- an avatar could be covered in
                // noise with no [TextureAttempt] and no [TextureGiveUp] entry to show for it,
                // which is precisely how this hid. Drop the stale entry and fall through to a
                // real fetch instead.
                // The `(isSculpt || rejectDegraded)` narrowing this condition used to carry was
                // only ever safe by accident. A dangling `if` below (its body commented out, so
                // the `return` became the body) meant a NORMAL texture never returned from this
                // block at all -- it always fell through to a real re-fetch, which is what
                // repaired a bad cache entry for everything except sculpts and bakes. Fixing that
                // `if` made this branch reachable for normal textures too, and with the narrowing
                // still in place a degraded entry was handed straight to the renderer instead:
                // gap-filled speckle on avatar attachment and clothing textures. Since the cache
                // is only ever WRITTEN on a clean decode, a degraded read PROVES the entry is bad
                // no matter who is asking -- so drop it for every caller.
                if (decodedFromCache != null && decodedFromCache.IsDegraded)
                {
                    Console.Error.WriteLine($"[TextureCache] {textureId}: cached bytes now decode DEGRADED — discarding the cache entry and refetching");
                    try { File.Delete(cacheFile); } catch { }
                }
                else if (decodedFromCache != null)
                {
                    System.Threading.Interlocked.Increment(ref _texPipeCacheHit);
                    return decodedFromCache;
                }
                else
                {
                    try { File.Delete(cacheFile); } catch { }
                }
            }
        }

        // Reached here = the disk cache did not serve it (no file, or a bad entry). Count it as an
        // HTTP-bound request regardless of whether the retry loop below eventually succeeds.
        System.Threading.Interlocked.Increment(ref _texPipeHttp);

        var throttle = isSculpt ? _sculptFetchThrottle : _textureFetchThrottle;

        // Set once an HTTP body has proven undecodable, to force the remaining attempts onto UDP.
        // Retrying HTTP after a decode failure is pointless: the sim serves the identical bytes
        // and the identical failure. Measured on OSGrid 2026-08-02 -- ten textures came back with
        // valid SOC/SIZ headers but far too few bytes for their declared size (32000 for
        // 1024x1024), failed both decoders, burned all three attempts on HTTP, and left their
        // objects white. The UDP path was never reached for any of them.
        bool httpUndecodable = false;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (!_session.IsConnected) return null;

            await throttle.WaitAsync(priority).ConfigureAwait(false);
            byte[]? bytes;
            bool isReliable = false;
            try
            {
                var fetchTask = _session.FetchTextureDataAsync(textureId, desiredDiscard, httpUndecodable);
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(60));
                if (await Task.WhenAny(fetchTask, timeoutTask).ConfigureAwait(false) == fetchTask)
                {
                    var fetchResult = await fetchTask.ConfigureAwait(false);
                    bytes = fetchResult.Data;
                    isReliable = fetchResult.IsReliable;
                    if (fetchResult.Gone)
                    {
                        // Permission denial -- stop now, and remember it for the session so no
                        // later caller re-runs the fetch (or its LMV pipeline fallback).
                        if (_goneTextures.TryAdd(textureId, 0))
                            Console.Error.WriteLine($"[TextureGiveUp] {textureId}: sim denied it (403/401) -- not retrying this session");
                        return null;
                    }
                }
                else
                {
                    bytes = null; // Timeout, will retry or fail
                }
            }
            finally
            {
                throttle.Release();
            }

            if (bytes is { Length: > 0 })
            {
                int httpReduce = ReduceFactorFromHeader(bytes, isSculpt, screenPixelArea);
                var result = await Task.Run(() => DecodeTexture(bytes, isSculpt, httpReduce)).ConfigureAwait(false);
                if (httpReduce > 0 && (result == null || result.IsDegraded))
                {
                    // Same rule as the cache path: never let a reduced decode be the reason we call
                    // an asset broken, retry it whole and judge that.
                    System.Threading.Interlocked.Increment(ref _texPipeReduceRetry);
                    result = await Task.Run(() => DecodeTexture(bytes, isSculpt)).ConfigureAwait(false);
                }
                else if (httpReduce > 0)
                {
                    System.Threading.Interlocked.Increment(ref _texPipeReduced);
                }

                // Both decoders (Magick.NET and the CoreJ2K fallback) refused these bytes, yet the
                // real viewer draws the same assets, so the bytes themselves are the evidence and
                // there is no other way to get at them -- the normal cache is only written on a
                // SUCCESSFUL decode, so a failing texture leaves nothing behind to examine.
                // Written once per id, next to the cache, for offline inspection of the raw
                // codestream (markers, declared dimensions, where it actually ends).
                // Next attempt goes over UDP instead of asking HTTP for the same bytes again.
                //
                // A DEGRADED result belongs here just as much as a null one, and leaving it out was
                // the bug: the retry below (`if (result.IsDegraded) continue`) re-entered the loop
                // with httpUndecodable still false, so all three attempts asked HTTP for the same
                // truncated body and got it, byte for byte. Measured on OSGrid 2026-08-02 --
                // texture 6d9be86d (the ivy statue) fetched 32000 bytes on attempts #0, #1 and #2,
                // Magick.NET rejecting each with "Tile part length size inconsistent with stream
                // length" (the codestream's own SOT declares more data than arrived) while healthy
                // 1024x1024 assets in the same view weigh 285-450 kB. Three retries, zero chance of
                // a different answer, and the complete asset was sitting on the UDP path untouched.
                if (result == null || result.IsDegraded)
                {
                    httpUndecodable = true;
                }

                // Extended from "decode returned null" to "decode was not clean". A degraded decode
                // leaves nothing behind either -- the cache write below is gated on !IsDegraded --
                // so the one artefact that could settle whether 32000 bytes is a whole small asset
                // or a truncated large one was being thrown away on every run. Both cases now land
                // on disk for offline marker inspection.
                if ((result == null || result.IsDegraded) && cacheFile != null)
                {
                    try
                    {
                        string badFile = cacheFile + (result == null ? ".baddecode" : ".degraded");
                        if (!File.Exists(badFile))
                        {
                            await File.WriteAllBytesAsync(badFile, bytes).ConfigureAwait(false);
                            Console.Error.WriteLine($"[TextureFetch] {textureId}: " +
                                (result == null ? "both decoders failed" : "decoded DEGRADED") +
                                $" on {bytes.Length} bytes — saved to {System.IO.Path.GetFileName(badFile)}");
                        }
                    }
                    catch { }
                }

                if (result != null)
                {
                    if (desiredDiscard == 0 && cacheFile != null && !result.IsDegraded)
                    {
                        try { await File.WriteAllBytesAsync(cacheFile, bytes).ConfigureAwait(false); } catch { }
                    }

                    // A "degraded" decode almost always means the J2C bytes we got over the wire
                    // were truncated (dropped UDP packet — see docs/HANDOVER_CLAUDE.md) and the
                    // CoreJ2K fallback filled the gaps with guessed/averaged pixel values. For a
                    // sculpt map that corrupts every vertex position, producing melted/collapsed
                    // geometry — the original reason this retry existed, gated to `isSculpt` only.
                    // That gate was wrong: it assumed a degraded REGULAR texture is just "a one-frame
                    // blur", harmless enough to hand to the renderer as-is. Confirmed false on an
                    // avatar bake texture — the fallback fill produced a full TV-static/noise pattern
                    // across the skin, not a blur, because CoreJ2K's gap-fill degrades far worse than
                    // a blur on some content/resolutions. Retry for every texture, not just sculpts,
                    // as long as attempts remain — a re-fetch has a real chance of getting the
                    // complete bytes. Only surrender to the degraded result on the last attempt (a
                    // wrong-but-present texture still beats none at all).
                    if (result.IsDegraded)
                    {
                        if (isReliable)
                        {
                            if (rejectDegraded)
                            {
                                if (_giveUpLogged.TryAdd(textureId, 0))
                                    Console.Error.WriteLine($"[TextureGiveUp] {textureId}: degraded bake/avatar texture fetched reliably — returning null");
                                return null;
                            }
                            return result;
                        }

                        if (attempt < 2) continue; // retry
                        if (rejectDegraded)
                        {
                            if (_giveUpLogged.TryAdd(textureId, 0))
                                Console.Error.WriteLine($"[TextureGiveUp] {textureId}: degraded bake/avatar texture after 3 attempts — returning null rather than showing gap-fill noise");
                            return null;
                        }
                        // SCULPT MAPS USED TO BE REFUSED HERE TOO. That was measured wrong, twice
                        // over, and the visible cost was high: a refused sculpt map makes the
                        // renderer draw a placeholder cylinder at the object's full scale, so a
                        // reef of rocks came out as smooth flat discs while Firestorm drew rocks
                        // (OSGrid, The Dangazi Forest, 2026-08-22 -- sculpt map bb745170, of which
                        // the sim serves 33,600 bytes for a tile-part declaring 113,049).
                        //
                        // 1. THE REAL VIEWER DECODES PARTIAL STREAMS ON PURPOSE.
                        //    llimagej2coj.cpp:420-421 calls opj_decoder_set_strict_mode(decoder,
                        //    OPJ_FALSE) under the comment "enable decoding partially loaded
                        //    images". A short codestream is not an error case in SL, it is how
                        //    textures stream. Magick.NET gives no access to that flag (verified:
                        //    reduce-factor, quality-layers and non-strict defines all still throw
                        //    "Tile part length size inconsistent" on all 95 truncated assets in a
                        //    real cache), which is exactly why our decode lands on the CoreJ2K
                        //    fallback and gets marked degraded in the first place.
                        //
                        // 2. THE DEGRADED DATA IS NEARLY THE FULL PICTURE. Measured over 12 real
                        //    cached assets, decoding only the first N% of each and comparing to
                        //    its own complete decode: mean absolute error per channel was 1.4/255
                        //    at 75% of the bytes, 1.9 at 30%, and still 4.2 at 2%. It degrades
                        //    smoothly -- there is no fraction at which the image collapses into
                        //    the "gap-fill noise" this refusal was named after.
                        //
                        // 3. FOR A SCULPT THE LOST DETAIL IS DETAIL THAT IS NEVER READ. The
                        //    viewer point-samples a sculpt map onto a grid of at most 32x32
                        //    (SCULPT_REZ_4, llvolume.cpp:3137) whatever the map's resolution, so
                        //    what a truncated codestream costs -- the highest-frequency wavelet
                        //    passes -- is thrown away before a vertex is ever placed.
                        //
                        // The earlier "melted ribbon" observation that motivated the refusal is
                        // not contradicted: that was a sculpt built from gap-filled data, and it
                        // did look wrong. It was just never weighed against what replaces it. A
                        // slightly soft rock beats a cylinder.
                        // Byte count included because it is what distinguishes the two causes of a
                        // persistent degrade: if UDP also delivers this few bytes the asset really
                        // is truncated on the sim and no fetch path can fix it; if UDP delivers a
                        // full-size body that still degrades, the fault is in the decoder.
                        Console.Error.WriteLine($"[TextureGiveUp] {textureId}: returning DEGRADED " +
                            $"{(isSculpt ? "SCULPT map " : "")}{result.Width}x{result.Height} after 3 attempts, " +
                            $"last body {bytes.Length} bytes (better than blank)");
                        return result;
                    }
                    return result;
                }
            }

            await Task.Delay(250).ConfigureAwait(false);
        }

        if (_giveUpLogged.TryAdd(textureId, 0))
            Console.Error.WriteLine($"[TextureGiveUp] {textureId}: all 3 attempts produced no usable bytes");
        return null;
    }

    /// <summary>Resolves a face's LEGACY Blinn-Phong material (normal + specular map), or null if
    /// the region does not know the id.
    ///
    /// <para>Deliberately NOT one request per call. These ids repeat heavily -- a whole build
    /// shares one material -- and the capability takes up to 50 ids at a time, so a per-face
    /// request would turn one round trip into hundreds. Calls made close together are collected
    /// into a single batch, every caller awaits the same batch, and results are memoised.</para>
    ///
    /// <para>A negative result is cached too, briefly: an id the sim does not return would
    /// otherwise be re-requested by every face that references it, forever.</para></summary>
    public Task<LegacyMaterialData?> GetLegacyMaterialAsync(Guid materialId)
    {
        if (materialId == Guid.Empty) return Task.FromResult<LegacyMaterialData?>(null);

        if (_memCache.TryGetValue(LegacyMaterialKey(materialId), out LegacyMaterialData cached))
            return Task.FromResult<LegacyMaterialData?>(cached);
        if (_recentMaterialMisses.TryGetValue(materialId, out _))
            return Task.FromResult<LegacyMaterialData?>(null);

        return _inflightLegacyMaterials.GetOrAdd(materialId, id => QueueLegacyMaterialAsync(id));
    }

    private static object LegacyMaterialKey(Guid id) => $"legacymaterial:{id}";

    private readonly ConcurrentDictionary<Guid, Task<LegacyMaterialData?>> _inflightLegacyMaterials = new();
    private readonly MemoryCache _recentMaterialMisses =
        new(new MemoryCacheOptions { SizeLimit = 4096 });

    // Ids waiting to go out, and the gate that lets one collector send them as a batch.
    private readonly List<Guid> _pendingMaterialIds = new();
    private readonly SemaphoreSlim _materialBatchGate = new(1, 1);

    /// <summary>How long to let ids accumulate before sending. Long enough that a frame's worth of
    /// faces lands in one request, short enough not to be visible -- materials arriving a tick
    /// late costs nothing, a request per face costs a round trip per face.</summary>
    private static readonly TimeSpan MaterialBatchWindow = TimeSpan.FromMilliseconds(100);

    private async Task<LegacyMaterialData?> QueueLegacyMaterialAsync(Guid materialId)
    {
        try
        {
            lock (_pendingMaterialIds) _pendingMaterialIds.Add(materialId);

            await Task.Delay(MaterialBatchWindow).ConfigureAwait(false);
            await _materialBatchGate.WaitAsync().ConfigureAwait(false);
            try
            {
                // Another caller's batch may already have covered this id while we waited on the
                // gate; nothing left to send in that case.
                if (_memCache.TryGetValue(LegacyMaterialKey(materialId), out LegacyMaterialData done))
                    return done;

                Guid[] batch;
                lock (_pendingMaterialIds)
                {
                    batch = _pendingMaterialIds.ToArray();
                    _pendingMaterialIds.Clear();
                }
                if (batch.Length == 0) return null;

                var materials = await _session.FetchLegacyMaterialsAsync(batch).ConfigureAwait(false);
                foreach (var m in materials)
                {
                    _memCache.Set(LegacyMaterialKey(m.Id), m,
                        new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = TimeSpan.FromMinutes(30) });
                }

                // Anything asked for and not returned is a miss -- deflect it for a while rather
                // than letting every face that references it re-request it.
                foreach (var asked in batch)
                {
                    if (!_memCache.TryGetValue(LegacyMaterialKey(asked), out LegacyMaterialData _))
                        _recentMaterialMisses.Set(asked, true,
                            new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2) });
                }
            }
            finally
            {
                _materialBatchGate.Release();
            }

            return _memCache.TryGetValue(LegacyMaterialKey(materialId), out LegacyMaterialData result)
                ? result : null;
        }
        finally
        {
            _inflightLegacyMaterials.TryRemove(materialId, out _);
        }
    }

    /// <summary>
    /// Fetches a GLTF PBR material by UUID and returns its mapped parameters and texture UUIDs.
    /// </summary>
    public Task<PbrMaterialData?> GetMaterialAsync(Guid materialId)
    {
        if (_memCache.TryGetValue(materialId, out PbrMaterialData? cached))
        {
            return Task.FromResult(cached);
        }
        return _inflightMaterials.GetOrAdd(materialId, async id =>
        {
            try
            {
                var result = await FetchMaterialAsync(id).ConfigureAwait(false);
                if (result != null)
                {
                    _memCache.Set(id, result, new MemoryCacheEntryOptions { Size = 1024, SlidingExpiration = TimeSpan.FromMinutes(10) });
                }
                return result;
            }
            finally
            {
                _inflightMaterials.TryRemove(id, out _);
            }
        });
    }

    private async Task<PbrMaterialData?> FetchMaterialAsync(Guid materialId)
    {
        if (!_session.IsConnected)
        {
            return null;
        }

        try
        {
            var asset = await _session.FetchMaterialDataAsync(materialId).ConfigureAwait(false);
            if (asset == null)
            {
                Console.WriteLine($"[AssetService] material {materialId} fetch returned null (RenderMaterials cap gave no data)");
                return null;
            }

            Guid baseColorTex = Guid.Empty;
            Guid normalTex = Guid.Empty;
            Guid ormTex = Guid.Empty;
            Guid emissiveTex = Guid.Empty;

            if (asset.TextureIds != null)
            {
                if (asset.TextureIds.Length > LibreMetaverse.Assets.AssetMaterial.TEXTURE_BASE_COLOR)
                    baseColorTex = asset.TextureIds[LibreMetaverse.Assets.AssetMaterial.TEXTURE_BASE_COLOR].Guid;

                if (asset.TextureIds.Length > LibreMetaverse.Assets.AssetMaterial.TEXTURE_NORMAL)
                    normalTex = asset.TextureIds[LibreMetaverse.Assets.AssetMaterial.TEXTURE_NORMAL].Guid;

                if (asset.TextureIds.Length > LibreMetaverse.Assets.AssetMaterial.TEXTURE_METALLIC_ROUGHNESS)
                    ormTex = asset.TextureIds[LibreMetaverse.Assets.AssetMaterial.TEXTURE_METALLIC_ROUGHNESS].Guid;

                if (asset.TextureIds.Length > LibreMetaverse.Assets.AssetMaterial.TEXTURE_EMISSIVE)
                    emissiveTex = asset.TextureIds[LibreMetaverse.Assets.AssetMaterial.TEXTURE_EMISSIVE].Guid;
            }

            var baseColor = new System.Numerics.Vector4(
                asset.BaseColorFactor.R,
                asset.BaseColorFactor.G,
                asset.BaseColorFactor.B,
                asset.BaseColorFactor.A);

            var emissive = new System.Numerics.Vector3(
                asset.EmissiveFactor.X,
                asset.EmissiveFactor.Y,
                asset.EmissiveFactor.Z);

            // LibreMetaverse.Assets.AssetMaterial.AlphaMode mirrors glTF's own alphaMode 1:1
            // (verified via metadata reflection against LibreMetaverse.dll 3.0.0: enum
            // GltfAlphaMode { Opaque, Blend, Mask }) — an authoritative, creator-declared signal,
            // never a pixel-content guess. Map by name rather than casting the underlying int:
            // GltfAlphaMode's declaration order (Opaque=0, Blend=1, Mask=2) does NOT match our
            // PbrAlphaMode's (Opaque=0, Mask=1, Blend=2), so a raw enum cast would silently swap
            // Mask and Blend.
            var alphaMode = asset.AlphaMode switch
            {
                LibreMetaverse.Assets.GltfAlphaMode.Blend => PbrAlphaMode.Blend,
                LibreMetaverse.Assets.GltfAlphaMode.Mask => PbrAlphaMode.Mask,
                _ => PbrAlphaMode.Opaque,
            };

            return new PbrMaterialData(
                baseColorTex,
                normalTex,
                ormTex,
                emissiveTex,
                baseColor,
                asset.MetallicFactor,
                asset.RoughnessFactor,
                emissive,
                alphaMode,
                asset.AlphaCutoff,
                asset.DoubleSided
            );
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AssetService] Failed to fetch material {materialId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Fetches and decodes an animation (binary BVH) by UUID into engine-neutral keyframe
    /// data, or null if it cannot be decoded. Concurrent requests for the same id share
    /// one fetch/decode.
    /// </summary>
    public Task<AnimationData?> GetAnimationAsync(Guid animId)
    {
        if (_memCache.TryGetValue(animId, out AnimationData? cached))
        {
            return Task.FromResult(cached);
        }
        return _inflightAnimations.GetOrAdd(animId, async id =>
        {
            try
            {
                var result = await FetchAndDecodeAnimationAsync(id).ConfigureAwait(false);
                if (result != null)
                {
                    _memCache.Set(id, result, new MemoryCacheEntryOptions { Size = 4096, SlidingExpiration = TimeSpan.FromMinutes(15) });
                }
                return result;
            }
            finally
            {
                _inflightAnimations.TryRemove(id, out _);
            }
        });
    }

    private async Task<AnimationData?> FetchAndDecodeAnimationAsync(Guid animId)
    {
        byte[]? bytes = null;
        string? cacheFile = string.IsNullOrEmpty(_cacheDir) ? null : System.IO.Path.Combine(_cacheDir, animId.ToString() + ".anim");

        if (cacheFile != null && File.Exists(cacheFile))
        {
            try { bytes = await File.ReadAllBytesAsync(cacheFile).ConfigureAwait(false); } catch { }
        }

        if (bytes == null || bytes.Length == 0)
        {
            if (!_session.IsConnected) return GetDefaultStandingAnimation();

            bytes = await _session.FetchAnimationDataAsync(animId).ConfigureAwait(false);
            if (bytes == null || bytes.Length == 0) return GetDefaultStandingAnimation();

            if (cacheFile != null)
            {
                try { await File.WriteAllBytesAsync(cacheFile, bytes).ConfigureAwait(false); } catch { }
            }
        }

        try
        {
            return await Task.Run(() => AnimationDecodeService.Decode(bytes)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AssetService] Failed to decode animation {animId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Internal (not private) solely so <c>SLNG.Assets.Tests</c> can exercise the
    /// sculpt-map decode path directly — see <see cref="InternalsVisibleToAttribute"/> in the
    /// project file. Not part of the public API.</summary>
    // Both decoders were wrapped in bare `catch { }`, so a texture that neither could read left no
    // trace of WHY -- the caller only ever saw null and the surface rendered blank. Measured
    // 2026-08-02: the same bytes, at the same length, with the same isSculpt/discard arguments,
    // decode successfully in a plain console process and fail inside the client, which is not a
    // question that can be answered without the exception itself. One line per texture per
    // decoder.
    private static readonly ConcurrentDictionary<string, byte> _decodeFailureLogged = new();

    private static void LogDecodeFailure(string decoder, byte[] bytes, Exception ex)
    {
        string head = bytes.Length >= 4
            ? $"{bytes[0]:X2}{bytes[1]:X2}{bytes[2]:X2}{bytes[3]:X2}" : "??";
        if (_decodeFailureLogged.TryAdd($"{decoder}:{bytes.Length}:{head}:{ex.GetType().Name}", 0))
            Console.Error.WriteLine($"[DecodeFail] {decoder} rejected {bytes.Length} bytes (head {head}): " +
                                    $"{ex.GetType().Name}: {ex.Message}");
    }

    /// <summary>Re-decodes a J2C at progressively lower resolution levels until one succeeds.
    ///
    /// Ported in spirit from the viewer's OpenJPEG path (llimagej2coj.cpp:268, :418), which sets
    /// a reduce factor rather than attempting full resolution on partial data. ImageMagick exposes
    /// the same OpenJPEG knob as the `jp2:reduce-factor` define.
    ///
    /// Returns a NON-degraded result: the pixels it produces are real decoded data, not
    /// reconstruction. It is smaller than the asset's nominal size, which the caller handles the
    /// same way it handles any texture that arrives at a lower detail level.</summary>

    /// <summary>Reads the codestream's own declared dimensions from its SIZ marker (ITU-T T.800
    /// Table A-4) without decoding anything. Used both to size a reduce-level decode before paying
    /// for it and to tell a truncated decode from a deliberately reduced one.</summary>
    internal static bool TryReadJ2kSize(byte[] bytes, out int width, out int height, out int components)
    {
        width = height = 0; components = -1;

        // Deliberately NOT gated on the raw-codestream signature (FF 4F). SL and OpenSim serve raw
        // codestreams -- see [[sl-textures-are-raw-codestreams]] -- but Magick.NET's own
        // MagickFormat.J2c encoder emits a JP2-BOXED file (starts 00 00 00 0C 6A 50 20 20), which is
        // what the tests encode with, and the SIZ marker sits inside the contiguous codestream box
        // either way. Scanning for it covers both.
        //
        // The scan is bounded and structurally validated rather than taking the first FF 51 it
        // sees: Lsiz must equal 38 + 3*Csiz (ITU-T T.800 Table A-4), which entropy-coded data
        // essentially never satisfies by accident, and a real SIZ is always within the first few
        // dozen bytes of the codestream.
        int limit = System.Math.Min(bytes.Length - 40, 4096);
        for (int i = 0; i < limit; i++)
        {
            if (bytes[i] != 0xFF || bytes[i + 1] != 0x51) continue; // SIZ marker

            int lsiz = (bytes[i + 2] << 8) | bytes[i + 3];
            // Csiz follows Rsiz/Xsiz/Ysiz/XOsiz/YOsiz/XTsiz/YTsiz/XTOsiz/YTOsiz.
            int csiz = (bytes[i + 38] << 8) | bytes[i + 39];
            if (csiz <= 0 || csiz > 16384 || lsiz != 38 + 3 * csiz) continue;

            int w = (bytes[i + 6] << 24) | (bytes[i + 7] << 16) | (bytes[i + 8] << 8) | bytes[i + 9];
            int h = (bytes[i + 10] << 24) | (bytes[i + 11] << 16) | (bytes[i + 12] << 8) | bytes[i + 13];
            if (w <= 0 || h <= 0) continue;

            width = w; height = h; components = csiz;
            return true;
        }
        return false;
    }

    /// <param name="reduceFactor">JPEG-2000 decoder reduce level -- see <see cref="TextureLod"/>.
    /// 0 decodes the full image. This is a DECODER option on a complete codestream (OpenJPEG's
    /// cp_reduce, reached through Magick's <c>jp2:reduce-factor</c> define); it is unrelated to the
    /// disabled network-side truncation, which fails because Magick will not read a codestream that
    /// ends early. Ignored for sculpt maps, whose samples are vertex coordinates -- decoding those
    /// at a lower resolution would silently drop vertices.</param>
    internal static TextureData? DecodeTexture(byte[] bytes, bool isSculpt = false, int reduceFactor = 0)
    {
        if (isSculpt) reduceFactor = 0;
        try
        {
            // Magick.NET wraps OpenJPEG. On a genuinely truncated/corrupt J2C bitstream (dropped
            // UDP packets — see docs/HANDOVER_CLAUDE.md) it reliably throws (verified: truncating
            // a real codestream to 90% of its length makes Magick.NET raise
            // MagickCoderErrorException "Tile part length size inconsistent with stream length").
            // That hard failure is a FEATURE for sculpt maps, not a bug to route around: it lets us
            // fall through to the CoreJ2K path below and, more importantly, tells us the decode is
            // untrustworthy. CoreJ2K does NOT make that distinction — verified: given the exact
            // same truncated bytes, CoreJ2K.DecodeToImage returns a "successful", full-dimension
            // bitmap with no exception and no warning, silently filling the missing wavelet data
            // with the block's DC/low-frequency average. For a normal photo that's an unnoticeable
            // blur; for a sculpt map, where each pixel is an independent, unrelated vertex XYZ, that
            // averaging melts the whole vertex grid into smooth, edge-free blobs — the exact
            // "melted ribbon" corruption this comment used to blame on Magick's "silent zero-fill".
            // An earlier version of this code unconditionally forced ALL sculpt maps through
            // CoreJ2K to dodge that Magick failure mode, which traded a loud, detectable failure
            // (spiky garbage geometry, or a null decode) for a silent, confident, WRONG one on
            // every sculpt whose bytes arrive incomplete — worse, not better. Verified via a
            // byte-identical decode test with a realistic lossy-quality bitstream that CoreJ2K's
            // decode is NOT otherwise lower fidelity than Magick's when the codestream is intact,
            // so there is no quality reason to prefer it as the primary path. Sculpt maps now go
            // through the same Magick-first, CoreJ2K-fallback path as every other texture.
            var settings = new ImageMagick.MagickReadSettings();
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0x4F)
            {
                settings.Format = ImageMagick.MagickFormat.J2c;
            }
            if (reduceFactor > 0)
            {
                // The define is registered under "jp2" for both the JP2 and raw-J2C coders -- they
                // are the same reader in ImageMagick.
                settings.SetDefine(ImageMagick.MagickFormat.Jp2, "reduce-factor", reduceFactor.ToString());
            }
            using var image = new ImageMagick.MagickImage(bytes, settings);
            image.Warning += (s, e) => { /* Suppress Magick.NET console spam */ };

            int width = (int)image.Width;
            int height = (int)image.Height;

            // Sculpt maps encode vertex XYZ as RGB per pixel, so their samples must reach the mesher
            // as the EXACT bytes the codestream carries -- any tone/gamma transfer warps the spatial
            // coordinates non-linearly.
            //
            // This block used to do `image.ColorSpace = ImageMagick.ColorSpace.RGB` here, intending
            // "treat these samples as linear, don't apply an sRGB transfer". That is not what the
            // property does: assigning ColorSpace CONVERTS the pixels into the target space (the
            // `-colorspace` operator), it does not merely re-tag them (`-set colorspace`). Magick
            // decodes a profile-less J2C as sRGB, so the assignment applied a full sRGB->linear
            // transfer to what are actually raw coordinates -- exactly the warp the comment was
            // trying to prevent. Measured on a real OSGrid sculpt (2026-08-01): with the assignment
            // a mid-height ring's radius wandered between 0.27 and 0.68 (a round cushion rendered as
            // a pointed teardrop); without it the same ring is a constant 0.50 -- a perfect circle,
            // matching Firestorm. Cross-verified three ways on the same cached asset: Magick without
            // the assignment and CoreJ2K (the independent fallback decoder) agree within +/-1 per
            // channel, while the assignment's output differs by ~70 per channel and matches an
            // sRGB->linear curve applied to their shared value to the byte.
            //
            // Leaving ColorSpace untouched is therefore the correct handling: GetPixels() below then
            // returns the codestream's own sample values verbatim, which is what a sculpt map is.
            //
            // Do NOT resize the sculpt map here either. An earlier version force-resized every
            // non-64×64 sculpt to 64×64 with nearest-neighbor (FilterType.Point) here, BEFORE the
            // mesher ever saw it. On a 128×128 organic sculpt (a tree, say), nearest-neighbor keeps
            // only 1 of every 4 pixels — collapsing adjacent branch vertices onto each other,
            // producing zero-area triangles, which the degenerate-triangle filter in
            // PrimMeshService.Convert then deleted, leaving holes the surviving triangles stretched
            // across as long spikes/blades (exactly the "jagged tree" symptom). Passing native
            // resolution through lets the vendored PrimMesher/SculptMap.cs build the correct grid at
            // full detail. (Its own LOD reduction used to bilinear-prescale the bitmap, which is a
            // DIFFERENT bug of the same shape — averaging vertex positions instead of point-sampling
            // them, verified against the real viewer's LLVolume::sculptGenerateMapVertices to always
            // point-sample the native-resolution texel array and never filter/resample it — fixed
            // directly in SculptMap.cs, 2026-08-01.)

            bool isDegraded = false;
            int trueComponents = -1;

            // Verify Magick.NET decoded the FULL codestream, not a low-res thumbnail reconstructed
            // from only the low-frequency/DC wavelet data of a truncated fetch (dropped UDP packet
            // -- see docs/HANDOVER_CLAUDE.md). This check used to be skipped entirely for sculpts
            // (isSculpt gated it out), on the theory that only regular textures need it -- backwards:
            // a sculpt map is exactly the case this matters MOST for. A truncated sculpt J2K decode
            // can keep the declared pixel DIMENSIONS while still being reconstructed from incomplete
            // wavelet data, silently compressing the genuine per-pixel Z (blue-channel) variation
            // toward a narrow low-frequency average -- rendering a real shape (e.g. a tall drum) as
            // a flattened dish, with no exception, no wrong dimensions, nothing else to catch it on.
            // Skipping this check meant a degraded sculpt decode never set IsDegraded, so
            // FetchAndDecodeTextureAsync's retry-on-degraded path (see its own doc comment, which
            // already assumed sculpts WERE covered here) never fired for sculpts at all.
            int trueWidth = -1, trueHeight = -1;

            // One SIZ reader for the whole class (see TryReadJ2kSize) -- this used to be a second,
            // subtly different copy of the same loop, gated on the raw-codestream signature.
            if (TryReadJ2kSize(bytes, out int sizW, out int sizH, out int sizC))
            {
                trueWidth = sizW;
                trueHeight = sizH;
                // Read so a decode that kept the right WIDTH/HEIGHT but silently dropped the alpha
                // plane (e.g. a byte-limited progressive fetch whose alpha tile-part never arrived)
                // is caught below -- the width*height check only catches SPATIAL truncation, not a
                // missing component. An avatar bake decoded that way forces alpha=255 for every
                // pixel further down (ch<4 branch), which defeats the bake's alpha-cutout shaping
                // entirely (e.g. system hair renders as its full, uncut card silhouette instead of
                // styled strands). Not meaningful for sculpts (RGB-only), but harmless to compute.
                trueComponents = sizC;
            }

            // A reduce-level decode is SUPPOSED to come out smaller, so the truncation check has to
            // compare against the size the reduce factor asks for, not the codestream's full size --
            // otherwise every reduced decode reports as a truncated thumbnail, and (worse) the disk
            // cache path deletes the perfectly good .j2c that produced it.
            int expectedWidth = TextureLod.ReducedDimension(trueWidth, reduceFactor);
            int expectedHeight = TextureLod.ReducedDimension(trueHeight, reduceFactor);
            if (trueWidth > 0 && trueHeight > 0 && (width * height < expectedWidth * expectedHeight))
            {
                // Accept the thumbnail but mark as degraded so it isn't cached
                Console.WriteLine($"[AssetService] Magick decoded thumbnail {width}x{height}, expected {expectedWidth}x{expectedHeight}" +
                    (reduceFactor > 0 ? $" (reduce={reduceFactor} of {trueWidth}x{trueHeight})" : "") + ". Marked as degraded.");
                isDegraded = true;
            }

            if (!isSculpt)
            {
                if (image.HasAlpha || image.ChannelCount >= 4) image.ColorSpace = ImageMagick.ColorSpace.Transparent;
                else image.ColorSpace = ImageMagick.ColorSpace.sRGB;
            }

            byte[] rgba;
            using (var pixels = image.GetPixels())
            {
                var raw = pixels.GetValues() ?? Array.Empty<byte>();
                int ch = width > 0 && height > 0 ? raw.Length / (width * height) : 0;

                if (!isSculpt && trueComponents >= 4 && ch < 4 && !isDegraded)
                {
                    Console.WriteLine($"[AssetService] Decoded {ch} channel(s) but header declares {trueComponents} -- alpha plane likely truncated. Marked as degraded.");
                    isDegraded = true;
                }

                if (ch >= 3 && raw.Length >= width * height * ch)
                {
                    rgba = new byte[width * height * 4];
                    for (int p = 0; p < width * height; p++)
                    {
                        int s = p * ch, d = p * 4;
                        rgba[d] = raw[s];
                        rgba[d + 1] = raw[s + 1];
                        rgba[d + 2] = raw[s + 2];
                        rgba[d + 3] = ch >= 4 ? raw[s + 3] : (byte)255;
                    }
                }
                else
                    throw new Exception("Magick.NET decode invalid channels");
            }

            // SourceWidth/Height carry the codestream's own size, so the renderer can tell a
            // deliberately reduced decode (still worth sharpening later) from one already at full
            // resolution (never is). Falls back to the decoded size for a non-J2C image.
            return new TextureData(width, height, rgba, isDegraded,
                trueWidth > 0 ? trueWidth : width, trueHeight > 0 ? trueHeight : height);
        }
        catch (Exception magickEx)
        {
            LogDecodeFailure("Magick.NET", bytes, magickEx);

            // Decode FEWER RESOLUTION LEVELS instead of guessing the missing ones -- what the real
            // viewer does (llimagej2coj.cpp: parameters.cp_reduce = discardLevel, and
            // opj_set_decoded_resolution_factor). A progressive J2C carries its low resolution
            // levels COMPLETE; only the highest is cut off. Asking for one level less therefore
            // yields a smaller but genuinely CLEAN image, which is exactly why a still-loading
            // texture in SL looks soft and never speckled.
            //
            // This is the difference between the two failure modes seen here: CoreJ2K's gap fill
            // produced dense speckle noise (unusable on an avatar), while a reduced-resolution
            // decode is simply a lower mip. Tried before the CoreJ2K fallback so the clean answer
            // wins whenever it exists.
            // No reduced-resolution retry here any more. It never once succeeded (ImageMagick
            // rejects these streams while parsing tile-part headers, so no reduce factor can
            // help), and measuring the CoreJ2K fallback below on ten real samples showed it
            // decodes them CLEANLY at full resolution -- speckle 1.2-2.5 per channel, RISING when
            // downscaled, which is the signature of an ordinary image rather than gap fill. The
            // premise that these assets needed reconstruction at all was wrong.
            // Magick.NET (OpenJP2) is very strict and fails on missing EOC markers or bad header lengths
            // common in older SL/OpenSim assets. Fall back to CoreJ2K, which is much more forgiving.
            try
            {
                SkiaSharp.SKBitmap? bitmap = null;
                lock (_coreJ2kLogLock)
                {
                    var originalOut = Console.Out;
                    var originalError = Console.Error;
                    try
                    {
                        Console.SetOut(System.IO.TextWriter.Null);
                        Console.SetError(System.IO.TextWriter.Null);
                        bitmap = CoreJ2K.J2kImage.DecodeToImage<SkiaSharp.SKBitmap>(bytes);
                    }
                    finally
                    {
                        Console.SetOut(originalOut);
                        Console.SetError(originalError);
                    }
                }

                if (bitmap != null)
                {
                    using (bitmap)
                    {
                        if (bitmap.Width > 0 && bitmap.Height > 0)
                        {
                            // Do NOT resize sculpt maps (see the Magick path above for why) —
                            // MeshFoundry downscales them itself with proper linear averaging.
                            var targetBitmap = bitmap;

                            // Ensure the bitmap is converted to Rgba8888 for Godot's Image.CreateFromData
                            var rgbaBitmap = targetBitmap.ColorType == SkiaSharp.SKColorType.Rgba8888
                                ? targetBitmap
                                : targetBitmap.Copy(SkiaSharp.SKColorType.Rgba8888);

                            int width = targetBitmap.Width;
                            int height = targetBitmap.Height;
                            byte[] exactRgba = new byte[width * height * 4];

                            if (rgbaBitmap.RowBytes == width * 4)
                            {
                                System.Runtime.InteropServices.Marshal.Copy(rgbaBitmap.GetPixels(), exactRgba, 0, exactRgba.Length);
                            }
                            else
                            {
                                IntPtr ptr = rgbaBitmap.GetPixels();
                                for (int y = 0; y < height; y++)
                                {
                                    System.Runtime.InteropServices.Marshal.Copy(ptr + y * rgbaBitmap.RowBytes, exactRgba, y * width * 4, width * 4);
                                }
                            }

                            if (targetBitmap != bitmap) targetBitmap.Dispose();
                            if (rgbaBitmap != targetBitmap && rgbaBitmap != bitmap) rgbaBitmap.Dispose();

                            // We intentionally use System.Diagnostics.Trace.Listeners to suppress CoreJ2K's internal Trace logs?
                            // Actually, just returning the exact array fixes the "sim looks weird" bug (skewed/failed textures).
                            // CoreJ2K always decodes full resolution -- it is the fallback for
                            // bytes Magick refused, and no reduce factor was applied here.
                            return new TextureData(width, height, exactRgba, true, width, height); // degraded: used the fallback
                        }
                    }
                }
            }
            catch (Exception coreEx)
            {
                LogDecodeFailure("CoreJ2K", bytes, coreEx);
                // Both standard and CoreJ2K decode failed. This is a truly corrupt asset.
                // We intentionally suppress the error logs here to avoid console spam during region crossings.
            }
            return null;
        }
    }

    private static AnimationData GetDefaultStandingAnimation()
    {
        return new AnimationData
        {
            Length = 10f,
            Loop = true,
            InPoint = 0f,
            OutPoint = 10f,
            Priority = 2,
            Joints = new AnimationJointData[]
            {
                new AnimationJointData
                {
                    JointName = "mArmLeft",
                    Priority = 2,
                    RotationKeys = new[] { new RotationKeyframe(0f, System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitZ, 0.95f)) }
                },
                new AnimationJointData
                {
                    JointName = "mArmRight",
                    Priority = 2,
                    RotationKeys = new[] { new RotationKeyframe(0f, System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitZ, -0.95f)) }
                }
            }
        };
    }
}
