using System;
using System.Collections.Generic;
using System.Text;
using Godot;
using SLNG.Core;
using SLNG.Core.Components;

namespace SLNG.App;

/// <summary>
/// BUG-RENDER-53 diagnostic (--diag only): finds prim faces that SHOULD be textured and are not.
/// A white face has no signature in the log -- the build logged a texture, the upload logged a verdict, and
/// the face is still plain white -- so this looks at what the surface is actually wearing: a legacy face with a
/// texture id whose texture is known to have decoded and uploaded, but whose material has no albedo texture
/// bound. Run on both sides of <c>--share-materials</c> it says which surface, which texture, which entry and
/// how it is drawn (own node, split child or instance group), without anyone having to select the object.
/// </summary>
public partial class ObjectRenderer
{
    private const double MaterialAuditIntervalSeconds = 10.0;
    private const double MaterialAuditSettleSeconds = 20.0;   // a texture may still be on its way before this

    private double _auditTimer = MaterialAuditIntervalSeconds;
    private readonly Dictionary<(Guid, int), double> _auditBareSince = new();

    private void TickMaterialAudit(double delta)
    {
        if (!Diagnostics.Enabled || _world == null || !_agentPosKnown) return;
        _auditTimer -= delta;
        if (_auditTimer > 0) return;
        _auditTimer = MaterialAuditIntervalSeconds;

        double now = Time.GetTicksMsec() / 1000.0;
        int surfacesChecked = 0, textured = 0, bare = 0, bareLoaded = 0, bareSettled = 0;
        var seenNow = new HashSet<(Guid, int)>();
        var detail = new StringBuilder();
        int detailLines = 0;

        foreach (var (id, state) in _visuals)
        {
            if (state.ResourcesReleased || state.PendingRemoval || !IsInstanceValid(state.MeshInstance) || !state.Shown) continue;
            var entity = _world.GetEntity(id);
            var prim = entity?.GetComponent<PrimitiveComponent>();
            if (entity == null || prim == null || state.LoadedMeshKey == Guid.Empty) continue;
            if (!_meshFaceIndices.TryGetValue(state.LoadedMeshKey, out var faceIndices) || faceIndices.Length == 0) continue;

            var group = _instanceGroups?.GroupFor(id);
            var defaultFace = new FaceTexture(prim.TextureId, prim.RenderMaterialId, prim.LegacyMaterialId, prim.ColorTint,
                prim.RepeatU, prim.RepeatV, prim.OffsetU, prim.OffsetV, prim.Rotation, prim.TexGen, prim.Fullbright);

            for (int i = 0; i < faceIndices.Length; i++)
            {
                int faceIdx = faceIndices[i];
                var ft = prim.Faces != null && faceIdx >= 0 && faceIdx < prim.Faces.Length ? prim.Faces[faceIdx] : defaultFace;
                if (ft.TextureId == Guid.Empty || ft.TextureId == FaceTexture.TransparentTextureId || ft.RenderMaterialId != Guid.Empty) continue;

                // An instanced member's own node has no mesh; the group draws it with one material for all.
                var mat = group != null ? group.SharedMaterial as ShaderMaterial : SurfaceMaterial(state, i);
                if (mat == null) continue;
                surfacesChecked++;

                bool hasTexture = mat.GetShaderParameter(PrimShaderFamily.HasAlbedoTexture) is { VariantType: Variant.Type.Bool } has && has.AsBool()
                                  && mat.GetShaderParameter(PrimShaderFamily.AlbedoTexture).Obj is GodotObject tex && IsInstanceValid(tex);
                if (hasTexture) { textured++; continue; }

                bare++;
                var slot = (id, i);
                seenNow.Add(slot);
                if (!_auditBareSince.TryGetValue(slot, out double since)) { _auditBareSince[slot] = now; since = now; }

                // Known to have decoded and uploaded at some point: the texture is not the reason it is bare.
                if (!GpuCache.TryGetAlphaMode(ft.TextureId, out _)) continue;
                bareLoaded++;
                if (now - since < MaterialAuditSettleSeconds) continue;
                bareSettled++;
                if (detailLines++ >= 8) continue;

                string entry = "-";
                if (state.SurfaceMaterialKeys != null && i < state.SurfaceMaterialKeys.Length
                    && _materialCache.TryGet(state.SurfaceMaterialKeys[i], out var e))
                    entry = $"refs={e.RefCount} entryMat={(ReferenceEquals(e.Material, mat) ? "same" : "OTHER")}";
                bool detached = state.SurfaceDetached != null && i < state.SurfaceDetached.Length && state.SurfaceDetached[i] != 0;
                var tr = entity.GetComponent<TransformComponent>();
                detail.Append(" | local=").Append(entity.LocalId)
                    .Append(" at <").Append(tr != null ? $"{tr.Position.X:0},{tr.Position.Y:0},{tr.Position.Z:0}" : "?").Append('>')
                    .Append(" surf=").Append(i).Append(" tex=").Append(ft.TextureId.ToString()[..8])
                    .Append(' ').Append(PrimShaderKindName(mat.Shader))
                    .Append(" mat=").Append(mat.GetInstanceId().ToString("X"))
                    .Append(" has=").Append(mat.GetShaderParameter(PrimShaderFamily.HasAlbedoTexture).ToString())
                    .Append(group != null ? " instanced" : "")
                    .Append(state.SplitChildren != null ? " split" : "")
                    .Append(detached ? " detached" : "")
                    .Append(' ').Append(entry)
                    .Append(" for ").Append((now - since).ToString("0")).Append("s");
            }
        }

        // Forget slots that are no longer bare, so a later bare spell starts its own clock.
        if (_auditBareSince.Count > 0)
        {
            var gone = new List<(Guid, int)>();
            foreach (var slot in _auditBareSince.Keys) if (!seenNow.Contains(slot)) gone.Add(slot);
            foreach (var slot in gone) _auditBareSince.Remove(slot);
        }

        GD.Print($"[MatAudit] share={(RenderConfig.ShareMaterials ? "on" : "off")} legacyTexturedSurfaces={surfacesChecked} " +
                 $"withTexture={textured} bare={bare} bareButTextureLoaded={bareLoaded} bareFor{MaterialAuditSettleSeconds:0}s+={bareSettled}{detail}");
    }
}
