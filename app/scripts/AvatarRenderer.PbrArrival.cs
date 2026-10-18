using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Godot;
using SLNG.Core;
using SLNG.Core.Components;

namespace SLNG.App;

/// <summary>
/// BUG-RENDER-49: a glTF material that arrives after an avatar face was built without it -- worn items
/// (rigged, static, HUD) and animesh parts.
///
/// <para>Two things are decided without the material when it is not to be had: the face's material
/// (its legacy self) and, for a rigged mesh, whether the face has triangles at all
/// (<see cref="FaceDrawsNothing"/>: a TE-invisible face is left out). When the material turns up, the
/// item is built again from scratch -- the same path a face change takes -- so geometry, materials and
/// the outfit merge's facts and keys (FEAT-PERF-23) all follow from the material together.</para>
///
/// <para>Only materials some face was actually built WITHOUT are acted on: the first-time load of an
/// ordinary material reaches its faces through the build's own await (and the worn path's prefetch),
/// and costs no rebuild.</para>
/// </summary>
public partial class AvatarRenderer
{
    private readonly ConcurrentDictionary<Guid, byte> _pbrBuiltWithout = new();

    /// <summary>A face naming <paramref name="materialId"/> was decided while the material was not to be had.</summary>
    private void NotePbrBuiltWithout(Guid materialId) => _pbrBuiltWithout.TryAdd(materialId, 0);

    internal bool IsPbrBuiltWithout(Guid materialId) => _pbrBuiltWithout.ContainsKey(materialId);

    /// <summary><see cref="SLNG.Assets.AssetService.MaterialLoaded"/>, on a worker thread.</summary>
    private void OnPbrMaterialLoaded(Guid materialId)
    {
        if (!_pbrBuiltWithout.TryRemove(materialId, out _)) return;
        MainThreadWorkQueue.Enqueue(MainThreadWorkQueue.Lane.Visual, () => RebuildForPbrMaterial(materialId), label: "avatar.pbr_arrived");
    }

    /// <summary>Builds again every worn item and animesh part whose faces name
    /// <paramref name="materialId"/>. Main thread. Returns how many were asked for.</summary>
    internal int RebuildForPbrMaterial(Guid materialId)
    {
        var worn = WornItemsNaming(materialId);
        foreach (var entityId in worn)
        {
            // What UpdateAttachment's duplicate guards compare against goes first, or the repeat is swallowed
            // (same mesh, same faces) -- RerouteDroppedControlAvatar does the same.
            _attachmentMeshIds.Remove(entityId);
            _attachmentPrimSignatures.TryRemove(entityId, out _);
            _hudContent.Remove(entityId);
            UpdateAttachment(entityId.ToString());
        }

        int parts = 0;
        foreach (var (rootId, ca) in _controlAvatars)
        {
            foreach (var (primId, part) in ca.Parts)
            {
                if (worn.Contains(primId)) continue;
                if (!ObjectRenderer.NamesRenderMaterial(part.DefaultFace.RenderMaterialId, part.Faces, materialId)) continue;
                _pendingControlParts[primId] = new PendingControlPart(part.MeshData, part.MeshId, rootId, part.Faces, part.DefaultFace, part.Visible);
                Guid id = primId;
                MainThreadWorkQueue.Enqueue(MainThreadWorkQueue.Lane.Visual, () => BuildPendingControlPart(id),
                    coalesceKey: $"controlavatar.rig:{id}", label: "controlavatar.rig");
                parts++;
            }
        }

        if (worn.Count + parts > 0)
            GD.Print($"[PbrMaterial] {materialId.ToString()[..8]} arrived after {worn.Count} worn item(s) and {parts} animesh part(s) " +
                     "were built without it -- building them again");
        return worn.Count + parts;
    }

    /// <summary>The worn items (body and HUD) whose prim names <paramref name="materialId"/> on any face.</summary>
    internal HashSet<Guid> WornItemsNaming(Guid materialId)
    {
        var found = new HashSet<Guid>();
        if (_world == null) return found;
        void Consider(Guid entityId)
        {
            var prim = _world.GetEntity(entityId)?.GetComponent<PrimitiveComponent>();
            if (prim != null && ObjectRenderer.NamesRenderMaterial(prim.RenderMaterialId, prim.Faces, materialId)) found.Add(entityId);
        }
        foreach (var entityId in _attachmentToAvatar.Keys) Consider(entityId);
        foreach (var entityId in _hudContent.Keys) Consider(entityId);
        return found;
    }
}
