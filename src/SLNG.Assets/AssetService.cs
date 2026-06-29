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
                var map = await GetTextureAsync(id).ConfigureAwait(false);
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

            submeshes.Add(new MeshSubmesh(positions, normals, uvs, indices));
        }

        return submeshes.Count == 0 ? null : new MeshData(submeshes);
    }

    /// <summary>
    /// Fetches and decodes a texture (JPEG2000) by UUID into engine-neutral RGBA, or null
    /// if it cannot be decoded. Concurrent requests for the same id share one fetch/decode.
    /// </summary>
    public Task<TextureData?> GetTextureAsync(Guid textureId)
    {
        if (_memCache.TryGetValue(textureId, out TextureData? cached))
        {
            return Task.FromResult(cached);
        }
        return _inflightTextures.GetOrAdd(textureId, async id => {
            try {
                var result = await FetchAndDecodeTextureAsync(id).ConfigureAwait(false);
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

    private async Task<TextureData?> FetchAndDecodeTextureAsync(Guid textureId)
    {
        string? cacheFile = string.IsNullOrEmpty(_cacheDir) ? null : System.IO.Path.Combine(_cacheDir, textureId.ToString() + ".j2c");

        // 1. Try the on-disk bytes. If they decode, great; if not, the cache entry is
        //    poisoned (a partial download from an earlier run) — drop it and re-fetch.
        if (cacheFile != null && File.Exists(cacheFile))
        {
            byte[]? cached = null;
            try { cached = await File.ReadAllBytesAsync(cacheFile).ConfigureAwait(false); } catch { }
            if (cached != null && cached.Length > 0)
            {
                var decodedFromCache = await Task.Run(() => DecodeTexture(cached)).ConfigureAwait(false);
                if (decodedFromCache != null) return decodedFromCache;
                try { File.Delete(cacheFile); } catch { }
            }
        }

        // 2. Fetch fresh and decode, with a few retries. On a busy region the first transfer
        //    can come back truncated; a retry once congestion eases usually succeeds. We only
        //    persist bytes that actually decode, so the cache is never poisoned.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (!_session.IsConnected) return null;

            var bytes = await _session.FetchTextureDataAsync(textureId).ConfigureAwait(false);
            if (bytes is { Length: > 0 })
            {
                var result = await Task.Run(() => DecodeTexture(bytes)).ConfigureAwait(false);
                if (result != null)
                {
                    if (cacheFile != null)
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

    private static TextureData? DecodeTexture(byte[] bytes)
    {
        try
        {
            // First try the native SL decoder which correctly handles SL alpha channels!
            var asset = new LibreMetaverse.Assets.AssetTexture(new LibreMetaverse.UUID(), bytes);
            if (asset.Decode())
            {
                if (asset.Image != null)
                {
                    int width = asset.Image.Width;
                    int height = asset.Image.Height;
                    
                    byte[] rgba = new byte[width * height * 4];
                    bool hasAlpha = asset.Image.Alpha != null && asset.Image.Alpha.Length == width * height;
                    bool hasColor = asset.Image.Red != null && asset.Image.Green != null && asset.Image.Blue != null;
                    
                    if (hasColor)
                    {
                        var red = asset.Image.Red!;
                        var green = asset.Image.Green!;
                        var blue = asset.Image.Blue!;
                        var alpha = asset.Image.Alpha;

                        for (int i = 0; i < width * height; i++)
                        {
                            rgba[i * 4] = red[i];
                            rgba[i * 4 + 1] = green[i];
                            rgba[i * 4 + 2] = blue[i];
                            rgba[i * 4 + 3] = hasAlpha ? alpha![i] : (byte)255;
                        }
                        
                        Console.WriteLine($"[AssetService] AssetTexture decoded manually: {width}x{height}, HasAlpha: {hasAlpha}");
                        return new TextureData(width, height, rgba);
                    }
                }
            }
        }
        catch { }

        try
        {
            // Magick.NET wraps OpenJPEG and seamlessly handles malformed J2C 
            // bitstreams (missing EOC, trailing padding, etc.) that crash CoreJ2K.
            using var image = new ImageMagick.MagickImage(bytes);
            
            Console.WriteLine($"[AssetService] Magick fallback decoded image: {image.Width}x{image.Height}, Channels: {image.ChannelCount}, ColorSpace: {image.ColorSpace}, HasAlpha: {image.HasAlpha}");
            
            // SL textures often have 4 channels for RGBA but don't explicitly mark themselves
            // as having an alpha channel in the J2K header.
            if (image.ChannelCount >= 4)
            {
                image.HasAlpha = true;
            }

            // In Magick.NET, ColorSpace.Transparent is required to properly export the alpha
            // channel when mapping to RGBA, otherwise it may be flattened.
            if (image.HasAlpha)
            {
                image.ColorSpace = ImageMagick.ColorSpace.Transparent;
            }
            else
            {
                image.ColorSpace = ImageMagick.ColorSpace.sRGB;
            }

            int width = (int)image.Width;
            int height = (int)image.Height;

            byte[] rgba;
            
            using (var pixels = image.GetPixels())
            {
                if (image.ChannelCount == 4)
                {
                    // For 4-channel SL textures, Magick.NET's ToByteArray("RGBA") mapping often
                    // overwrites the unmapped 4th channel with opaque 255.
                    // Using GetValues() retrieves the raw interleaved bytes (R, G, B, A) directly
                    // as decoded by OpenJPEG, preserving the alpha channel.
                    rgba = pixels.GetValues() ?? Array.Empty<byte>();
                }
                else
                {
                    // For 3-channel images, use ToByteArray("RGBA") to safely pad the 4th byte with 255
                    rgba = pixels.ToByteArray("RGBA") ?? Array.Empty<byte>();
                }
            }

            return new TextureData(width, height, rgba);
        }
        catch (Exception ex)
        {
            // Return null (not a magenta placeholder): null is not cached, so the texture is
            // re-fetched/re-decoded next time instead of being locked to a fallback, and the
            // caller can drop a poisoned disk-cache entry. The surface keeps its base colour.
            Console.WriteLine($"[AssetService] Magick.NET failed to decode texture: {ex.Message}");
            return null;
        }
    }
}
