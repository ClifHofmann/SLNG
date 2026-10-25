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

    /// <summary>One line per rigged item and face set: what every face record says, and what the rig made of
    /// each face in the loaded LOD -- its own surface, MERGED into the surface of the identical face before it
    /// (BUG-RENDER-12's run merge: drawn, in authored order, with that face's material), HIDDEN (draws nothing:
    /// TextureEntry alpha 0, the transparent texture, or its glTF material), or absent from the LOD.</summary>
    private void LogWornFaces(AvatarVisual visual, Guid entityId, Guid meshId, MeshData meshData, FaceTexture[]? faces,
        FaceTexture defaultFace)
    {
        if (!Diagnostics.Enabled) return;
        // RiggedMeshBuilder.Build's run walk, recording instead of building.
        var state = new Dictionary<int, string>();
        int runFirst = -1;
        FaceTexture runFace = default;
        foreach (var sub in meshData.Submeshes)
        {
            if (sub.Indices.Length == 0 || sub.Weights == null || state.ContainsKey(sub.FaceIndex)) continue;
            var face = ResolveFaceTexture(faces, defaultFace, sub.FaceIndex);
            if (FaceDrawsNothing(face))
            {
                state[sub.FaceIndex] = "HIDDEN";
                runFirst = -1;
                continue;
            }
            if (runFirst < 0 || !face.Equals(runFace))
            {
                runFirst = sub.FaceIndex;
                runFace = face;
                state[sub.FaceIndex] = "surface";
            }
            else
            {
                state[sub.FaceIndex] = $"merged->{runFirst}";
            }
        }
        int count = Math.Min(8, Math.Max(faces?.Length ?? 0, 1 + (state.Count > 0 ? MaxKey(state) : 0)));
        var parts = new List<string>(count);
        for (int f = 0; f < count; f++)
        {
            var face = ResolveFaceTexture(faces, defaultFace, f);
            parts.Add($"{f}:{Short(face.TextureId)} a={face.Color.W.ToString("0.###", CultureInfo.InvariantCulture)}" +
                      (face.LegacyMaterialId != Guid.Empty ? $" mat={Short(face.LegacyMaterialId)}" : "") +
                      (face.RenderMaterialId != Guid.Empty ? $" pbr={Short(face.RenderMaterialId)}" : "") +
                      $" {state.GetValueOrDefault(f, "not-in-LOD")}");
        }
        GD.Print($"[WornFaces] wearer={visual.AgentId.ToString("N")[..8]} item={entityId.ToString("N")[..8]} mesh={Short(meshId)} | " +
                 string.Join(" | ", parts));

        static string Short(Guid id) => id == Guid.Empty ? "-" : id.ToString("N")[..8];
        static int MaxKey(Dictionary<int, string> d) { int m = 0; foreach (int k in d.Keys) m = Math.Max(m, k); return m; }
    }

    private static readonly HashSet<Guid> _legacyMaterialLogged = new();

    /// <summary>A worn face's legacy material in full, once per material (--diag): the fields the viewer reads
    /// from the RenderMaterials LLSD (llmaterial.cpp: DiffuseAlphaMode, AlphaMaskCutoff, the maps and the
    /// specular scalars), with the alpha mode as the raw number the wire carries.</summary>
    private static void LogLegacyMaterial(LegacyMaterialData m)
    {
        if (!Diagnostics.Enabled) return;
        lock (_legacyMaterialLogged)
        {
            if (!_legacyMaterialLogged.Add(m.Id)) return;
        }
        static string Short(Guid id) => id == Guid.Empty ? "none" : id.ToString("N")[..8];
        GD.Print($"[LegacyMaterial] worn {Short(m.Id)} DiffuseAlphaMode={(int)m.DiffuseAlphaMode} ({m.DiffuseAlphaMode}) " +
                 $"AlphaMaskCutoff={m.AlphaMaskCutoff} -> discard below {LegacyMaterialAlpha.MaskThreshold(m.AlphaMaskCutoff).ToString("0.####", CultureInfo.InvariantCulture)} if MASK | " +
                 $"normal={Short(m.NormalMap)} specular={Short(m.SpecularMap)} specColor=({m.SpecularColor.X.ToString("0.##", CultureInfo.InvariantCulture)}," +
                 $"{m.SpecularColor.Y.ToString("0.##", CultureInfo.InvariantCulture)},{m.SpecularColor.Z.ToString("0.##", CultureInfo.InvariantCulture)}," +
                 $"{m.SpecularColor.W.ToString("0.##", CultureInfo.InvariantCulture)}) SpecExp={m.SpecularExponent} EnvIntensity={m.EnvironmentIntensity}");
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
        // The alpha map captured when the bake was decoded (BakeAlphaMaps); the full decoded bake only if that is
        // missing and it is still in the asset cache.
        byte[] data;
        int width, height;
        bool fromMap;
        if (BakeAlphaMaps.TryGet(facts.Texture, out var map))
        {
            (data, width, height, fromMap) = (map.Alpha, map.Width, map.Height, true);
        }
        else if (_assetService.TryGetCachedTexture(facts.Texture, out var bake))
        {
            (data, width, height, fromMap) = (bake.Rgba, bake.Width, bake.Height, false);
        }
        else
        {
            GD.Print($"[BomAlpha] mesh={meshId.ToString("N")[..8]} face={faceIndex} bake={facts.Texture.ToString("N")[..8]} " +
                     $"ch={facts.BakeChannel}: no alpha map of this bake yet -- measured when the face is next built");
            lock (_bomAlphaLogged) _bomAlphaLogged.Remove(key);
            return;
        }
        var uvs = System.Runtime.InteropServices.MemoryMarshal.Cast<Godot.Vector2, System.Numerics.Vector2>(
            arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array());
        var index = arrays[(int)Mesh.ArrayType.Index];
        var indices = index.VariantType == Variant.Type.Nil ? Array.Empty<int>() : index.AsInt32Array();
        var scale = sm.GetShaderParameter(PrimShaderFamily.UvScale).AsVector2();
        var offset = sm.GetShaderParameter(PrimShaderFamily.UvOffset).AsVector2();
        float threshold = facts.Kind == PrimShaderFamily.Kind.Scissor ? facts.Threshold : 0.004f;
        var placeScale = new System.Numerics.Vector2(scale.X, scale.Y);
        var placeOffset = new System.Numerics.Vector2(offset.X, offset.Y);
        var coverage = fromMap
            ? BomAlphaProbe.MeasureAlpha(data, width, height, uvs, indices, placeScale, placeOffset, threshold)
            : BomAlphaProbe.Measure(data, width, height, uvs, indices, placeScale, placeOffset, threshold);
        var whole = GpuCache.TryGetAlphaStats(facts.Texture, out var stats)
            ? $" whole bake minA={stats.MinAlpha} mid={stats.FracMid.ToString("0.###", CultureInfo.InvariantCulture)} clear={stats.FracClear.ToString("0.###", CultureInfo.InvariantCulture)}"
            : "";
        GD.Print($"[BomAlpha] wearer={visual.AgentId.ToString("N")[..8]} mesh={meshId.ToString("N")[..8]} face={faceIndex} " +
                 $"bake={facts.Texture.ToString("N")[..8]} ch={facts.BakeChannel} {(fromMap ? "alpha map " : "")}{width}x{height} " +
                 $"{facts.Kind}@{threshold.ToString("0.###", CultureInfo.InvariantCulture)}: " +
                 $"{coverage.Samples} samples, {(coverage.CutFraction * 100f).ToString("0.#", CultureInfo.InvariantCulture)}% cut, " +
                 $"minA={coverage.MinAlpha} meanA={coverage.MeanAlpha.ToString("0.#", CultureInfo.InvariantCulture)}{whole}");
    }
}
