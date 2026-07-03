using System.Collections.Generic;
using System.Numerics;

namespace SLNG.Assets;

/// <summary>
/// Applies vertex morphs to a base body-part mesh, the engine-neutral equivalent of the viewer's
/// LLPolyMorphTarget::apply (indra/llappearance/llpolymorph.cpp). Given the avatar's effective
/// param weights (from <see cref="AvatarShapeService.ComputeEffectiveWeights"/>), it produces the
/// morphed position and normal arrays the renderer uploads. Pure/allocating: never mutates the
/// base mesh, so the shared cached <see cref="AvatarBodyPartMesh"/> stays reusable across avatars.
/// </summary>
public static class AvatarMorphService
{
    // Viewer's NORMAL_SOFTEN_FACTOR (llpolymorph.cpp:42): morph normal deltas are accumulated at
    // 0.65 strength onto the base normal, then the sum is renormalized. Keeps morphed shading
    // smooth rather than snapping hard to the target normal.
    private const float NormalSoftenFactor = 0.65f;

    /// <summary>Returns freshly-allocated morphed position and normal arrays for
    /// <paramref name="part"/> under <paramref name="weights"/>. Position:
    /// <c>base + Σ (effectiveWeight · positionDelta)</c>. Normal: base normal plus
    /// <c>Σ (effectiveWeight · 0.65 · normalDelta)</c>, renormalized — matching the viewer.
    /// Vertices no morph touches keep their base values. A part with no morphs (or all-zero
    /// weights) returns copies of the base arrays.</summary>
    public static (Vector3[] Positions, Vector3[] Normals) Apply(
        AvatarBodyPartMesh part, IReadOnlyDictionary<int, float> weights)
    {
        var positions = (Vector3[])part.Positions.Clone();
        // Accumulate softened normal deltas onto a copy of the base normals, then renormalize —
        // the viewer's getScaledNormals() starts from the base and each morph adds onto it.
        var scaledNormals = (Vector3[])part.Normals.Clone();

        foreach (var morph in part.Morphs)
        {
            if (!weights.TryGetValue(morph.ParamId, out float w) || w == 0f) continue;

            var vidx = morph.VertexIndices;
            var pdelta = morph.PositionDeltas;
            var ndelta = morph.NormalDeltas;
            for (int k = 0; k < vidx.Length; k++)
            {
                int v = vidx[k];
                if ((uint)v >= (uint)positions.Length) continue; // guard against a bad index
                positions[v] += pdelta[k] * w;
                scaledNormals[v] += ndelta[k] * (w * NormalSoftenFactor);
            }
        }

        var normals = new Vector3[part.Normals.Length];
        for (int i = 0; i < normals.Length; i++)
        {
            var n = scaledNormals[i];
            float lenSq = n.LengthSquared();
            normals[i] = lenSq > 1e-12f ? n / System.MathF.Sqrt(lenSq) : part.Normals[i];
        }

        return (positions, normals);
    }
}
