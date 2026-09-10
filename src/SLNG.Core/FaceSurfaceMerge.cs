using System;

namespace SLNG.Core;

/// <summary>BUG-RENDER-16 / BUG-RENDER-12: decides which of a mesh's submeshes may be committed
/// as ONE renderer surface because they resolve to the same material.
///
/// <para><b>Why this exists.</b> Godot sorts the transparent queue per SURFACE, and — verified
/// against Godot 4.7-stable, <c>render_forward_clustered.h</c>'s
/// <c>SortByReverseDepthAndPriority</c> — the comparator reads exactly two things:</para>
/// <code>
/// return (A->sort.priority == B->sort.priority) ? (A->owner->depth > B->owner->depth)
///                                              : (A->sort.priority &lt; B->sort.priority);
/// </code>
/// <para><c>owner</c> is the geometry INSTANCE, and its depth is assigned once per instance from
/// the instance AABB centre (<c>render_forward_clustered.cpp:961-966</c>). So every surface of one
/// mesh instance compares EQUAL, and <c>SortArray</c> is an unstable introsort (median-of-3):
/// the tied group is permuted according to the surrounding array contents, which change whenever
/// the camera moves. That permutation is the flicker. A mesh emitted as N surfaces hands the
/// sorter N independently-reorderable pieces of what the creator authored as one ordered stream;
/// emitted as one surface there is nothing left to reorder, because triangles WITHIN a surface
/// are drawn in index order and are never sorted.</para>
///
/// <para><b>Why no shader mode can substitute.</b> <c>sort.priority</c> — the only other term —
/// is the material's render priority and is compared BEFORE depth, i.e. it is scene-global: using
/// it to order the faces within one plant would order a near plant's face 0 ahead of a far plant's
/// face 1 and break inter-object sorting outright. Nothing else about a material reaches the
/// comparator, which is why sharing one material object across a mesh's faces cannot fix the
/// order either. BUG-RENDER-16 records the six shader modes that were tried instead.</para>
///
/// <para><b>Why merging is invisible.</b> <see cref="FaceTexture"/> is a
/// <c>readonly record struct</c>, so the equality below is full value equality over every field a
/// material is built from — texture id, both material ids, tint, repeats, offsets, rotation,
/// texgen and fullbright. Two faces that compare equal cannot produce different materials, and
/// the alpha kind is itself a function of that record plus per-texture cached properties, so a
/// merged run cannot straddle two shader kinds either.</para>
/// </summary>
public static class FaceSurfaceMerge
{
    /// <summary>Above this submesh count the run pattern no longer fits
    /// <see cref="Signature"/>'s exact bitmask, so nothing is merged rather than risk a
    /// collision between two different patterns sharing a cache key. SL faces cap at 8 and real
    /// mesh assets carry far fewer submeshes than this.</summary>
    public const int MaxMergeableSubmeshes = 64;

    /// <summary>"No face is individually animated" — pass this as <c>animatedFace</c> when the
    /// object has no <c>llSetTextureAnim</c> block, or when its block drives ALL faces (wire
    /// face 255 / <c>-1</c>), which needs no barrier because every face moves together.</summary>
    public const int NoAnimatedFace = -1;

    /// <summary>Resolves a submesh's SL face record exactly the way the renderer's material build
    /// does, so "will these two submeshes get the same material?" can be answered at MESH-BUILD
    /// time, before any material exists.</summary>
    public static FaceTexture Resolve(FaceTexture[]? faces, in FaceTexture defaultFace, int faceIndex) =>
        (faces != null && faceIndex >= 0 && faceIndex < faces.Length) ? faces[faceIndex] : defaultFace;

    /// <summary>Marks which submeshes begin a new surface.
    /// <paramref name="submeshFaceIndices"/> must list the SL face number of each submesh that
    /// will actually be committed, in authored order — the caller filters out empty submeshes
    /// first, because those produce no surface and must not be allowed to break a run.
    /// <paramref name="animatedFace"/> is the SL face a per-face texture animation drives
    /// (<see cref="NoAnimatedFace"/> for none): that face is never merged with a neighbour, so
    /// animating it cannot drag an identically-textured static face along with it.</summary>
    /// <returns>The resulting surface count.</returns>
    public static int Plan(
        ReadOnlySpan<int> submeshFaceIndices, FaceTexture[]? faces, in FaceTexture defaultFace,
        int animatedFace, Span<bool> runStart)
    {
        if (runStart.Length < submeshFaceIndices.Length)
            throw new ArgumentException("runStart must be at least as long as submeshFaceIndices", nameof(runStart));

        runStart[..submeshFaceIndices.Length].Clear();
        if (submeshFaceIndices.Length == 0) return 0;

        // Only CONSECUTIVE runs merge. Merging scattered matches would interleave triangles the
        // creator ordered deliberately -- the very thing being preserved (BUG-RENDER-12).
        bool mergeable = submeshFaceIndices.Length <= MaxMergeableSubmeshes;

        int surfaces = 0;
        var runFace = default(FaceTexture);
        bool inRun = false;
        bool previousWasAnimated = false;

        for (int i = 0; i < submeshFaceIndices.Length; i++)
        {
            int faceIndex = submeshFaceIndices[i];
            bool isAnimated = animatedFace >= 0 && faceIndex == animatedFace;
            var face = Resolve(faces, defaultFace, faceIndex);

            bool starts = !inRun
                || !mergeable
                || isAnimated
                || previousWasAnimated
                || !face.Equals(runFace);

            if (starts)
            {
                runStart[i] = true;
                surfaces++;
                runFace = face;
                inRun = true;
            }

            previousWasAnimated = isAnimated;
        }

        return surfaces;
    }

    /// <summary>An EXACT identifier of a run pattern (bit <c>i</c> set = submesh <c>i</c> starts a
    /// surface), so a shared mesh built for one pattern is never handed to an instance whose
    /// texturing implies a different one. Exact rather than a hash because a collision would
    /// silently render the wrong geometry; <see cref="MaxMergeableSubmeshes"/> is what keeps it
    /// exact. Within one geometry the submesh count is fixed, so the mask alone identifies the
    /// pattern.</summary>
    public static ulong Signature(ReadOnlySpan<bool> runStart, int count)
    {
        if (count > MaxMergeableSubmeshes)
            throw new ArgumentOutOfRangeException(nameof(count), count, "run pattern does not fit an exact 64-bit mask");

        ulong mask = 0;
        for (int i = 0; i < count; i++)
        {
            if (runStart[i]) mask |= 1UL << i;
        }
        return mask;
    }

    /// <summary>True when the plan merges nothing, i.e. every submesh starts its own surface. The
    /// renderer uses this to keep the un-merged path — and its shared-mesh cache key — bit
    /// identical to what it was before this change.</summary>
    public static bool IsIdentity(ReadOnlySpan<bool> runStart, int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (!runStart[i]) return false;
        }
        return true;
    }
}
