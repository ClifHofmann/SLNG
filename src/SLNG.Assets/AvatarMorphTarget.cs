using System.Numerics;

namespace SLNG.Assets;

/// <summary>
/// One vertex-morph target for a body-part mesh (the .llm equivalent of the viewer's
/// LLPolyMorphData). Sparse: only the vertices this morph moves are listed. Deltas are
/// per-unit-weight in SL space — the morphed vertex is
/// <c>base + Σ_morphs (effectiveWeight · delta)</c> (see AvatarMorphService), matching
/// LLPolyMorphTarget::apply. <see cref="ParamId"/> is the VisualParam whose effective weight
/// (from AvatarShapeService.ComputeEffectiveWeights) scales this morph.
/// </summary>
public sealed class AvatarMorphTarget
{
    public int ParamId { get; }
    public string Name { get; }

    /// <summary>Index into the owning <see cref="AvatarBodyPartMesh"/>'s base vertex arrays for
    /// each entry in <see cref="PositionDeltas"/> / <see cref="NormalDeltas"/>.</summary>
    public int[] VertexIndices { get; }

    /// <summary>Per-unit-weight position delta (SL space) for each <see cref="VertexIndices"/>.</summary>
    public Vector3[] PositionDeltas { get; }

    /// <summary>Per-unit-weight normal delta (SL space) for each <see cref="VertexIndices"/>.</summary>
    public Vector3[] NormalDeltas { get; }

    public AvatarMorphTarget(int paramId, string name, int[] vertexIndices, Vector3[] positionDeltas, Vector3[] normalDeltas)
    {
        ParamId = paramId;
        Name = name;
        VertexIndices = vertexIndices;
        PositionDeltas = positionDeltas;
        NormalDeltas = normalDeltas;
    }
}
