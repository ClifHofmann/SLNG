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
                      $" rgb=({face.Color.X.ToString("0.##", CultureInfo.InvariantCulture)},{face.Color.Y.ToString("0.##", CultureInfo.InvariantCulture)},{face.Color.Z.ToString("0.##", CultureInfo.InvariantCulture)})" +
                      (face.Fullbright ? " fullbright" : "") +
                      (face.LegacyMaterialId != Guid.Empty ? $" mat={Short(face.LegacyMaterialId)}" : "") +
                      (face.RenderMaterialId != Guid.Empty ? $" pbr={Short(face.RenderMaterialId)}" : "") +
                      Placement(face) +
                      $" {state.GetValueOrDefault(f, "not-in-LOD")}");
        }
        GD.Print($"[WornFaces] wearer={visual.AgentId.ToString("N")[..8]} item={entityId.ToString("N")[..8]} mesh={Short(meshId)} | " +
                 string.Join(" | ", parts));

        static string Short(Guid id) => id == Guid.Empty ? "-" : id.ToString("N")[..8];
        // The TextureEntry placement, only when it is not the identity (the viewer applies it to a BoM face
        // like to any other, llface.cpp:1704-1790).
        static string Placement(FaceTexture f)
        {
            if (f.RepeatU == 1f && f.RepeatV == 1f && f.OffsetU == 0f && f.OffsetV == 0f && f.Rotation == 0f) return "";
            string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
            return $" rep={F(f.RepeatU)}x{F(f.RepeatV)} off={F(f.OffsetU)},{F(f.OffsetV)} rot={F(f.Rotation)}";
        }
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

    /// <summary>The bake's alpha map made from the uploaded texture (--diag only, once per bake: the map is kept in
    /// <see cref="BakeAlphaMaps"/>), or null when the material carries no texture.</summary>
    private static (byte[] Alpha, int Width, int Height)? ReadBackBakeAlpha(ShaderMaterial material, Guid bakeId)
    {
        if (material.GetShaderParameter(PrimShaderFamily.AlbedoTexture).Obj is not Texture2D texture || !IsInstanceValid(texture))
            return null;
        using var image = texture.GetImage();
        if (image == null || image.IsEmpty()) return null;
        if (image.IsCompressed()) image.Decompress();
        if (image.GetFormat() != Image.Format.Rgba8) image.Convert(Image.Format.Rgba8);
        // Mip 0 only: GetData() is the whole chain.
        int w = image.GetWidth(), h = image.GetHeight();
        var all = image.GetData();
        if (all.Length < w * h * 4) return null;
        var rgba = all.Length == w * h * 4 ? all : all.AsSpan(0, w * h * 4).ToArray();
        BakeAlphaMaps.Capture(bakeId, rgba, w, h);
        return BakeAlphaMaps.TryGet(bakeId, out var map) ? map : null;
    }

    private static readonly HashSet<string> _bomBoundLogged = new();

    /// <summary>[BomBound] (--diag, once per mesh/face/bake): what the surface's albedo sampler ACTUALLY holds, read
    /// back from the uploaded texture, over the face's UV box -- mean RGB and the share of blue-dominant texels (the
    /// "BAKED_UPPER" placeholder is blue). [BomAlpha] measures the decoded bake the asset service handed out; this
    /// measures what is bound, so a placeholder or a wrong image on the sampler shows here and nowhere else.</summary>
    private static void LogBomBound(MeshInstance3D mi, int surface, ShaderMaterial sm, Guid meshId, int faceIndex, FaceMaterialFacts facts)
    {
        string key = $"{meshId:N}:{faceIndex}:{facts.Texture:N}";
        lock (_bomBoundLogged) { if (!_bomBoundLogged.Add(key)) return; }
        if (sm.GetShaderParameter(PrimShaderFamily.AlbedoTexture).Obj is not Texture2D texture || !IsInstanceValid(texture)
            || mi.Mesh is not ArrayMesh mesh || !MeshSurfaceCache.TryGetSurface(mesh, surface, out var arrays))
        {
            GD.Print($"[BomBound] mesh={meshId.ToString("N")[..8]} face={faceIndex} no bound albedo texture (has={sm.GetShaderParameter(PrimShaderFamily.HasAlbedoTexture)})");
            return;
        }
        using var image = texture.GetImage();
        if (image == null || image.IsEmpty()) return;
        if (image.IsCompressed()) image.Decompress();
        if (image.GetFormat() != Image.Format.Rgba8) image.Convert(Image.Format.Rgba8);
        int w = image.GetWidth(), h = image.GetHeight();
        var data = image.GetData();
        var uvs = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
        var scale = sm.GetShaderParameter(PrimShaderFamily.UvScale).AsVector2();
        var offset = sm.GetShaderParameter(PrimShaderFamily.UvOffset).AsVector2();
        long r = 0, g = 0, b = 0, blue = 0, n = 0;
        foreach (var uv in uvs)
        {
            var p = uv * scale + offset;
            int x = Math.Clamp((int)(p.X * w), 0, w - 1), y = Math.Clamp((int)(p.Y * h), 0, h - 1);
            int i = (y * w + x) * 4;
            r += data[i]; g += data[i + 1]; b += data[i + 2]; n++;
            if (data[i + 2] > data[i] + 40 && data[i + 2] > data[i + 1] + 40) blue++;
        }
        if (n == 0) return;
        GD.Print($"[BomBound] mesh={meshId.ToString("N")[..8]} face={faceIndex} intended bake={facts.Texture.ToString("N")[..8]} " +
                 $"bound {w}x{h} at the face's {n} UVs: mean rgb=({r / n},{g / n},{b / n}) blueDominant={(100f * blue / n).ToString("0.#", CultureInfo.InvariantCulture)}% " +
                 $"shader={sm.Shader?.ResourcePath} kind={facts.Kind}");
    }

    /// <summary>Measures a Bakes-on-Mesh face against its bake (see the type's summary). Main thread; reads only
    /// managed copies (the surface's cached arrays, the asset service's decoded bake) -- never the GPU.</summary>
    private void LogBomAlpha(MeshInstance3D mi, int surface, Material material, AvatarVisual? visual, Guid meshId, int faceIndex)
    {
        if (!Diagnostics.Enabled || visual == null || _assetService == null || material is not ShaderMaterial sm) return;
        var facts = FactsOf(sm);
        if (facts.BakeChannel < 0 || facts.Texture == Guid.Empty || facts.Hidden || facts.Failed) return;
        LogBomBound(mi, surface, sm, meshId, faceIndex, facts);
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
        else if (ReadBackBakeAlpha(sm, facts.Texture) is { } readBack)
        {
            // Last resort, once per bake (the map is kept): the uploaded texture itself, read back from VRAM.
            // A RenderingServer sync on the main thread -- acceptable under --diag only, never otherwise.
            (data, width, height, fromMap) = (readBack.Alpha, readBack.Width, readBack.Height, true);
        }
        else
        {
            GD.Print($"[BomAlpha] mesh={meshId.ToString("N")[..8]} face={faceIndex} bake={facts.Texture.ToString("N")[..8]} " +
                     $"ch={facts.BakeChannel}: the bake's pixels could not be had -- not measured");
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
        // Where on the bake the face lands (top-origin, after the TE placement), so it can be compared with the
        // region the body's own BoM faces sample.
        var lo = new System.Numerics.Vector2(float.MaxValue);
        var hi = new System.Numerics.Vector2(float.MinValue);
        foreach (var uv in uvs)
        {
            var p = uv * placeScale + placeOffset;
            lo = System.Numerics.Vector2.Min(lo, p);
            hi = System.Numerics.Vector2.Max(hi, p);
        }
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
                 $"minA={coverage.MinAlpha} meanA={coverage.MeanAlpha.ToString("0.#", CultureInfo.InvariantCulture)} " +
                 $"at u {lo.X.ToString("0.###", CultureInfo.InvariantCulture)}..{hi.X.ToString("0.###", CultureInfo.InvariantCulture)} " +
                 $"v {lo.Y.ToString("0.###", CultureInfo.InvariantCulture)}..{hi.Y.ToString("0.###", CultureInfo.InvariantCulture)} (top-origin){whole}");
    }
}
