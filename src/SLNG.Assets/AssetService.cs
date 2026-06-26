using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using CoreJ2K;
using LibreMetaverse;
using LibreMetaverse.Assets;
using LibreMetaverse.Rendering;
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
    private readonly ConcurrentDictionary<Guid, Task<MeshData?>> _meshCache = new();
    private readonly ConcurrentDictionary<Guid, Task<TextureData?>> _textureCache = new();

    public AssetService(GridSession session)
    {
        _session = session;
    }

    /// <summary>
    /// Fetches and decodes a mesh by UUID, or null if it cannot be decoded. Concurrent
    /// requests for the same id share a single fetch/decode.
    /// </summary>
    public Task<MeshData?> GetMeshAsync(Guid meshId)
    {
        return _meshCache.GetOrAdd(meshId, FetchAndDecodeMeshAsync);
    }

    private async Task<MeshData?> FetchAndDecodeMeshAsync(Guid meshId)
    {
        if (!_session.IsConnected)
        {
            return null;
        }

        try
        {
            byte[]? bytes = await _session.FetchMeshDataAsync(meshId).ConfigureAwait(false);
            if (bytes == null || bytes.Length == 0)
            {
                return null;
            }

            // Decode off the render thread; never block the main thread.
            return await Task.Run(() => Decode(meshId, bytes)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AssetService] Failed to fetch/decode mesh {meshId}: {ex.Message}");
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
        return _textureCache.GetOrAdd(textureId, FetchAndDecodeTextureAsync);
    }

    private async Task<TextureData?> FetchAndDecodeTextureAsync(Guid textureId)
    {
        if (!_session.IsConnected)
        {
            return null;
        }

        try
        {
            byte[]? bytes = await _session.FetchTextureDataAsync(textureId).ConfigureAwait(false);
            if (bytes == null || bytes.Length == 0)
            {
                return null;
            }

            // Decode off the render thread; never block the main thread.
            return await Task.Run(() => DecodeTexture(bytes)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AssetService] Failed to fetch/decode texture {textureId}: {ex.ToString()}");
            return null;
        }
    }

    private readonly ConcurrentDictionary<Guid, Task<PbrMaterialData?>> _materialCache = new();

    /// <summary>
    /// Fetches a GLTF PBR material by UUID and returns its mapped parameters and texture UUIDs.
    /// </summary>
    public Task<PbrMaterialData?> GetMaterialAsync(Guid materialId)
    {
        return _materialCache.GetOrAdd(materialId, FetchMaterialAsync);
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

    private static TextureData? DecodeTexture(byte[] bytes)
    {
        try
        {
            // Magick.NET wraps OpenJPEG and seamlessly handles malformed J2C 
            // bitstreams (missing EOC, trailing padding, etc.) that crash CoreJ2K.
            using var image = new ImageMagick.MagickImage(bytes);
            
            // Ensure we have RGBA output
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

            // GetPixels returns an IPixelCollection which can be dumped to a byte array.
            // MagickImage automatically handles conversion to 8-bit RGBA if requested.
            // But usually we just get the raw values.
            byte[] rgba;
            
            using (var pixels = image.GetPixels())
            {
                // ToByteArray returns pixels based on the channel mapping.
                // We map exactly to "RGBA" (1 byte per channel)
                rgba = pixels.ToByteArray("RGBA") ?? Array.Empty<byte>();
            }

            return new TextureData(width, height, rgba);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AssetService] Magick.NET failed to decode texture: {ex.Message}");
            return CreateFallbackTexture();
        }
    }

    private static TextureData CreateFallbackTexture()
    {
        // 8x8 bright magenta/black checkerboard texture so it's blatantly obvious
        int size = 8;
        var rgba = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int o = (y * size + x) * 4;
                bool isMagenta = ((x / 2) + (y / 2)) % 2 == 0;
                rgba[o] = isMagenta ? (byte)255 : (byte)0;
                rgba[o + 1] = 0;
                rgba[o + 2] = isMagenta ? (byte)255 : (byte)0;
                rgba[o + 3] = 255;
            }
        }
        return new TextureData(size, size, rgba);
    }
}
