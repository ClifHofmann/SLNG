using System;
using System.Collections.Concurrent;
using System.Globalization;
using Godot;
using SLNG.Assets;
using SLNG.Core;

namespace SLNG.App;

/// <summary>
/// BUG-RENDER-49 follow-up: one <c>[PbrFace]</c> line per (glTF material, override, resulting shader kind),
/// under <c>--diag</c>, for world prims and avatar faces alike. Says what the material asset declares, whether
/// the face carries a FEAT-RENDER-26 override and what it changes, and what the face is drawn as -- so "the PBR
/// hair renders hard-edged and untinted" can be answered from the log: an override that never arrived (no
/// override on the line), one that arrived but did not take (override present, effective mode unchanged), or a
/// base material that never loaded (UNAVAILABLE). Called from material builds, which run on pool threads.
/// </summary>
public static class PbrFaceLog
{
    private static readonly ConcurrentDictionary<(Guid Material, int Override, string Kind, string Who), byte> _seen = new();

    /// <summary>Logs a face whose glTF material is in hand. <paramref name="who"/> names the face's owner (wearer,
    /// item, local id, mesh, face index -- whatever the caller has).</summary>
    public static void Face(string who, Guid materialId, PbrMaterialData baseMaterial, GltfMaterialOverride? o,
        PbrMaterialData effective, string kind)
    {
        if (!Diagnostics.Enabled) return;
        if (!_seen.TryAdd((materialId, o?.GetHashCode() ?? 0, kind, who), 0)) return;
        GD.Print($"[PbrFace] {who} mat={Short(materialId)} base am={baseMaterial.AlphaMode} ac={F(baseMaterial.AlphaCutoff)} " +
                 $"bc={V(baseMaterial.BaseColorFactor)} tex={Short(baseMaterial.BaseColorTextureId)} " +
                 $"override={Describe(o)} -> effective am={effective.AlphaMode} ac={F(effective.AlphaCutoff)} " +
                 $"bc={V(effective.BaseColorFactor)} -> {kind}");
    }

    /// <summary>Logs a face that names a glTF material which is not to be had: it is drawn as its legacy self.</summary>
    public static void Unavailable(string who, Guid materialId, GltfMaterialOverride? o)
    {
        if (!Diagnostics.Enabled) return;
        if (!_seen.TryAdd((materialId, o?.GetHashCode() ?? 0, "unavailable", who), 0)) return;
        GD.Print($"[PbrFace] {who} mat={Short(materialId)} UNAVAILABLE override={Describe(o)} -> legacy face");
    }

    private static string Describe(GltfMaterialOverride? o)
    {
        if (o == null || o.IsEmpty) return "none";
        var parts = new System.Collections.Generic.List<string>(5);
        if (o.BaseColor is { } bc) parts.Add($"bc={V(bc)}");
        if (o.AlphaMode is { } am) parts.Add($"am={am}");
        if (o.AlphaCutoff is { } ac) parts.Add($"ac={F(ac)}");
        if (o.BaseColorTexture is { } tex) parts.Add($"tex={(tex == Guid.Empty ? "removed" : Short(tex))}");
        if (o.DoubleSided is { } ds) parts.Add($"ds={ds}");
        if (o.BaseColorTransform != null) parts.Add("ti");
        return parts.Count == 0 ? "present(other fields)" : string.Join(' ', parts);
    }

    private static string Short(Guid id) => id == Guid.Empty ? "-" : id.ToString("N")[..8];
    private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static string V(System.Numerics.Vector4 v) => $"({F(v.X)},{F(v.Y)},{F(v.Z)},{F(v.W)})";
}
