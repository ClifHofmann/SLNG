using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Godot;
using SLNG.Core;
using SLNG.Core.Components;

namespace SLNG.App;

/// <summary>
/// BUG-RENDER-49: a glTF material that arrives after a face was built without it.
///
/// <para>A face whose material cannot be had is built as its legacy self, and with
/// <see cref="RenderConfig.ShareMaterials"/> that legacy material is shared under the face's
/// <see cref="PrimMaterialKey"/> -- the very key the PBR material belongs under. When the material does
/// arrive later (a fetch that failed once and succeeds on a later face), the stale entries are RETIRED:
/// moved to a unique key no new face can produce, with every face that holds them rewritten to the
/// retired key so its release still finds the entry. Then every object naming the material builds its
/// materials again and gets the PBR one.</para>
///
/// <para>Only materials some face was actually built WITHOUT are acted on
/// (<see cref="_pbrBuiltWithout"/>): <see cref="SLNG.Assets.AssetService.MaterialLoaded"/> fires for
/// every material, and the common first-time load reaches the faces through the build's own await.</para>
/// </summary>
public partial class ObjectRenderer
{
    private readonly ConcurrentDictionary<Guid, byte> _pbrBuiltWithout = new();

    /// <summary>A key variant no face can produce (the renderer's own variants are small numbers).</summary>
    private const ulong RetiredVariant = 1UL << 63;

    private static long _retiredKeys;

    /// <summary>A face naming <paramref name="materialId"/> was built while the material was not to be had.</summary>
    private void NotePbrBuiltWithout(Guid materialId) => _pbrBuiltWithout.TryAdd(materialId, 0);

    internal bool IsPbrBuiltWithout(Guid materialId) => _pbrBuiltWithout.ContainsKey(materialId);

    /// <summary><see cref="SLNG.Assets.AssetService.MaterialLoaded"/>, on a worker thread.</summary>
    private void OnPbrMaterialLoaded(Guid materialId)
    {
        if (!_pbrBuiltWithout.TryRemove(materialId, out _)) return;
        MainThreadWorkQueue.Enqueue(MainThreadWorkQueue.Lane.Visual, () => RefreshPbrMaterial(materialId), label: "prim.pbr_arrived");
    }

    /// <summary>Retires the stale shared materials and builds again every object whose faces name
    /// <paramref name="materialId"/>. Main thread. Returns how many objects were re-applied.</summary>
    internal int RefreshPbrMaterial(Guid materialId)
    {
        RetireSharedPbrMaterials(materialId);
        if (_world == null) return 0;

        var stale = new List<VisualState>();
        foreach (var state in _visuals.Values)
        {
            if (state.PendingRemoval || state.LoadedMeshKey == Guid.Empty) continue;
            var prim = _world.GetEntity(state.EntityId)?.GetComponent<PrimitiveComponent>();
            if (prim != null && NamesRenderMaterial(prim.RenderMaterialId, prim.Faces, materialId)) stale.Add(state);
        }
        foreach (var state in stale) _ = ApplyFaceMaterialsAsync(state);
        if (stale.Count > 0)
            GD.Print($"[PbrMaterial] {materialId.ToString()[..8]} arrived after {stale.Count} object(s) were built without it -- rebuilding their materials");
        return stale.Count;
    }

    /// <summary>Moves every finished shared material whose face names <paramref name="materialId"/> to a
    /// retired key and rewrites the faces that hold it. Returns how many entries were retired.</summary>
    internal int RetireSharedPbrMaterials(Guid materialId)
    {
        if (!RenderConfig.ShareMaterials) return 0;

        Dictionary<PrimMaterialKey, PrimMaterialKey>? retired = null;
        foreach (var key in _materialCache.Keys)
        {
            if (key.Face.RenderMaterialId != materialId || (key.Variant & RetiredVariant) != 0) continue;
            ulong unique = RetiredVariant | (ulong)System.Threading.Interlocked.Increment(ref _retiredKeys);
            var retiredKey = PrimMaterialKey.Create(key.Face, key.PlanarScale, unique, key.TexAnimA, key.TexAnimB);
            if (_materialCache.Retire(key, retiredKey)) (retired ??= new())[key] = retiredKey;
        }
        if (retired == null) return 0;

        foreach (var state in _visuals.Values)
        {
            if (state.SurfaceMaterialKeys is not { } keys) continue;
            for (int i = 0; i < keys.Length; i++)
            {
                if (retired.TryGetValue(keys[i], out var retiredKey)) keys[i] = retiredKey;
            }
        }
        return retired.Count;
    }

    internal static bool NamesRenderMaterial(Guid defaultMaterial, FaceTexture[]? faces, Guid materialId)
    {
        if (defaultMaterial == materialId) return true;
        if (faces == null) return false;
        foreach (var face in faces)
        {
            if (face.RenderMaterialId == materialId) return true;
        }
        return false;
    }
}
