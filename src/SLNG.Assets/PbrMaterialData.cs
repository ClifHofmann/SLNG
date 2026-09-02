using System;
using System.Numerics;

namespace SLNG.Assets;

/// <summary>Engine-neutral mirror of glTF's own <c>alphaMode</c> (KHR core spec: OPAQUE/MASK/
/// BLEND). Authoritative when a face carries a real <see cref="PbrMaterialData"/> — no pixel
/// inspection needed, unlike legacy no-material content (see AvatarRenderer's ApplyAlphaCutout
/// heuristic fallback).</summary>
public enum PbrAlphaMode
{
    Opaque,
    Mask,
    Blend,
}

/// <summary>
/// Engine-neutral DTO representing a parsed GLTF material.
/// Contains texture UUIDs and material factors for Godot ORMMaterial3D mapping.
/// </summary>
public sealed record PbrMaterialData(
    Guid BaseColorTextureId,
    Guid NormalTextureId,
    Guid MetallicRoughnessTextureId,
    Guid EmissiveTextureId,
    Vector4 BaseColorFactor,
    float MetallicFactor,
    float RoughnessFactor,
    Vector3 EmissiveFactor,
    PbrAlphaMode AlphaMode = PbrAlphaMode.Opaque,
    // glTF spec default for MASK mode when the asset omits its own cutoff.
    float AlphaCutoff = 0.5f,
    // BUG-RENDER-06: glTF's own doubleSided flag (KHR core spec), mirrored from
    // LibreMetaverse's AssetMaterial.DoubleSided. The real viewer back-face culls every prim
    // face by default and lifts it ONLY for a material that sets this explicitly -- a face's
    // renderer must route through the engine's cull-disabled shader variant when this is true,
    // never as a blanket default (see PrimShaderFamily.Select's doubleSided parameter).
    bool DoubleSided = false
);
