using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using SLNG.Assets;
using SLNG.Core;

namespace SLNG.App;

/// <summary>
/// BUG-RENDER-50 follow-up diagnostics (--diag only) for "this worn face is drawn here but cut away in Firestorm":
/// <list type="bullet">
/// <item><c>[WornFaces]</c>: every face record of a rigged item as it was rigged -- texture, TextureEntry alpha,
/// legacy and glTF material -- including the faces that draw nothing, so a viewer-side comparison can tell a
/// face SLNG shows from one the sim told it to hide.</item>
/// <item><c>[BomAlpha]</c>: for a face sampling a Bakes-on-Mesh bake, how much of the face the bake's alpha (the
/// wearer's alpha layers, composited server-side) cuts away at the face's own cutoff, measured at the face's
/// own UVs on the decoded bake. Numbers only: on a Linden grid another resident's bake is never written out as
/// an image (TPV §2.b, FEAT-SL-01).</item>
/// </list>
/// </summary>
public partial class AvatarRenderer
{
    private static readonly HashSet<string> _bomAlphaLogged = new();

    /// <summary>One line per rigged item and face set: what every face record says.</summary>
    private static void LogWornFaces(AvatarVisual visual, Guid entityId, Guid meshId, FaceTexture[]? faces, FaceTexture defaultFace,
        int[] drawnFaces)
    {
        if (!Diagnostics.Enabled) return;
        int count = Math.Max(faces?.Length ?? 0, 1);
        var parts = new List<string>(count);
        for (int f = 0; f < count && f < 8; f++)
        {
            var face = ResolveFaceTexture(faces, defaultFace, f);
            bool drawn = Array.IndexOf(drawnFaces, f) >= 0;
            parts.Add($"{f}:{Short(face.TextureId)} a={face.Color.W.ToString("0.###", CultureInfo.InvariantCulture)}" +
                      (face.LegacyMaterialId != Guid.Empty ? $" mat={Short(face.LegacyMaterialId)}" : "") +
                      (face.RenderMaterialId != Guid.Empty ? $" pbr={Short(face.RenderMaterialId)}" : "") +
                      (drawn ? "" : " NOT-DRAWN"));
        }
        GD.Print($"[WornFaces] wearer={visual.AgentId.ToString("N")[..8]} item={entityId.ToString("N")[..8]} mesh={Short(meshId)} | " +
                 string.Join(" | ", parts));

        static string Short(Guid id) => id == Guid.Empty ? "-" : id.ToString("N")[..8];
    }

    /// <summary>Measures a Bakes-on-Mesh face against its bake (see the type's summary). Main thread; reads only
    /// managed copies (the surface's cached arrays, the asset service's decoded bake) -- never the GPU.</summary>
    private void LogBomAlpha(MeshInstance3D mi, int surface, Material material, AvatarVisual? visual, Guid meshId, int faceIndex)
    {
        if (!Diagnostics.Enabled || visual == null || _assetService == null || material is not ShaderMaterial sm) return;
        var facts = FactsOf(sm);
        if (facts.BakeChannel < 0 || facts.Texture == Guid.Empty || facts.Hidden || facts.Failed) return;
        string key = $"{meshId:N}:{faceIndex}:{facts.Texture:N}:{facts.Kind}";
        lock (_bomAlphaLogged)
        {
            if (!_bomAlphaLogged.Add(key)) return;
        }
        if (mi.Mesh is not ArrayMesh mesh || !MeshSurfaceCache.TryGetSurface(mesh, surface, out var arrays)) return;
        if (!_assetService.TryGetCachedTexture(facts.Texture, out var bake))
        {
            GD.Print($"[BomAlpha] mesh={meshId.ToString("N")[..8]} face={faceIndex} bake={facts.Texture.ToString("N")[..8]} " +
                     $"ch={facts.BakeChannel}: the decoded bake is no longer in memory -- not measured");
            return;
        }
        var uvs = System.Runtime.InteropServices.MemoryMarshal.Cast<Godot.Vector2, System.Numerics.Vector2>(
            arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array());
        var index = arrays[(int)Mesh.ArrayType.Index];
        var indices = index.VariantType == Variant.Type.Nil ? Array.Empty<int>() : index.AsInt32Array();
        var scale = sm.GetShaderParameter(PrimShaderFamily.UvScale).AsVector2();
        var offset = sm.GetShaderParameter(PrimShaderFamily.UvOffset).AsVector2();
        float threshold = facts.Kind == PrimShaderFamily.Kind.Scissor ? facts.Threshold : 0.004f;
        var coverage = BomAlphaProbe.Measure(bake.Rgba, bake.Width, bake.Height, uvs, indices,
            new System.Numerics.Vector2(scale.X, scale.Y), new System.Numerics.Vector2(offset.X, offset.Y), threshold);
        var whole = GpuCache.TryGetAlphaStats(facts.Texture, out var stats)
            ? $" whole bake minA={stats.MinAlpha} mid={stats.FracMid.ToString("0.###", CultureInfo.InvariantCulture)} clear={stats.FracClear.ToString("0.###", CultureInfo.InvariantCulture)}"
            : "";
        GD.Print($"[BomAlpha] wearer={visual.AgentId.ToString("N")[..8]} mesh={meshId.ToString("N")[..8]} face={faceIndex} " +
                 $"bake={facts.Texture.ToString("N")[..8]} ch={facts.BakeChannel} {bake.Width}x{bake.Height} " +
                 $"{facts.Kind}@{threshold.ToString("0.###", CultureInfo.InvariantCulture)}: " +
                 $"{coverage.Samples} samples, {(coverage.CutFraction * 100f).ToString("0.#", CultureInfo.InvariantCulture)}% cut, " +
                 $"minA={coverage.MinAlpha} meanA={coverage.MeanAlpha.ToString("0.#", CultureInfo.InvariantCulture)}{whole}");
    }
}
