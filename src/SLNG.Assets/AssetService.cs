using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using LibreMetaverse;
using LibreMetaverse.Assets;
using LibreMetaverse.Rendering;
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
    private readonly GridSession _session;
    private readonly string _cacheDir;
    private readonly MemoryCache _memCache;
    
    private readonly ConcurrentDictionary<Guid, Task<MeshData?>> _inflightMeshes = new();
    private readonly ConcurrentDictionary<Guid, Task<TextureData?>> _inflightTextures = new();
    private static readonly object _coreJ2kLogLock = new();
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
        return _inflightPrimMeshes.GetOrAdd(key, async k => {
            try {
                var result = await Task.Run(() => PrimMeshService.Generate(k.Shape, ToLibreMetaverseDetailLevel(k.Lod))).ConfigureAwait(false);
                if (result != null) {
                    long size = EstimateMeshSize(result);
                    _memCache.Set(k, result, new MemoryCacheEntryOptions { Size = size, SlidingExpiration = TimeSpan.FromMinutes(10) });
                }
                return result;
            } finally {
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
        return _inflightSculptMeshes.GetOrAdd((sculptId, sculptType), async k => {
            var id = k.Id;
            try {
                var map = await GetTextureAsync(id, true).ConfigureAwait(false);
                if (map == null) return null;

                var result = await Task.Run(() =>
                    PrimMeshService.GenerateSculpt(map.Rgba, map.Width, map.Height, k.Type)).ConfigureAwait(false);

                if (result != null) {
                    long size = EstimateMeshSize(result);
                    _memCache.Set(cacheKey, result, new MemoryCacheEntryOptions { Size = size, SlidingExpiration = TimeSpan.FromMinutes(10) });
                }
                return result;
            } finally {
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
        return _inflightMeshes.GetOrAdd(meshId, async id => {
            try {
                var result = await FetchAndDecodeMeshAsync(id).ConfigureAwait(false);
                if (result != null) {
                    long size = 1024 * 10; // rough 10KB estimate per mesh
                    _memCache.Set(id, result, new MemoryCacheEntryOptions { Size = size, SlidingExpiration = TimeSpan.FromMinutes(10) });
                }
                return result;
            } finally {
                _inflightMeshes.TryRemove(id, out _);
            }
        });
    }

    private async Task<MeshData?> FetchAndDecodeMeshAsync(Guid meshId)
    {
        byte[]? bytes = null;
        string? cacheFile = string.IsNullOrEmpty(_cacheDir) ? null : System.IO.Path.Combine(_cacheDir, meshId.ToString() + ".mesh");

        if (cacheFile != null && File.Exists(cacheFile))
        {
            try { bytes = await File.ReadAllBytesAsync(cacheFile).ConfigureAwait(false); } catch { }
        }

        if (bytes == null || bytes.Length == 0)
        {
            if (!_session.IsConnected) return null;

            bytes = await _session.FetchMeshDataAsync(meshId).ConfigureAwait(false);
            if (bytes == null || bytes.Length == 0) return null;

            if (cacheFile != null)
            {
                try { await File.WriteAllBytesAsync(cacheFile, bytes).ConfigureAwait(false); } catch { }
            }
        }

        try
        {
            return await Task.Run(() => Decode(meshId, bytes)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AssetService] Failed to decode mesh {meshId}: {ex.Message}");
            return null;
        }
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
    /// = the joint's overridden local position) — see the renderer's ApplyJointPositionOverrides.</summary>
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

        return new MeshSkin(skin.JointNames, inverseBinds, bindShape, skin.PelvisOffset, altInverseBinds);
    }

    private static System.Numerics.Matrix4x4 ToMatrix(float[] m, int o) => new(
        m[o + 0],  m[o + 1],  m[o + 2],  m[o + 3],
        m[o + 4],  m[o + 5],  m[o + 6],  m[o + 7],
        m[o + 8],  m[o + 9],  m[o + 10], m[o + 11],
        m[o + 12], m[o + 13], m[o + 14], m[o + 15]);

    /// <summary>
    /// Fetches and decodes a texture (JPEG2000) by UUID into engine-neutral RGBA, or null
    /// if it cannot be decoded. Concurrent requests for the same id share one fetch/decode.
    /// </summary>
    public Task<TextureData?> GetTextureAsync(Guid textureId, bool isSculpt = false)
    {
        if (_memCache.TryGetValue(textureId, out TextureData? cached))
        {
            return Task.FromResult(cached);
        }
        return _inflightTextures.GetOrAdd(textureId, async id => {
            try {
                var result = await FetchAndDecodeTextureAsync(id, isSculpt).ConfigureAwait(false);
                if (result != null) {
                    long size = result.Width * result.Height * 4;
                    if (size <= 0) size = 1024;
                    _memCache.Set(id, result, new MemoryCacheEntryOptions { Size = size, SlidingExpiration = TimeSpan.FromMinutes(5) });
                }
                return result;
            } finally {
                _inflightTextures.TryRemove(id, out _);
            }
        });
    }

    private static readonly SemaphoreSlim _textureFetchThrottle = new SemaphoreSlim(4, 4);

    private async Task<TextureData?> FetchAndDecodeTextureAsync(Guid textureId, bool isSculpt)
    {
        string? cacheFile = string.IsNullOrEmpty(_cacheDir) ? null : System.IO.Path.Combine(_cacheDir, textureId.ToString() + "_v5.j2c");

        if (cacheFile != null && File.Exists(cacheFile))
        {
            byte[]? cached = null;
            try { cached = await File.ReadAllBytesAsync(cacheFile).ConfigureAwait(false); } catch { }
            if (cached != null && cached.Length > 0)
            {
                var decodedFromCache = await Task.Run(() => DecodeTexture(cached, isSculpt)).ConfigureAwait(false);
                if (decodedFromCache != null) return decodedFromCache;
                try { File.Delete(cacheFile); } catch { }
            }
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (!_session.IsConnected) return null;

            await _textureFetchThrottle.WaitAsync().ConfigureAwait(false);
            byte[]? bytes;
            try
            {
                var fetchTask = _session.FetchTextureDataAsync(textureId);
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(60));
                if (await Task.WhenAny(fetchTask, timeoutTask).ConfigureAwait(false) == fetchTask)
                {
                    bytes = await fetchTask.ConfigureAwait(false);
                }
                else
                {
                    bytes = null; // Timeout, will retry or fail
                }
            }
            finally
            {
                _textureFetchThrottle.Release();
            }

            if (bytes is { Length: > 0 })
            {
                var result = await Task.Run(() => DecodeTexture(bytes, isSculpt)).ConfigureAwait(false);
                if (result != null)
                {
                    if (cacheFile != null && !result.IsDegraded)
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
                        if (attempt < 2) continue; // retry
                        if (isSculpt) return null; // a degraded sculpt is a giant shard that ruins the view — force the caller's placeholder fallback instead
                        return result; // no better option left for a regular texture; visibly wrong beats a permanently blank/placeholder surface
                    }
                    return result;
                }
            }

            await Task.Delay(250).ConfigureAwait(false);
        }

        return null;
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
        return _inflightMaterials.GetOrAdd(materialId, async id => {
            try {
                var result = await FetchMaterialAsync(id).ConfigureAwait(false);
                if (result != null) {
                    _memCache.Set(id, result, new MemoryCacheEntryOptions { Size = 1024, SlidingExpiration = TimeSpan.FromMinutes(10) });
                }
                return result;
            } finally {
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
                asset.AlphaCutoff
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
        return _inflightAnimations.GetOrAdd(animId, async id => {
            try {
                var result = await FetchAndDecodeAnimationAsync(id).ConfigureAwait(false);
                if (result != null) {
                    _memCache.Set(id, result, new MemoryCacheEntryOptions { Size = 4096, SlidingExpiration = TimeSpan.FromMinutes(15) });
                }
                return result;
            } finally {
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
    internal static TextureData? DecodeTexture(byte[] bytes, bool isSculpt = false)
    {
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
            using var image = new ImageMagick.MagickImage(bytes, settings);
            image.Warning += (s, e) => { /* Suppress Magick.NET console spam */ };

            int width = (int)image.Width;
            int height = (int)image.Height;

            // Sculpt maps encode vertex XYZ as RGB per pixel. Read them as linear RGB (never
            // sRGB — a gamma transfer would warp the spatial coordinates).
            //
            // Do NOT resize the sculpt map here. MeshFoundry's own SculptMap does the LOD
            // downscaling internally with proper *linear* averaging of adjacent vertices (see
            // vendored PrimMesher/SculptMap.cs — "the scaling is done in floating point ... the
            // position will be averaged between pixel values"). An earlier version force-resized
            // every non-64×64 sculpt to 64×64 with nearest-neighbor (FilterType.Point) here,
            // BEFORE MeshFoundry ever saw it. On a 128×128 organic sculpt (a tree, say), nearest-
            // neighbor keeps only 1 of every 4 pixels — collapsing adjacent branch vertices onto
            // each other, producing zero-area triangles, which the degenerate-triangle filter in
            // PrimMeshService.Convert then deleted, leaving holes the surviving triangles stretched
            // across as long spikes/blades (exactly the "jagged tree" symptom). Passing native
            // resolution through lets MeshFoundry build the correct grid and scale it properly.
            if (isSculpt)
            {
                image.ColorSpace = ImageMagick.ColorSpace.RGB;
            }

            bool isDegraded = false;

            if (!isSculpt)
            {
                // For normal textures, verify if Magick.NET decoded a low-res thumbnail instead of the full image
                int trueWidth = -1, trueHeight = -1;

                if (settings.Format == ImageMagick.MagickFormat.J2c)
                {
                    for (int i = 0; i < bytes.Length - 13; i++)
                    {
                        if (bytes[i] == 0xFF && bytes[i + 1] == 0x51) // SIZ marker
                        {
                            trueWidth = (bytes[i + 6] << 24) | (bytes[i + 7] << 16) | (bytes[i + 8] << 8) | bytes[i + 9];
                            trueHeight = (bytes[i + 10] << 24) | (bytes[i + 11] << 16) | (bytes[i + 12] << 8) | bytes[i + 13];
                            break;
                        }
                    }
                }

                if (trueWidth > 0 && trueHeight > 0 && (width * height < trueWidth * trueHeight))
                {
                    // Accept the thumbnail but mark as degraded so it isn't cached
                    Console.WriteLine($"[AssetService] Magick decoded thumbnail {width}x{height}, expected {trueWidth}x{trueHeight}. Marked as degraded.");
                    isDegraded = true;
                }

                if (image.HasAlpha || image.ChannelCount >= 4) image.ColorSpace = ImageMagick.ColorSpace.Transparent;
                else image.ColorSpace = ImageMagick.ColorSpace.sRGB;
            }

            byte[] rgba;
            using (var pixels = image.GetPixels())
            {
                var raw = pixels.GetValues() ?? Array.Empty<byte>();
                int ch = width > 0 && height > 0 ? raw.Length / (width * height) : 0;
                if (ch >= 3 && raw.Length >= width * height * ch)
                {
                    rgba = new byte[width * height * 4];
                    for (int p = 0; p < width * height; p++)
                    {
                        int s = p * ch, d = p * 4;
                        rgba[d]     = raw[s];
                        rgba[d + 1] = raw[s + 1];
                        rgba[d + 2] = raw[s + 2];
                        rgba[d + 3] = ch >= 4 ? raw[s + 3] : (byte)255;
                    }
                }
                else
                    throw new Exception("Magick.NET decode invalid channels");
            }

            return new TextureData(width, height, rgba, isDegraded);
        }
        catch
        {
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
                            return new TextureData(width, height, exactRgba, true); // Mark as degraded so we know it used the fallback
                        }
                    }
                }
            }
            catch
            {
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
