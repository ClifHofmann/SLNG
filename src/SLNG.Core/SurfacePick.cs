using System;
using System.Collections.Generic;
using System.Numerics;

namespace SLNG.Core;

/// <summary>
/// Where on an object a click actually landed, in the detail the SL touch protocol carries.
/// All values are in the mesh's own local frame; the caller rotates them into region space.
/// </summary>
/// <param name="FaceIndex">The SL prim face that was hit — what <c>llDetectedTouchFace</c>
/// returns.</param>
/// <param name="St">The interpolated mesh UV at the hit point — what the wire calls STCoord and
/// what <c>llDetectedTouchST</c> returns.</param>
public readonly record struct SurfaceHit(
    int FaceIndex,
    Vector2 St,
    Vector3 Position,
    Vector3 Normal,
    Vector3 Binormal,
    float Distance);

/// <summary>One submesh to test a ray against: an SL face's geometry, indexed triangles.</summary>
/// <remarks>
/// Deliberately plain arrays rather than the decoded-mesh type. <c>SLNG.Core</c> sits below
/// <c>SLNG.Assets</c> and cannot see it, and this way the maths is testable without decoding an
/// asset first.
/// </remarks>
public readonly record struct PickSubmesh(
    Vector3[] Positions,
    Vector3[]? Normals,
    Vector2[]? Uvs,
    int[] Indices,
    int FaceIndex);

/// <summary>
/// Ray/mesh intersection at face granularity — the client half of an SL touch.
///
/// <para>A touch is not just "this object was clicked". The wire carries the face, the surface
/// coordinates, the normal and the binormal of the exact spot (<c>ObjectGrab.SurfaceInfo</c>,
/// filled by <c>LLPickInfo::getSurfaceInfo</c>), because that is what a script reads back with
/// <c>llDetectedTouchFace</c>, <c>llDetectedTouchST</c> and <c>llDetectedTouchUV</c>. Multi-item
/// vendors are built on exactly that: one panel, one face per product. Telling every script that
/// face 0 was touched at (0,0) is not a missing nicety — it is a wrong answer, and a vendor that
/// acts on the face simply does nothing.</para>
///
/// <para>The physics raycast that finds WHICH object was clicked cannot answer this: its collision
/// shape is one undifferentiated triangle soup with no faces and no texture coordinates. So the
/// object is picked by physics and then the ray is re-tested against that one object's decoded
/// geometry, which is what the reference viewer does too (<c>cursorIntersect</c> against a single
/// object).</para>
/// </summary>
public static class SurfacePick
{
    // Anything closer than this along the ray is the surface we started on, not a hit.
    private const float MinDistance = 1e-4f;

    /// <summary>
    /// Finds the nearest triangle <paramref name="direction"/> crosses from
    /// <paramref name="origin"/>, both in the mesh's local frame.
    /// </summary>
    /// <remarks>
    /// Front and back faces both count. The ray has already been shown to hit this object, so
    /// refusing a back-facing triangle would only ever turn a real hit into no answer at all —
    /// SL content is full of single-sided geometry viewed from its back, and a prim's inside
    /// surfaces are legitimately touchable.
    /// </remarks>
    public static bool TryPick(
        IReadOnlyList<PickSubmesh> submeshes, Vector3 origin, Vector3 direction, out SurfaceHit hit)
    {
        hit = default;
        if (submeshes == null || submeshes.Count == 0) return false;

        float dirLength = direction.Length();
        if (dirLength < float.Epsilon) return false;
        Vector3 dir = direction / dirLength;

        float best = float.MaxValue;
        bool found = false;

        foreach (var sub in submeshes)
        {
            var positions = sub.Positions;
            var indices = sub.Indices;
            if (positions == null || indices == null) continue;

            for (int t = 0; t + 2 < indices.Length; t += 3)
            {
                int i0 = indices[t], i1 = indices[t + 1], i2 = indices[t + 2];
                // A malformed asset can index past its own vertex array. Skip that triangle
                // rather than throwing, exactly as the collision-shape builder does.
                if ((uint)i0 >= (uint)positions.Length ||
                    (uint)i1 >= (uint)positions.Length ||
                    (uint)i2 >= (uint)positions.Length) continue;

                if (!RayTriangle(origin, dir, positions[i0], positions[i1], positions[i2],
                                 out float distance, out float bu, out float bv)) continue;
                if (distance >= best) continue;

                best = distance;
                found = true;
                hit = Resolve(sub, i0, i1, i2, bu, bv, origin + dir * distance, distance);
            }
        }

        return found;
    }

    /// <summary>Fills in everything the wire wants once the winning triangle is known.</summary>
    private static SurfaceHit Resolve(
        PickSubmesh sub, int i0, int i1, int i2, float bu, float bv, Vector3 position, float distance)
    {
        float bw = 1f - bu - bv;

        var p0 = sub.Positions[i0];
        var p1 = sub.Positions[i1];
        var p2 = sub.Positions[i2];

        Vector2 uv0 = default, uv1 = default, uv2 = default;
        bool hasUv = sub.Uvs != null && sub.Uvs.Length > Math.Max(i0, Math.Max(i1, i2));
        if (hasUv)
        {
            uv0 = sub.Uvs![i0];
            uv1 = sub.Uvs[i1];
            uv2 = sub.Uvs[i2];
        }

        Vector2 st = hasUv ? uv0 * bw + uv1 * bu + uv2 * bv : Vector2.Zero;

        // The shaded normal where the asset has one, so a smooth-shaded surface reports the
        // normal it LOOKS like it has rather than the flat facet it is built from. Falling back
        // to the facet normal keeps a mesh without normals from reporting a zero vector, which
        // is what "no normal at all" looks like on the wire.
        Vector3 normal;
        bool hasNormals = sub.Normals != null && sub.Normals.Length > Math.Max(i0, Math.Max(i1, i2));
        if (hasNormals)
        {
            normal = sub.Normals![i0] * bw + sub.Normals[i1] * bu + sub.Normals[i2] * bv;
        }
        else
        {
            normal = Vector3.Cross(p1 - p0, p2 - p0);
        }
        normal = Normalize(normal, Vector3.UnitZ);

        Vector3 binormal = Binormal(p0, p1, p2, uv0, uv1, uv2, normal, hasUv);

        return new SurfaceHit(sub.FaceIndex, st, position, normal, binormal, distance);
    }

    /// <summary>
    /// The direction the texture's V axis runs in, at the hit. The viewer derives it the same way
    /// round — tangent from the triangle's UV gradient, then <c>binormal = normal x tangent</c>
    /// (<c>LLPickInfo::getSurfaceInfo</c>) — so a script comparing it against a known face gets
    /// the same vector we would send from the same geometry.
    /// </summary>
    private static Vector3 Binormal(
        Vector3 p0, Vector3 p1, Vector3 p2,
        Vector2 uv0, Vector2 uv1, Vector2 uv2, Vector3 normal, bool hasUv)
    {
        Vector3 e1 = p1 - p0;
        Vector3 e2 = p2 - p0;

        if (hasUv)
        {
            Vector2 d1 = uv1 - uv0;
            Vector2 d2 = uv2 - uv0;
            float det = d1.X * d2.Y - d2.X * d1.Y;
            // A degenerate UV triangle (all three vertices on one texture coordinate) has no
            // gradient to read a tangent from; anything derived from it would be noise.
            if (MathF.Abs(det) > 1e-12f)
            {
                Vector3 tangent = (e1 * d2.Y - e2 * d1.Y) / det;
                return Normalize(Vector3.Cross(normal, tangent), FallbackPerpendicular(normal));
            }
        }

        return Normalize(Vector3.Cross(normal, e1), FallbackPerpendicular(normal));
    }

    /// <summary>Möller–Trumbore, two-sided. <paramref name="bu"/>/<paramref name="bv"/> are the
    /// barycentric weights of v1 and v2.</summary>
    private static bool RayTriangle(
        Vector3 origin, Vector3 dir, Vector3 v0, Vector3 v1, Vector3 v2,
        out float distance, out float bu, out float bv)
    {
        distance = 0f; bu = 0f; bv = 0f;

        Vector3 e1 = v1 - v0;
        Vector3 e2 = v2 - v0;
        Vector3 pvec = Vector3.Cross(dir, e2);
        float det = Vector3.Dot(e1, pvec);

        // Edge-on: the ray runs parallel to the triangle's plane.
        if (MathF.Abs(det) < 1e-12f) return false;

        float inv = 1f / det;
        Vector3 tvec = origin - v0;
        float u = Vector3.Dot(tvec, pvec) * inv;
        if (u < -1e-6f || u > 1f + 1e-6f) return false;

        Vector3 qvec = Vector3.Cross(tvec, e1);
        float v = Vector3.Dot(dir, qvec) * inv;
        if (v < -1e-6f || u + v > 1f + 1e-6f) return false;

        float t = Vector3.Dot(e2, qvec) * inv;
        if (t < MinDistance) return false;

        distance = t; bu = u; bv = v;
        return true;
    }

    private static Vector3 Normalize(Vector3 v, Vector3 fallback)
    {
        float length = v.Length();
        return length > 1e-12f ? v / length : fallback;
    }

    private static Vector3 FallbackPerpendicular(Vector3 normal)
    {
        Vector3 axis = MathF.Abs(normal.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX;
        return Normalize(Vector3.Cross(normal, axis), Vector3.UnitX);
    }
}
