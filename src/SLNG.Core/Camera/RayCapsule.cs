using System;
using System.Numerics;

namespace SLNG.Core.Camera;

/// <summary>
/// Ray against a capsule (the segment a-b swept by a sphere of the given radius), for picking an
/// avatar by its skeleton instead of by a single physics proxy (FEAT-UI-55).
/// </summary>
public static class RayCapsule
{
    private const float MinT = 1e-6f;

    /// <summary>
    /// The nearest hit of the ray <c>origin + t * dir</c> with the capsule's surface at <c>t &gt; 0</c>.
    /// <paramref name="dir"/> need not be unit length; <paramref name="t"/> is a distance. A ray that
    /// starts inside the capsule reports where it leaves, so a camera pushed into a head still gets a
    /// focus point. A zero-length segment is a sphere.
    /// </summary>
    public static bool Intersect(Vector3 origin, Vector3 dir, Vector3 a, Vector3 b, float radius, out float t)
    {
        t = 0f;
        float len = dir.Length();
        if (len < 1e-9f || radius <= 0f) return false;
        var d = dir / len;

        float best = float.MaxValue;
        var ba = b - a;
        float baba = Vector3.Dot(ba, ba);

        if (baba < 1e-10f)
        {
            if (SphereRoots(origin, d, a, radius, out float near, out float far))
            {
                if (near > MinT) best = near;
                else if (far > MinT) best = far;
            }
        }
        else
        {
            var oa = origin - a;

            // Infinite cylinder around the axis, accepted only between the two end planes.
            float bard = Vector3.Dot(ba, d);
            float baoa = Vector3.Dot(ba, oa);
            float qa = baba - bard * bard;
            if (qa > 1e-9f * baba)
            {
                float rdoa = Vector3.Dot(d, oa);
                float oaoa = Vector3.Dot(oa, oa);
                float qb = baba * rdoa - baoa * bard;
                float qc = baba * oaoa - baoa * baoa - radius * radius * baba;
                float h = qb * qb - qa * qc;
                if (h >= 0f)
                {
                    float sq = MathF.Sqrt(h);
                    ConsiderBody((-qb - sq) / qa, baoa, bard, baba, ref best);
                    ConsiderBody((-qb + sq) / qa, baoa, bard, baba, ref best);
                }
            }

            // The end caps are spheres; only the half facing away from the segment is surface.
            if (SphereRoots(origin, d, a, radius, out float na, out float fa))
            {
                ConsiderCap(na, origin, d, a, ba, atStart: true, ref best);
                ConsiderCap(fa, origin, d, a, ba, atStart: true, ref best);
            }
            if (SphereRoots(origin, d, b, radius, out float nb, out float fb))
            {
                ConsiderCap(nb, origin, d, b, ba, atStart: false, ref best);
                ConsiderCap(fb, origin, d, b, ba, atStart: false, ref best);
            }
        }

        if (best == float.MaxValue) return false;
        t = best;
        return true;
    }

    private static void ConsiderBody(float tc, float baoa, float bard, float baba, ref float best)
    {
        if (tc <= MinT || tc >= best) return;
        float y = baoa + tc * bard; // position along the axis: 0..baba is inside the segment
        if (y >= 0f && y <= baba) best = tc;
    }

    private static void ConsiderCap(float tc, Vector3 origin, Vector3 d, Vector3 centre, Vector3 ba, bool atStart,
        ref float best)
    {
        if (tc <= MinT || tc >= best) return;
        float side = Vector3.Dot(origin + d * tc - centre, ba);
        if (atStart ? side <= 0f : side >= 0f) best = tc;
    }

    private static bool SphereRoots(Vector3 origin, Vector3 d, Vector3 centre, float radius, out float near, out float far)
    {
        near = far = 0f;
        var oc = origin - centre;
        float b = Vector3.Dot(oc, d);
        float c = Vector3.Dot(oc, oc) - radius * radius;
        float h = b * b - c;
        if (h < 0f) return false;
        float sq = MathF.Sqrt(h);
        near = -b - sq;
        far = -b + sq;
        return true;
    }
}
