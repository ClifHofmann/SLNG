using System;
using System.Collections.Generic;
using System.Numerics;

namespace SLNG.Assets;

/// <summary>What <see cref="RiggedMeshBuilder.Build"/> produced, plus the numbers the renderer
/// logs about it.</summary>
public sealed class RiggedMeshGeometry
{
    public List<RiggedSurface> Surfaces { get; } = new();

    /// <summary>How many submeshes the asset had, so a log can say "6 submeshes -> 1 surface".</summary>
    public int SubmeshCount { get; init; }

    public int TotalVertices { get; set; }

    /// <summary>Vertices whose weights don't resolve to any bound joint (all 4 influences reference a
    /// joint outside this mesh's own joint list, or the referenced joint failed to resolve in OUR
    /// skeleton). Those fall back to "pin to skin slot 0" — an ARBITRARY joint (whichever happened
    /// to be first in this mesh's own joint list), potentially anatomically distant from the
    /// vertex's real position. A mesh that's a mix of correctly-weighted and orphaned vertices
    /// stretches between the two — a classic "candy-wrapper" skinning artifact that looks exactly
    /// like an elongated snout/spike.</summary>
    public int OrphanedVertices { get; set; }

    /// <summary>Influences whose joint reference had to be remapped to stay in range (see
    /// RiggedMeshBuilder.AddInfluence). Before that method was fixed to match the viewer these were
    /// DROPPED, and the resulting renormalization snapped affected vertices onto an unrelated bone —
    /// the "hair tears into flat shards" bug. A nonzero count means this mesh is one that relies on
    /// the viewer's clamping behavior.</summary>
    public int RemappedInfluences { get; set; }

    /// <summary>Total weight per skin slot, over every vertex: which bone this mesh is mostly
    /// weighted to, and how much of its total vertex weight lands there. Points straight at a
    /// shape/scale bug on a specific bone (e.g. an unexpectedly huge mHead scale) without having to
    /// guess from bind-pose extent alone, which is meaningless pre-skinning.</summary>
    public float[] SlotWeightSum { get; init; } = Array.Empty<float>();

    /// <summary>Bind-pose extent in SL axes, positions AFTER the bind-shape matrix. The RAW vertex
    /// AABB is meaningless for a sanity check: "giant rig" uploads store vertices in a huge position
    /// domain (±50 m) that the tiny bind-shape scale cancels.</summary>
    public Vector3 BindPoseMin { get; set; } = new(float.MaxValue);
    public Vector3 BindPoseMax { get; set; } = new(float.MinValue);

    /// <summary>Non-finite values replaced on the way out (BUG-RENDER-40), and the first vertex of
    /// each kind -- the same counts MeshArrayGuard.VertexGuard keeps, so the renderer can name the
    /// mesh once.</summary>
    public int BadPositions { get; set; }
    public int FirstBadPosition { get; set; } = -1;
    public int BadNormals { get; set; }
    public int FirstBadNormal { get; set; } = -1;
    public int BadUvs { get; set; }
    public int FirstBadUv { get; set; } = -1;

    public int[] FaceIndices()
    {
        var faces = new int[Surfaces.Count];
        for (int i = 0; i < faces.Length; i++) faces[i] = Surfaces[i].FaceIndex;
        return faces;
    }
}
