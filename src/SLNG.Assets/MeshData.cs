using System.Collections.Generic;
using System.Numerics;

namespace SLNG.Assets;

/// <summary>
/// Engine- and protocol-neutral decoded mesh. Coordinates are in Second Life space
/// (Z-up, metres); the renderer applies any axis conversion. This is what crosses the
/// <c>SLNG.Assets</c> boundary — never a LibreMetaverse type.
/// <para><see cref="Skin"/> is non-null for rigged / fitted mesh (worn mesh bodies and
/// clothing). When present, the renderer skins the mesh to the avatar skeleton instead of
/// attaching it statically to a single bone.</para>
/// </summary>
public sealed record MeshData(IReadOnlyList<MeshSubmesh> Submeshes, MeshSkin? Skin = null);

/// <summary>One submesh: per-vertex arrays addressed by <see cref="Indices"/>.
/// <paramref name="FaceIndex"/> is the SL prim face number this submesh belongs to, so the
/// renderer can apply that face's texture. <paramref name="Weights"/> is parallel to
/// <paramref name="Positions"/> and present only for rigged submeshes (see <see cref="MeshSkin"/>).</summary>
public sealed record MeshSubmesh(
    Vector3[] Positions,
    Vector3[] Normals,
    Vector2[] UVs,
    int[] Indices,
    int FaceIndex = 0,
    VertexBoneWeights[]? Weights = null);

/// <summary>Up to four joint influences for one vertex. <c>Joint*</c> index into
/// <see cref="MeshSkin.JointNames"/>; <c>Weight*</c> are normalized to sum to 1.</summary>
public readonly record struct VertexBoneWeights(
    int Joint0, int Joint1, int Joint2, int Joint3,
    float Weight0, float Weight1, float Weight2, float Weight3);

/// <summary>Skinning data from a mesh asset's <c>skin</c> section. Joint names map to avatar
/// skeleton bones; the bind matrices position the mesh in the skeleton's rest pose.</summary>
public sealed record MeshSkin(
    string[] JointNames,
    Matrix4x4[] InverseBindMatrices,
    Matrix4x4 BindShapeMatrix,
    float PelvisOffset);
