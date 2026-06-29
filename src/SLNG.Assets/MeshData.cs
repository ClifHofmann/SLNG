using System.Collections.Generic;
using System.Numerics;

namespace SLNG.Assets;

/// <summary>
/// Engine- and protocol-neutral decoded mesh. Coordinates are in Second Life space
/// (Z-up, metres); the renderer applies any axis conversion. This is what crosses the
/// <c>SLNG.Assets</c> boundary — never a LibreMetaverse type.
/// </summary>
public sealed record MeshData(IReadOnlyList<MeshSubmesh> Submeshes);

/// <summary>One submesh: per-vertex arrays addressed by <see cref="Indices"/>.
/// <paramref name="FaceIndex"/> is the SL prim face number this submesh belongs to, so the
/// renderer can apply that face's texture.</summary>
public sealed record MeshSubmesh(Vector3[] Positions, Vector3[] Normals, Vector2[] UVs, int[] Indices, int FaceIndex = 0);
