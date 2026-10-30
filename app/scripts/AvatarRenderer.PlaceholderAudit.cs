using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using SLNG.Core;

namespace SLNG.App;

/// <summary>
/// BUG-RENDER-54 diagnostic (--diag only): finds the surface drawing a Bakes-on-Mesh placeholder ("BAKED_UPPER" on
/// blue). Every 10 s it walks each avatar's node tree and reads back the albedo texture of every surface; a
/// texture whose texels are mostly blue-dominant is reported with the node, its surface, the avatar and the shader.
/// Whatever the path that bound it, the surface is named here.
/// </summary>
public partial class AvatarRenderer
{
    private double _placeholderAuditTimer = 10.0;
    private readonly Dictionary<ulong, bool> _placeholderAuditVerdict = new();
    private readonly HashSet<string> _placeholderAuditLogged = new();

    private void TickPlaceholderAudit(double delta)
    {
        if (!Diagnostics.Enabled) return;
        _placeholderAuditTimer -= delta;
        if (_placeholderAuditTimer > 0) return;
        _placeholderAuditTimer = 10.0;

        int surfaces = 0, reported = 0;
        foreach (var visual in _visuals.Values)
        {
            if (visual.Skeleton == null || !IsInstanceValid(visual.Skeleton)) continue;
            var root = visual.Skeleton.GetParent() ?? visual.Skeleton;
            var stack = new Stack<Node>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                foreach (var child in node.GetChildren()) stack.Push(child);
                if (node is not MeshInstance3D mi || !mi.Visible || mi.Mesh == null) continue;
                int count = mi.Mesh.GetSurfaceCount();
                for (int s = 0; s < count; s++)
                {
                    if (mi.GetSurfaceOverrideMaterial(s) is not ShaderMaterial sm) continue;
                    if (sm.GetShaderParameter(PrimShaderFamily.AlbedoTexture).Obj is not Texture2D tex || !IsInstanceValid(tex)) continue;
                    surfaces++;
                    ulong id = tex.GetInstanceId();
                    if (!_placeholderAuditVerdict.TryGetValue(id, out bool blue))
                        _placeholderAuditVerdict[id] = blue = LooksLikePlaceholder(tex);
                    if (!blue) continue;
                    string key = $"{mi.GetPath()}:{s}:{id}";
                    if (!_placeholderAuditLogged.Add(key) || reported++ >= 12) continue;
                    var facts = _faceMaterialFacts.TryGetValue(sm, out var f) ? f.ToString() : "no facts";
                    GD.Print($"[PlaceholderAudit] wearer={visual.AgentId.ToString("N")[..8]} node={mi.GetPath()} name={mi.Name} surface={s} " +
                             $"tex={tex.GetWidth()}x{tex.GetHeight()} shader={sm.Shader?.ResourcePath} visible={mi.IsVisibleInTree()} facts={facts}");
                }
            }
        }
        if (reported > 0) GD.Print($"[PlaceholderAudit] {reported} surface(s) of {surfaces} sample a mostly-blue texture");
    }

    private static bool LooksLikePlaceholder(Texture2D texture)
    {
        using var image = texture.GetImage();
        if (image == null || image.IsEmpty()) return false;
        if (image.IsCompressed()) image.Decompress();
        if (image.GetFormat() != Image.Format.Rgba8) image.Convert(Image.Format.Rgba8);
        int w = image.GetWidth(), h = image.GetHeight();
        var data = image.GetData();
        long blue = 0, n = 0;
        for (int y = 0; y < h; y += 8)
            for (int x = 0; x < w; x += 8)
            {
                int i = (y * w + x) * 4;
                n++;
                if (data[i + 2] > data[i] + 40 && data[i + 2] > data[i + 1] + 40) blue++;
            }
        return n > 0 && 100f * blue / n > 40f;
    }
}
