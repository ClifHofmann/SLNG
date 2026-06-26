using System;
using System.Numerics;

namespace SLNG.Assets;

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
    Vector3 EmissiveFactor
);
