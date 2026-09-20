using System.Collections.Generic;
using System.Numerics;

namespace SLNG.Core;

/// <summary>The bounding box of a whole linked object, measured in its root prim's frame.</summary>
/// <remarks>
/// What the build tools act on is the OBJECT, not its root prim: the reference viewer stretches
/// against <c>LLSelectMgr::getBBoxOfSelection()</c> and draws its handles on that box
/// (llmanipscale.cpp). Measured on the root alone — which is what SLNG did — the handles of a
/// linked object sit somewhere in its middle, on whichever part happens to be the root, with no
/// visible relation to the thing being resized.
///
/// The root's frame is the right one to measure in because it is the frame the simulator resizes
/// in: a linked-set scale multiplies every part's offset from the root, so a box measured here
/// simply scales with the object.
/// </remarks>
public static class LinksetBounds
{
    /// <summary>One prim of the linkset, in the same frame the root is given in.</summary>
    public readonly record struct Part(Vector3 Position, Quaternion Rotation, Vector3 Scale);

    /// <summary>The box around the root and <paramref name="parts"/>, as an offset from the root
    /// and a half-size, both in the root's own frame. A root with no parts measures itself, which
    /// is exactly the old single-prim answer: centre zero, half the scale.</summary>
    public static (Vector3 Centre, Vector3 Half) Measure(
        Vector3 rootPosition, Quaternion rootRotation, Vector3 rootScale, IEnumerable<Part>? parts)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        Accumulate(Vector3.Zero, Quaternion.Identity, rootScale * 0.5f, ref min, ref max);

        if (parts != null)
        {
            foreach (var part in parts)
            {
                var (offset, rotation) = LinksetTransform.ToLocal(
                    part.Position, part.Rotation, rootPosition, rootRotation);
                Accumulate(offset, rotation, part.Scale * 0.5f, ref min, ref max);
            }
        }

        return ((min + max) * 0.5f, (max - min) * 0.5f);
    }

    /// <summary>Grows the box to hold one prim's eight corners. The corners, not the centre and
    /// a radius: a rotated part reaches further along an axis than its own size, and an object
    /// whose box cuts through its own parts is worse than no box at all.</summary>
    private static void Accumulate(Vector3 offset, Quaternion rotation, Vector3 half, ref Vector3 min, ref Vector3 max)
    {
        for (int c = 0; c < 8; c++)
        {
            var corner = new Vector3(
                (c & 1) != 0 ? half.X : -half.X,
                (c & 2) != 0 ? half.Y : -half.Y,
                (c & 4) != 0 ? half.Z : -half.Z);

            var p = offset + Vector3.Transform(corner, rotation);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
    }
}
