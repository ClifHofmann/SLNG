using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using LibreMetaverse;
using LibreMetaverse.Rendering;
using SLNG.Net;

namespace SLNG.Assets;

/// <summary>
/// Handles downloading and decoding of assets (LLMesh, etc.) using LibreMetaverse.
/// Maintains an in-memory cache of decoded meshes.
/// </summary>
public class AssetService
{
    private readonly GridSession _session;
    private readonly ConcurrentDictionary<Guid, Task<FacetedMesh?>> _meshCache = new();

    public AssetService(GridSession session)
    {
        _session = session;
    }

    /// <summary>
    /// Fetches and decodes a mesh by its UUID. Returns null if decoding fails.
    /// Uses an internal cache to avoid redundant downloads.
    /// </summary>
    public Task<FacetedMesh?> GetMeshAsync(Guid meshId)
    {
        // GetOrAdd ensures we only start the fetch/decode once per mesh ID.
        return _meshCache.GetOrAdd(meshId, FetchAndDecodeMeshAsync);
    }

    private async Task<FacetedMesh?> FetchAndDecodeMeshAsync(Guid meshId)
    {
        if (!_session.IsConnected) return null;

        try
        {
            // Fetch the binary AssetMesh from the simulator
            var assetMesh = await _session.FetchMeshAsync(meshId).ConfigureAwait(false);
            
            if (assetMesh == null || assetMesh.AssetData == null || assetMesh.AssetData.Length == 0)
                return null;

            // Decode it on a background thread to prevent blocking the main/render thread
            return await Task.Run(() =>
            {
                var dummyPrim = new Primitive();
                if (FacetedMesh.TryDecodeFromAsset(dummyPrim, assetMesh, DetailLevel.Highest, out var facetedMesh))
                {
                    return facetedMesh;
                }
                return null;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AssetService] Failed to fetch/decode mesh {meshId}: {ex.Message}");
            return null;
        }
    }
}
