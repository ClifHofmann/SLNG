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
    private readonly ConcurrentDictionary<Guid, Task<PbrMaterialData?>> _inflightMaterials = new();
    private readonly ConcurrentDictionary<Guid, Task<AnimationData?>> _inflightAnimations = new();
    private readonly ConcurrentDictionary<PrimShape, Task<MeshData?>> _inflightPrimMeshes = new();
    private readonly ConcurrentDictionary<Guid, Task<MeshData?>> _inflightSculptMeshes = new();

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
    /// Generates (and caches) real prim geometry for a procedural <see cref="PrimShape"/>.
    /// Meshing runs on a worker thread; identical shapes share one cached mesh. Returns null
    /// if the shape can't be meshed (caller falls back to a placeholder).
    /// </summary>
    public Task<MeshData?> GetPrimMeshAsync(PrimShape shape)
    {
        if (_memCache.TryGetValue(shape, out MeshData? cached))
        {
            return Task.FromResult(cached);
        }
        return _inflightPrimMeshes.GetOrAdd(shape, async s => {
            try {
                var result = await Task.Run(() => PrimMeshService.Generate(s)).ConfigureAwait(false);
                if (result != null) {
                    long size = EstimateMeshSize(result);
                    _memCache.Set(s, result, new MemoryCacheEntryOptions { Size = size, SlidingExpiration = TimeSpan.FromMinutes(10) });
                }
                return result;
            } finally {
                _inflightPrimMeshes.TryRemove(s, out _);
            }
        });
    }

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
        return _inflightSculptMeshes.GetOrAdd(sculptId, async id => {
            try {
                var map = await GetTextureAsync(id, true).ConfigureAwait(false);
                if (map == null) return null;

                var result = await Task.Run(() =>
                    PrimMeshService.GenerateSculpt(map.Rgba, map.Width, map.Height, sculptType)).ConfigureAwait(false);
                if (result != null) {
                    long size = EstimateMeshSize(result);
                    _memCache.Set(cacheKey, result, new MemoryCacheEntryOptions { Size = size, SlidingExpiration = TimeSpan.FromMinutes(10) });
                }
                return result;
            } finally {
                _inflightSculptMeshes.TryRemove(id, out _);
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
        if (!FacetedMesh.TryDecodeFromAsset(new Primitive(), asset, DetailLevel.Highest, out var faceted) || faceted is null)
        {
            return null;
        }

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
                weights = new VertexBoneWeights[vertexCount];
                for (int i = 0; i < vertexCount; i++)
                {
                    var w = face.Weights[i];
                    weights[i] = new VertexBoneWeights(
                        w.Joint0, w.Joint1, w.Joint2, w.Joint3,
                        w.Weight0, w.Weight1, w.Weight2, w.Weight3);
                }
            }

            submeshes.Add(new MeshSubmesh(positions, normals, uvs, indices, face.ID, weights));
        }

        if (submeshes.Count == 0) return null;

        return new MeshData(submeshes, ConvertSkin(faceted.SkinData));
    }

    /// <summary>Converts LibreMetaverse skin data into the neutral <see cref="MeshSkin"/>, or
    /// null when the mesh is not rigged. LMV stores matrices as flat row-major float[16]
    /// (row-vector convention), which maps directly onto <see cref="System.Numerics.Matrix4x4"/>.</summary>
    private static MeshSkin? ConvertSkin(MeshSkinData? skin)
    {
        if (skin?.JointNames == null || skin.JointNames.Length == 0) return null;

        int jointCount = skin.JointNames.Length;
        var inverseBinds = new System.Numerics.Matrix4x4[jointCount];
        var ibm = skin.InverseBindMatrices;
        for (int j = 0; j < jointCount; j++)
        {
            int o = j * 16;
            inverseBinds[j] = (ibm != null && ibm.Length >= o + 16)
                ? ToMatrix(ibm, o)
                : System.Numerics.Matrix4x4.Identity;
        }

        var bindShape = (skin.BindShapeMatrix != null && skin.BindShapeMatrix.Length >= 16)
            ? ToMatrix(skin.BindShapeMatrix, 0)
            : System.Numerics.Matrix4x4.Identity;

        return new MeshSkin(skin.JointNames, inverseBinds, bindShape, skin.PelvisOffset);
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

            var bytes = await _session.FetchTextureDataAsync(textureId).ConfigureAwait(false);
            if (bytes is { Length: > 0 })
            {
                var result = await Task.Run(() => DecodeTexture(bytes, isSculpt)).ConfigureAwait(false);
                if (result != null)
                {
                    if (cacheFile != null && !result.IsDegraded)
                    {
                        try { await File.WriteAllBytesAsync(cacheFile, bytes).ConfigureAwait(false); } catch { }
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

            return new PbrMaterialData(
                baseColorTex,
                normalTex,
                ormTex,
                emissiveTex,
                baseColor,
                asset.MetallicFactor,
                asset.RoughnessFactor,
                emissive
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
            if (!_session.IsConnected) return null;

            bytes = await _session.FetchAnimationDataAsync(animId).ConfigureAwait(false);
            if (bytes == null || bytes.Length == 0) return null;

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

    private static TextureData? DecodeTexture(byte[] bytes, bool isSculpt = false)
    {
        try
        {
            // Magick.NET wraps OpenJPEG and seamlessly handles malformed J2C bitstreams (missing EOC, trailing padding, etc.) that crash CoreJ2K.
            var settings = new ImageMagick.MagickReadSettings { Format = ImageMagick.MagickFormat.J2c };
            using var image = new ImageMagick.MagickImage(bytes, settings);
            
            int width = (int)image.Width;
            int height = (int)image.Height;

            // Sculpt maps MUST be 64x64 for MeshFoundry to build the correct 3D topology.
            if (isSculpt && (width != 64 || height != 64))
            {
                image.Resize(new ImageMagick.MagickGeometry("64x64!") { IgnoreAspectRatio = true });
                width = (int)image.Width;
                height = (int)image.Height;
            }
            bool isDegraded = false;
            
            if (!isSculpt)
            {
                // For normal textures, verify if Magick.NET decoded a low-res thumbnail instead of the full image
                int trueWidth = -1, trueHeight = -1;
                for (int i = 0; i < bytes.Length - 13; i++)
                {
                    if (bytes[i] == 0xFF && bytes[i + 1] == 0x51) // SIZ marker
                    {
                        trueWidth = (bytes[i + 6] << 24) | (bytes[i + 7] << 16) | (bytes[i + 8] << 8) | bytes[i + 9];
                        trueHeight = (bytes[i + 10] << 24) | (bytes[i + 11] << 16) | (bytes[i + 12] << 8) | bytes[i + 13];
                        break;
                    }
                }
                
                if (trueWidth > 0 && trueHeight > 0 && (width * height < trueWidth * trueHeight))
                {
                    // Accept the thumbnail but mark as degraded so it isn't cached
                    Console.WriteLine($"[AssetService] Magick decoded thumbnail {width}x{height}, expected {trueWidth}x{trueHeight}. Marked as degraded.");
                    isDegraded = true;
                }
            }

            // Ensure we have RGBA output
            if (image.HasAlpha) image.ColorSpace = ImageMagick.ColorSpace.Transparent;
            else image.ColorSpace = ImageMagick.ColorSpace.sRGB;
            // Ensure an alpha channel so the RGBA mapping below is always well defined.
            if (!image.HasAlpha) image.Alpha(ImageMagick.AlphaOption.Opaque);

            byte[] rgba;
            using (var pixels = image.GetPixels())
            {
                // Map whatever channel layout Magick decoded (3, 4, even 5 channels for some SL
                // skin bakes) straight to RGBA — handles odd channel counts that the old manual
                // stride loop rejected outright, leaving avatars with no skin texture.
                var raw = pixels.ToByteArray(ImageMagick.PixelMapping.RGBA);
                int need = width * height * 4;
                if (raw == null || raw.Length < need)
                {
                    Console.WriteLine($"[AssetService] Could not map texture to RGBA (channels {image.ChannelCount}, got {raw?.Length ?? 0}/{need})");
                    return null;
                }
                rgba = raw.Length == need ? raw : raw[..need];
            }

            return new TextureData(width, height, rgba, isDegraded);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AssetService] Magick.NET failed to decode texture ({ex.Message}). Trying padded recovery...");
            
            try 
            {
                byte[] padded = new byte[bytes.Length + 65536];
                Buffer.BlockCopy(bytes, 0, padded, 0, bytes.Length);
                padded[padded.Length - 2] = 0xFF;
                padded[padded.Length - 1] = 0xD9; // EOC marker

                var settings = new ImageMagick.MagickReadSettings { Format = ImageMagick.MagickFormat.J2c };
                using var image = new ImageMagick.MagickImage(padded, settings);
                
                int width = (int)image.Width;
                int height = (int)image.Height;
                bool isDegraded = true; // Always treat padded recovery as degraded so we can try to get the real file later

                if (image.HasAlpha) image.ColorSpace = ImageMagick.ColorSpace.Transparent;
                else image.ColorSpace = ImageMagick.ColorSpace.sRGB;

                byte[] rgba = Array.Empty<byte>();

                using (var pixels = image.GetPixels())
                {
                    var raw = pixels.GetValues() ?? Array.Empty<byte>();
                    if (image.ChannelCount == 4)
                    {
                        rgba = new byte[width * height * 4];
                        for (int i = 0; i < raw.Length; i += 4)
                        {
                            rgba[i] = raw[i];
                            rgba[i + 1] = raw[i + 1];
                            rgba[i + 2] = raw[i + 2];
                            rgba[i + 3] = raw[i + 3];
                        }
                    }
                    else if (image.ChannelCount == 3)
                    {
                        rgba = new byte[width * height * 4];
                        for (int i = 0, j = 0; i < raw.Length; i += 3, j += 4)
                        {
                            rgba[j] = raw[i];
                            rgba[j + 1] = raw[i + 1];
                            rgba[j + 2] = raw[i + 2];
                            rgba[j + 3] = 255;
                        }
                    }
                }

                if (rgba.Length > 0)
                {
                    Console.WriteLine($"[AssetService] Padded Magick.NET decode successful: {width}x{height}");
                    return new TextureData(width, height, rgba, isDegraded);
                }
            }
            catch (Exception paddedEx)
            {
                Console.WriteLine($"[AssetService] Padded Magick.NET decode also failed: {paddedEx.Message}");
            }
            return null;
        }
    }
}
