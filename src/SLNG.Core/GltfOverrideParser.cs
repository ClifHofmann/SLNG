using System;
using System.Collections.Generic;
using System.Numerics;

namespace SLNG.Core;

public static class GltfOverrideParser
{
    public const int MaxFaces = 45; // MAX_TES, llgltfmateriallist.cpp:202
    public static readonly Guid RemoveTextureMarker = new("ffffffff-ffff-ffff-ffff-ffffffffffff");

    public static bool TryParse(ReadOnlySpan<byte> payload, out GltfOverrideMessage? message, out string? error)
    {
        if (!LlsdNotation.TryParse(payload, out var root, out var llsdErr))
        {
            message = null;
            error = llsdErr;
            return false;
        }

        return TryFromLlsd(root, out message, out error);
    }

    public static bool TryFromLlsd(LlsdValue root, out GltfOverrideMessage? message, out string? error)
    {
        message = null;
        error = null;

        if (!root.IsMap)
        {
            error = "root is not a map";
            return false;
        }

        var idVal = root["id"];
        if (!idVal.IsInteger)
        {
            error = "missing or invalid integer 'id'";
            return false;
        }

        var teVal = root["te"];
        if (!teVal.IsArray)
        {
            error = "no 'te' array";
            return false;
        }

        var odVal = root["od"];
        var result = new Dictionary<int, GltfMaterialOverride>();

        int limit = Math.Min(teVal.Count, MaxFaces);
        for (int i = 0; i < limit; i++)
        {
            var faceElem = teVal[i];
            int face = faceElem.AsInteger();
            if (face < 0 || face >= MaxFaces)
            {
                continue;
            }

            var odElem = odVal[i]; // Undefined if i >= odVal.Count or not array
            var o = ParseOverride(odElem);
            if (o.IsEmpty)
            {
                result.Remove(face);
            }
            else
            {
                result[face] = o;
            }
        }

        message = new GltfOverrideMessage((uint)idVal.AsInteger(), result);
        return true;
    }

    internal static GltfMaterialOverride ParseOverride(LlsdValue od)
    {
        if (!od.IsMap)
        {
            return new GltfMaterialOverride();
        }

        Guid? baseColorTex = null;
        Guid? normalTex = null;
        Guid? mrTex = null;
        Guid? emissiveTex = null;

        var texVal = od["tex"];
        if (texVal.IsArray)
        {
            baseColorTex = ParseTextureSlot(texVal[0]);
            normalTex = ParseTextureSlot(texVal[1]);
            mrTex = ParseTextureSlot(texVal[2]);
            emissiveTex = ParseTextureSlot(texVal[3]);
        }

        Vector4? baseColor = null;
        var bcVal = od["bc"];
        if (bcVal.IsArray && bcVal.Count >= 4)
        {
            double r = bcVal[0].AsReal();
            double g = bcVal[1].AsReal();
            double b = bcVal[2].AsReal();
            double a = bcVal[3].AsReal();
            if (double.IsFinite(r) && double.IsFinite(g) && double.IsFinite(b) && double.IsFinite(a))
            {
                baseColor = new Vector4((float)r, (float)g, (float)b, (float)a);
            }
        }

        Vector3? emissiveColor = null;
        var ecVal = od["ec"];
        if (ecVal.IsArray && ecVal.Count >= 3)
        {
            double r = ecVal[0].AsReal();
            double g = ecVal[1].AsReal();
            double b = ecVal[2].AsReal();
            if (double.IsFinite(r) && double.IsFinite(g) && double.IsFinite(b))
            {
                emissiveColor = new Vector3((float)r, (float)g, (float)b);
            }
        }

        float? metallic = null;
        var mfVal = od["mf"];
        if (mfVal.IsReal && double.IsFinite(mfVal.AsReal()))
        {
            metallic = (float)mfVal.AsReal();
        }

        float? roughness = null;
        var rfVal = od["rf"];
        if (rfVal.IsReal && double.IsFinite(rfVal.AsReal()))
        {
            roughness = (float)rfVal.AsReal();
        }

        GltfAlphaMode? alphaMode = null;
        var amVal = od["am"];
        if (amVal.IsInteger)
        {
            int am = amVal.AsInteger();
            if (am >= 0 && am <= 2)
            {
                alphaMode = (GltfAlphaMode)am;
            }
        }

        float? alphaCutoff = null;
        var acVal = od["ac"];
        if (acVal.IsReal && double.IsFinite(acVal.AsReal()))
        {
            alphaCutoff = (float)acVal.AsReal();
        }

        bool? doubleSided = null;
        var dsVal = od["ds"];
        if (dsVal.IsBoolean)
        {
            doubleSided = dsVal.AsBoolean();
        }

        GltfTextureTransform? bcTransform = null;
        GltfTextureTransform? normTransform = null;
        GltfTextureTransform? mrTransform = null;
        GltfTextureTransform? emTransform = null;

        var tiVal = od["ti"];
        if (tiVal.IsArray)
        {
            bcTransform = ParseTransformSlot(tiVal[0]);
            normTransform = ParseTransformSlot(tiVal[1]);
            mrTransform = ParseTransformSlot(tiVal[2]);
            emTransform = ParseTransformSlot(tiVal[3]);
        }

        return new GltfMaterialOverride
        {
            BaseColorTexture = baseColorTex,
            NormalTexture = normalTex,
            MetallicRoughnessTexture = mrTex,
            EmissiveTexture = emissiveTex,
            BaseColor = baseColor,
            EmissiveColor = emissiveColor,
            Metallic = metallic,
            Roughness = roughness,
            AlphaMode = alphaMode,
            AlphaCutoff = alphaCutoff,
            DoubleSided = doubleSided,
            BaseColorTransform = bcTransform,
            NormalTransform = normTransform,
            MetallicRoughnessTransform = mrTransform,
            EmissiveTransform = emTransform
        };
    }

    private static Guid? ParseTextureSlot(LlsdValue val)
    {
        if (!val.IsUuid) return null;
        Guid g = val.AsUuid();
        if (g == Guid.Empty) return null;
        if (g == RemoveTextureMarker) return Guid.Empty;
        return g;
    }

    private static GltfTextureTransform? ParseTransformSlot(LlsdValue m)
    {
        if (!m.IsMap) return null;
        var oVal = m["o"];
        var sVal = m["s"];
        var rVal = m["r"];
        if (!oVal.IsDefined && !sVal.IsDefined && !rVal.IsDefined) return null;

        Vector2 offset = Vector2.Zero;
        if (oVal.IsArray && oVal.Count >= 2)
        {
            double ox = oVal[0].AsReal();
            double oy = oVal[1].AsReal();
            if (double.IsFinite(ox) && double.IsFinite(oy))
            {
                offset = new Vector2((float)ox, (float)oy);
            }
        }

        Vector2 scale = Vector2.One;
        if (sVal.IsArray && sVal.Count >= 2)
        {
            double sx = sVal[0].AsReal();
            double sy = sVal[1].AsReal();
            if (double.IsFinite(sx) && double.IsFinite(sy))
            {
                scale = new Vector2((float)sx, (float)sy);
            }
        }

        float rot = 0f;
        if (rVal.IsReal && double.IsFinite(rVal.AsReal()))
        {
            rot = (float)rVal.AsReal();
        }

        return new GltfTextureTransform(offset, scale, rot);
    }
}
