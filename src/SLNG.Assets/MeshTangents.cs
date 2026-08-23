using System;
using System.Numerics;

namespace SLNG.Assets;

/// <summary>
/// Per-vertex tangents, computed from positions, normals and UVs.
///
/// <para>Normal mapping needs them and nothing else provides them. Godot's
/// <c>SurfaceTool.GenerateTangents()</c> was tried and had to be switched off again: it produced
/// NaNs on degenerate triangles and those propagated into a Vulkan driver crash — SL content is
/// full of degenerate triangles (zero-area faces from extreme prim cuts, collapsed sculpt-grid
/// rows at a pole, duplicated vertices). The comment left at that call site is the whole reason
/// this exists.</para>
///
/// <para>So the safety property here is not incidental, it is the point: <b>every output is
/// finite</b>. Degenerate triangles contribute nothing instead of contributing an infinity, and a
/// vertex that ends up with no usable contribution falls back to an arbitrary but valid tangent
/// perpendicular to its normal rather than to a zero or NaN vector.</para>
///
/// <para>The algorithm is the standard Lengyel accumulate-and-orthonormalise: accumulate each
/// triangle's UV-space tangent onto its three vertices, then Gram-Schmidt against the vertex
/// normal. The handedness (w) is the usual cross-product sign test, which is what lets a mesh
/// with mirrored UVs — common on sculpts, whose two halves share a map — still light correctly.</para>
/// </summary>
public static class MeshTangents
{
    /// <summary>Computes one tangent per vertex as (x, y, z, w), with w = ±1 handedness.
    /// Positions, normals and UVs must be parallel arrays; indices are triangles.</summary>
    public static Vector4[] Compute(
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<Vector3> normals,
        ReadOnlySpan<Vector2> uvs,
        ReadOnlySpan<int> indices)
    {
        int n = positions.Length;
        var result = new Vector4[n];
        if (n == 0) return result;

        var tan = new Vector3[n];
        var bitan = new Vector3[n];

        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            int i0 = indices[t], i1 = indices[t + 1], i2 = indices[t + 2];
            if ((uint)i0 >= (uint)n || (uint)i1 >= (uint)n || (uint)i2 >= (uint)n) continue;

            Vector3 e1 = positions[i1] - positions[i0];
            Vector3 e2 = positions[i2] - positions[i0];
            Vector2 d1 = uvs[i1] - uvs[i0];
            Vector2 d2 = uvs[i2] - uvs[i0];

            // The determinant is zero exactly when the triangle has no area in UV space -- a
            // collapsed triangle, or three vertices sharing one UV. Dividing by it is where
            // SurfaceTool's NaNs came from. Skipping costs nothing: such a triangle carries no
            // information about the tangent direction anyway.
            float det = d1.X * d2.Y - d2.X * d1.Y;
            if (!IsFinite(det) || MathF.Abs(det) < 1e-12f) continue;

            float r = 1f / det;
            Vector3 sdir = (e1 * d2.Y - e2 * d1.Y) * r;
            Vector3 tdir = (e2 * d1.X - e1 * d2.X) * r;
            if (!IsFinite(sdir) || !IsFinite(tdir)) continue;

            tan[i0] += sdir; tan[i1] += sdir; tan[i2] += sdir;
            bitan[i0] += tdir; bitan[i1] += tdir; bitan[i2] += tdir;
        }

        for (int i = 0; i < n; i++)
        {
            Vector3 normal = normals.Length > i ? normals[i] : Vector3.UnitZ;
            if (!IsFinite(normal) || normal.LengthSquared() < 1e-12f) normal = Vector3.UnitZ;
            normal = Vector3.Normalize(normal);

            // Gram-Schmidt: the tangent must be perpendicular to the normal, and the accumulated
            // one generally is not (adjacent triangles pull it around).
            Vector3 t = tan[i] - normal * Vector3.Dot(normal, tan[i]);
            if (!IsFinite(t) || t.LengthSquared() < 1e-12f)
            {
                // No usable contribution: every triangle touching this vertex was degenerate, or
                // it is unreferenced. Any tangent perpendicular to the normal is valid; what must
                // not happen is a zero or NaN reaching the GPU.
                t = PerpendicularTo(normal);
            }
            t = Vector3.Normalize(t);

            float w = Vector3.Dot(Vector3.Cross(normal, t), bitan[i]) < 0f ? -1f : 1f;
            result[i] = new Vector4(t, w);
        }

        return result;
    }

    /// <summary>An arbitrary unit vector perpendicular to <paramref name="normal"/>, chosen from
    /// whichever axis the normal is least aligned with so the cross product cannot collapse.</summary>
    private static Vector3 PerpendicularTo(Vector3 normal)
    {
        Vector3 axis = MathF.Abs(normal.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
        return Vector3.Normalize(Vector3.Cross(normal, axis));
    }

    private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

    private static bool IsFinite(Vector3 v) => IsFinite(v.X) && IsFinite(v.Y) && IsFinite(v.Z);
}
