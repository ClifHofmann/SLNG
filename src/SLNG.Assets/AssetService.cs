using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using LibreMetaverse;
using LibreMetaverse.Assets;
using LibreMetaverse.Imaging;
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
            return await Task.Run(() => DecodeTexture(textureId, bytes)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AssetService] Failed to fetch/decode texture {textureId}: {ex.Message}");
            return null;
        }
    }

    private static TextureData? DecodeTexture(Guid textureId, byte[] bytes)
    {
        var texture = new AssetTexture(new UUID(textureId), bytes);
        if (!texture.Decode() || texture.Image is null)
        {
            return null;
        }

        var image = texture.Image;
        int width = image.Width;
        int height = image.Height;
        int pixelCount = width * height;
        if (pixelCount <= 0 || image.Red is null || image.Green is null || image.Blue is null)
        {
            return null;
        }

        bool hasAlpha = image.Alpha is not null && (image.Channels & ManagedImage.ImageChannels.Alpha) != 0;
        var rgba = new byte[pixelCount * 4];
        for (int i = 0; i < pixelCount; i++)
        {
            int o = i * 4;
            rgba[o] = image.Red[i];
            rgba[o + 1] = image.Green[i];
            rgba[o + 2] = image.Blue[i];
            rgba[o + 3] = hasAlpha ? image.Alpha![i] : (byte)255;
        }

        return new TextureData(width, height, rgba);
    }
}
