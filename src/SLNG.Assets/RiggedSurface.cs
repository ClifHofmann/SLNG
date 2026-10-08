using System.Numerics;

namespace SLNG.Assets;

/// <summary>One surface of a rigged (worn) mesh, ready to become engine vertex arrays: Godot axes,
/// clockwise-front winding, top-origin V, and four skin-slot influences per vertex.</summary>
/// <param name="FaceIndex">The SL face of the first submesh in the run; it picks the surface's material.</param>
/// <param name="Bones">Four skin SLOT indices per vertex (the position of the bind in the Skin), not
/// skeleton bone indices.</param>
/// <param name="Weights">Four weights per vertex, parallel to <paramref name="Bones"/>, summing to one.</param>
public sealed record RiggedSurface(
    int FaceIndex,
    Vector3[] Positions,
    Vector3[] Normals,
    Vector2[] UVs,
    int[] Bones,
    float[] Weights,
    int[] Indices);
