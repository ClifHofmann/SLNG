using System.Collections.Generic;
using System.Numerics;

namespace SLNG.Assets;

/// <summary>
/// A single body-part mesh loaded from an SL .llm file. Coordinates are in SL space (Z-up);
/// the renderer applies the SL→Godot axis swap. Bone names use Bento naming (mHead, mNeck, …).
/// This record is the only type that crosses the SLNG.Assets boundary — no LibreMetaverse types.
/// </summary>
public record AvatarMorphTarget(
    string Name,
    int[] VertexIndices,
    Vector3[] PositionOffsets,
    Vector3[] NormalOffsets
);

/// <summary>
/// A single body-part mesh loaded from an SL .llm file. Coordinates are in SL space (Z-up);
/// the renderer applies the SL→Godot axis swap. Bone names use Bento naming (mHead, mNeck, …).
/// This record is the only type that crosses the SLNG.Assets boundary — no LibreMetaverse types.
/// </summary>
public sealed record AvatarBodyPartMesh(
    string Name,
    Vector3[] Positions,
    Vector3[] Normals,
    Vector2[] UVs,
    int[] Indices,
    string?[] Bone1Names,
    float[] Bone1Weights,
    string?[] Bone2Names,
    float[] Bone2Weights,
    IReadOnlyList<AvatarMorphTarget>? Morphs = null
);

/// <summary>All body-part meshes needed to render a base avatar.</summary>
public sealed record AvatarBodyMeshData(IReadOnlyList<AvatarBodyPartMesh> Parts);
