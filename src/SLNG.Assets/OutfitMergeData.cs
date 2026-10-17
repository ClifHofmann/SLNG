using System;
using System.Collections.Generic;
using System.Numerics;

namespace SLNG.Assets;

// FEAT-PERF-23: the inputs and results of OutfitMerger.Merge.

/// <summary>The vertex streams of one skinned surface, in engine axes and winding, as the renderer hands
/// them to the engine: four influences per vertex (<see cref="Bones"/> are skin SLOTS) and four tangent
/// floats (xyz + sign).</summary>
public sealed record OutfitMeshArrays(
    Vector3[] Positions,
    Vector3[] Normals,
    float[] Tangents,
    Vector2[] UVs,
    int[] Bones,
    float[] Weights,
    int[] Indices)
{
    public int VertexCount => Positions.Length;
}

/// <summary>What happens to one surface of a worn item in the merge.</summary>
public enum OutfitSurfaceRole
{
    /// <summary>Depth-tested (opaque, alpha-cut): concatenated with every other surface of its bucket
    /// that has the same material key.</summary>
    Merge,

    /// <summary>Blended: Godot sorts transparent draws per instance, so it keeps an instance of its own
    /// at the sorting offset the renderer gives it (BUG-RENDER-16/38).</summary>
    Sorted,

    /// <summary>Draws nothing (hidden, or a texture that failed): left out of the geometry.</summary>
    Skip,
}

/// <summary>One surface of a worn item, in its authored order.</summary>
public sealed class OutfitSurfaceInput<TKey> where TKey : notnull
{
    /// <summary>Opaque to the merger; equal keys mean the renderer would build identical materials.</summary>
    public required TKey MaterialKey { get; init; }
    public required OutfitSurfaceRole Role { get; init; }

    /// <summary>For a <see cref="OutfitSurfaceRole.Sorted"/> surface: the instance's sorting offset.</summary>
    public float SortingOffset { get; init; }

    /// <summary>Bones are the ITEM's skin slots, indices into <see cref="OutfitItemInput{TKey}.Slots"/>.</summary>
    public required OutfitMeshArrays Arrays { get; init; }
}

/// <summary>One worn rigged item (prim): its skin slots and its surfaces.</summary>
public sealed class OutfitItemInput<TKey> where TKey : notnull
{
    public required Guid Id { get; init; }

    /// <summary>The stable order the merge keeps, so the same outfit always merges to the same bytes:
    /// the linkset root's id, then the item's own id.</summary>
    public long OrderMajor { get; init; }
    public long OrderMinor { get; init; }

    /// <summary>The key of each of the item's skin slots, in slot order.</summary>
    public required OutfitSlotKey[] Slots { get; init; }

    public required IReadOnlyList<OutfitSurfaceInput<TKey>> Surfaces { get; init; }
}

/// <summary>One bucket (a worn linkset in Phase 1) and the items merged into it.</summary>
public sealed class OutfitBucketInput<TKey> where TKey : notnull
{
    public required long Key { get; init; }
    public required IReadOnlyList<OutfitItemInput<TKey>> Items { get; init; }
}

/// <summary>Where one item surface ended up inside a merged surface.</summary>
public sealed record OutfitItemRange(Guid ItemId, int SurfaceIndex, int FirstVertex, int VertexCount, int FirstIndex, int IndexCount);

/// <summary>One merged surface: every Merge-role surface of the bucket with this material key (or the
/// part of them that fits under the vertex limit), bones remapped to outfit slots.</summary>
public sealed class OutfitMergedSurface<TKey> where TKey : notnull
{
    public required TKey MaterialKey { get; init; }
    public required OutfitMeshArrays Arrays { get; init; }
    public required IReadOnlyList<OutfitItemRange> Ranges { get; init; }
}

/// <summary>One blended surface on its own, bones remapped to outfit slots.</summary>
public sealed class OutfitSortedSurface<TKey> where TKey : notnull
{
    public required Guid ItemId { get; init; }
    public required int SurfaceIndex { get; init; }
    public required TKey MaterialKey { get; init; }
    public required float SortingOffset { get; init; }
    public required OutfitMeshArrays Arrays { get; init; }
}

public sealed class OutfitBucketResult<TKey> where TKey : notnull
{
    public required long Key { get; init; }

    /// <summary>The items that contributed geometry, in merge order.</summary>
    public required IReadOnlyList<Guid> Items { get; init; }

    public required IReadOnlyList<OutfitMergedSurface<TKey>> Surfaces { get; init; }
    public required IReadOnlyList<OutfitSortedSurface<TKey>> Sorted { get; init; }
}

public sealed class OutfitMergeResult<TKey> where TKey : notnull
{
    /// <summary>The slot index of <see cref="NewSlots"/>[0]: the count of the table the merge started from.</summary>
    public required int FirstNewSlot { get; init; }

    /// <summary>Keys the merge needed that the table did not have, in the order they were handed out.
    /// The table is append-only: these go on its end.</summary>
    public required IReadOnlyList<OutfitSlotKey> NewSlots { get; init; }

    /// <summary>Per contributing item: its slot s is outfit slot Remap[s].</summary>
    public required IReadOnlyDictionary<Guid, int[]> Remap { get; init; }

    public required IReadOnlyList<OutfitBucketResult<TKey>> Buckets { get; init; }

    /// <summary>Vertices and indices in all output surfaces, merged and sorted.</summary>
    public int Vertices { get; init; }
    public int Indices { get; init; }

    /// <summary>Input surfaces that drew something, and output surfaces (merged + sorted).</summary>
    public int SurfacesIn { get; init; }
    public int SurfacesOut { get; init; }
}
