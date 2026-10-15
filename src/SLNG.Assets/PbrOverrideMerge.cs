using System;
using System.Numerics;
using SLNG.Core;

namespace SLNG.Assets;

public static class PbrOverrideMerge
{
    /// <summary>The material a face renders with: <paramref name="baseMaterial"/> with the override's present fields
    /// applied (LLGLTFMaterial::applyOverride, llgltfmaterial.cpp:612-682, with explicit presence instead of the
    /// viewer's "differs from default" test). Returns the same instance when there is nothing to apply.</summary>
    public static PbrMaterialData Apply(PbrMaterialData baseMaterial, GltfMaterialOverride? o)
    {
        if (o is null || o.IsEmpty) return baseMaterial;
        return baseMaterial with
        {
            BaseColorTextureId = o.BaseColorTexture ?? baseMaterial.BaseColorTextureId,
            NormalTextureId = o.NormalTexture ?? baseMaterial.NormalTextureId,
            MetallicRoughnessTextureId = o.MetallicRoughnessTexture ?? baseMaterial.MetallicRoughnessTextureId,
            EmissiveTextureId = o.EmissiveTexture ?? baseMaterial.EmissiveTextureId,
            BaseColorFactor = o.BaseColor is { } bc ? Clamp01(bc) : baseMaterial.BaseColorFactor,
            EmissiveFactor = o.EmissiveColor is { } ec ? Clamp01(ec) : baseMaterial.EmissiveFactor,
            MetallicFactor = o.Metallic is { } m ? Math.Clamp(m, 0f, 1f) : baseMaterial.MetallicFactor,
            RoughnessFactor = o.Roughness is { } r ? Math.Clamp(r, 0f, 1f) : baseMaterial.RoughnessFactor,
            AlphaMode = o.AlphaMode is { } am ? Map(am) : baseMaterial.AlphaMode,
            AlphaCutoff = o.AlphaCutoff is { } ac ? Math.Clamp(ac, 0f, 1f) : baseMaterial.AlphaCutoff,
            DoubleSided = o.DoubleSided ?? baseMaterial.DoubleSided,
        };
    }

    private static PbrAlphaMode Map(GltfAlphaMode m) => m switch
    {
        GltfAlphaMode.Blend => PbrAlphaMode.Blend,
        GltfAlphaMode.Mask => PbrAlphaMode.Mask,
        _ => PbrAlphaMode.Opaque,
    };

    private static Vector4 Clamp01(Vector4 v) => new(
        Math.Clamp(v.X, 0f, 1f),
        Math.Clamp(v.Y, 0f, 1f),
        Math.Clamp(v.Z, 0f, 1f),
        Math.Clamp(v.W, 0f, 1f)
    );

    private static Vector3 Clamp01(Vector3 v) => new(
        Math.Clamp(v.X, 0f, 1f),
        Math.Clamp(v.Y, 0f, 1f),
        Math.Clamp(v.Z, 0f, 1f)
    );
}
