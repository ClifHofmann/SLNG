using System;
using System.Numerics;

namespace SLNG.Core;

/// <summary>What one face's override changes. A null member means "not overridden". Value equality, so equal
/// overrides compare equal inside FaceTexture (re-apply gate, FaceSurfaceMerge, PrimMaterialKey).</summary>
public sealed record GltfMaterialOverride
{
    // Texture per slot: null = unchanged, Guid.Empty = REMOVE the texture (the wire's all-F UUID), else replace.
    public Guid? BaseColorTexture { get; init; }
    public Guid? NormalTexture { get; init; }
    public Guid? MetallicRoughnessTexture { get; init; }
    public Guid? EmissiveTexture { get; init; }
    public Vector4? BaseColor { get; init; }          // r,g,b,a
    public Vector3? EmissiveColor { get; init; }
    public float? Metallic { get; init; }
    public float? Roughness { get; init; }
    public GltfAlphaMode? AlphaMode { get; init; }
    public float? AlphaCutoff { get; init; }
    public bool? DoubleSided { get; init; }
    // Per slot; null = no transform override for that slot. Only BaseColorTransform is rendered in v1.
    public GltfTextureTransform? BaseColorTransform { get; init; }
    public GltfTextureTransform? NormalTransform { get; init; }
    public GltfTextureTransform? MetallicRoughnessTransform { get; init; }
    public GltfTextureTransform? EmissiveTransform { get; init; }

    /// <summary>True when nothing is overridden (all 15 members null). Expression-bodied on purpose: a property
    /// with no backing field does not take part in the record's value equality.</summary>
    public bool IsEmpty => BaseColorTexture is null && NormalTexture is null && MetallicRoughnessTexture is null
        && EmissiveTexture is null && BaseColor is null && EmissiveColor is null && Metallic is null
        && Roughness is null && AlphaMode is null && AlphaCutoff is null && DoubleSided is null
        && BaseColorTransform is null && NormalTransform is null && MetallicRoughnessTransform is null
        && EmissiveTransform is null;
}
