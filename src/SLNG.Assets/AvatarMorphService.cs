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

        if (part.Morphs == null) return (positions, scaledNormals);

        foreach (var morph in part.Morphs)
        {
            // A non-finite effective weight (a degenerate visual param, NaN propagated from the
            // shape math) would turn every touched vertex into NaN — and NaN slips past `w == 0`.
            if (!weights.TryGetValue(morph.ParamId, out float w) || w == 0f || !float.IsFinite(w)) continue;

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

        // Belt and braces: never hand the renderer a non-finite vertex — Godot's normal/tangent
        // generation then logs "Vector3 cannot be normalized, the elements must be finite" and
        // the mesh collapses. Fall back to the untouched base value.
        int reverted = 0;
        for (int i = 0; i < positions.Length; i++)
            if (!IsFinite(positions[i])) { positions[i] = part.Positions[i]; reverted++; }
        if (reverted > 0)
            System.Console.Error.WriteLine(
                $"[AvatarMorph] {part.Name}: {reverted} vertex/vertices went non-finite under this shape " +
                "(a degenerate visual param) — reverted to base");

        var normals = new Vector3[part.Normals.Length];
        for (int i = 0; i < normals.Length; i++)
        {
            var n = scaledNormals[i];
            float lenSq = n.LengthSquared();
            if (lenSq > 1e-12f && float.IsFinite(lenSq))
                normals[i] = n / System.MathF.Sqrt(lenSq);
            else
                normals[i] = IsFinite(part.Normals[i]) && part.Normals[i].LengthSquared() > 1e-12f
                    ? part.Normals[i] : Vector3.UnitY;
        }

        return (positions, normals);
    }

    private static bool IsFinite(Vector3 v) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
