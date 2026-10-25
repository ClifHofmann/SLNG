using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace SLNG.Assets;

/// <summary>
/// FEAT-PERF-23: the pure array work of merging one avatar's worn rigged items, engine-agnostic so it
/// runs on a worker and is tested here. Spec: docs/specs/FEAT-PERF-23-outfit-merge.md.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Slots.</b> One table per avatar, append-only: an item slot whose (bone, inverse bind) key
/// is already in it reuses that slot, a new key goes on the end. Every influence is renumbered through
/// the item's own slot table; an index outside it falls back to the item's slot 0, the same slot
/// <c>RiggedMeshBuilder</c> pins an orphaned vertex to. Weights are copied unchanged.</item>
/// <item><b>Grouping.</b> Merge-role surfaces of one bucket with equal material keys become one
/// surface, cut between parts (never inside one) at the vertex limit. Sorted-role surfaces stay one
/// each. Nothing crosses a bucket.</item>
/// <item><b>Order.</b> Buckets by key, items by (OrderMajor, OrderMinor, Id), surfaces in authored
/// order, groups by first appearance -- the input order does not matter.</item>
/// <item>An item that draws nothing (no surfaces, or all Skip) takes no slots and adds nothing.</item>
/// </list>
/// </remarks>
public static class OutfitMerger
{
    /// <summary>The 16-bit index limit: a merged surface stops growing before it.</summary>
    public const int DefaultMaxVerticesPerSurface = 65_535;

    public static OutfitMergeResult<TKey> Merge<TKey>(
        IReadOnlyList<OutfitBucketInput<TKey>> buckets,
        IReadOnlyList<OutfitSlotKey> existingSlots,
        int maxVerticesPerSurface = DefaultMaxVerticesPerSurface,
        IEqualityComparer<TKey>? comparer = null) where TKey : notnull
    {
        comparer ??= EqualityComparer<TKey>.Default;
        if (maxVerticesPerSurface < 1) maxVerticesPerSurface = 1;

        var slotIndex = new Dictionary<OutfitSlotKey, int>(existingSlots.Count + 64);
        for (int i = 0; i < existingSlots.Count; i++) slotIndex.TryAdd(existingSlots[i], i);
        var newSlots = new List<OutfitSlotKey>();
        var remaps = new Dictionary<Guid, int[]>();

        int SlotOf(OutfitSlotKey key)
        {
            if (slotIndex.TryGetValue(key, out int slot)) return slot;
            slot = existingSlots.Count + newSlots.Count;
            newSlots.Add(key);
            slotIndex[key] = slot;
            return slot;
        }

        var results = new List<OutfitBucketResult<TKey>>(buckets.Count);
        int vertices = 0, indices = 0, surfacesIn = 0, surfacesOut = 0;

        foreach (var bucket in buckets.OrderBy(b => b.Key))
        {
            var items = bucket.Items
                .OrderBy(i => i.OrderMajor).ThenBy(i => i.OrderMinor).ThenBy(i => i.Id)
                .ToList();

            var groups = new List<Group<TKey>>();
            var groupOf = new Dictionary<TKey, Group<TKey>>(comparer);
            var sorted = new List<OutfitSortedSurface<TKey>>();
            var contributors = new List<Guid>();

            foreach (var item in items)
            {
                if (!Contributes(item)) continue;
                var remap = new int[item.Slots.Length];
                for (int s = 0; s < remap.Length; s++) remap[s] = SlotOf(item.Slots[s]);
                remaps[item.Id] = remap;
                contributors.Add(item.Id);

                for (int s = 0; s < item.Surfaces.Count; s++)
                {
                    var surface = item.Surfaces[s];
                    if (surface.Role == OutfitSurfaceRole.Skip || surface.Arrays.VertexCount == 0) continue;
                    surfacesIn++;
                    var part = new Part(item.Id, s, surface.Arrays, remap);
                    if (surface.Role == OutfitSurfaceRole.Sorted)
                    {
                        var arrays = Concat(new List<Part> { part }, out _);
                        sorted.Add(new OutfitSortedSurface<TKey>
                        {
                            ItemId = item.Id,
                            SurfaceIndex = s,
                            MaterialKey = surface.MaterialKey,
                            SortingOffset = surface.SortingOffset,
                            Arrays = arrays,
                        });
                        vertices += arrays.VertexCount;
                        indices += arrays.Indices.Length;
                        surfacesOut++;
                        continue;
                    }
                    if (!groupOf.TryGetValue(surface.MaterialKey, out var group))
                    {
                        group = new Group<TKey>(surface.MaterialKey);
                        groupOf[surface.MaterialKey] = group;
                        groups.Add(group);
                    }
                    group.Parts.Add(part);
                }
            }

            var merged = new List<OutfitMergedSurface<TKey>>();
            foreach (var group in groups)
            {
                // Cut between parts at the limit; a single part over it goes alone, as it was drawn before.
                var chunk = new List<Part>();
                int chunkVertices = 0;
                foreach (var part in group.Parts)
                {
                    int n = part.Arrays.VertexCount;
                    if (chunk.Count > 0 && chunkVertices + n > maxVerticesPerSurface)
                    {
                        merged.Add(Emit(group.Key, chunk));
                        chunk = new List<Part>();
                        chunkVertices = 0;
                    }
                    chunk.Add(part);
                    chunkVertices += n;
                }
                if (chunk.Count > 0) merged.Add(Emit(group.Key, chunk));
            }
            foreach (var surface in merged)
            {
                vertices += surface.Arrays.VertexCount;
                indices += surface.Arrays.Indices.Length;
                surfacesOut++;
            }

            results.Add(new OutfitBucketResult<TKey>
            {
                Key = bucket.Key,
                Items = contributors,
                Surfaces = merged,
                Sorted = sorted,
            });
        }

        return new OutfitMergeResult<TKey>
        {
            FirstNewSlot = existingSlots.Count,
            NewSlots = newSlots,
            Remap = remaps,
            Buckets = results,
            Vertices = vertices,
            Indices = indices,
            SurfacesIn = surfacesIn,
            SurfacesOut = surfacesOut,
        };
    }

    private static bool Contributes<TKey>(OutfitItemInput<TKey> item) where TKey : notnull
    {
        if (item.Slots.Length == 0) return false;
        foreach (var surface in item.Surfaces)
            if (surface.Role != OutfitSurfaceRole.Skip && surface.Arrays.VertexCount > 0) return true;
        return false;
    }

    private static OutfitMergedSurface<TKey> Emit<TKey>(TKey key, List<Part> parts) where TKey : notnull
    {
        var arrays = Concat(parts, out var ranges);
        return new OutfitMergedSurface<TKey> { MaterialKey = key, Arrays = arrays, Ranges = ranges };
    }

    private sealed record Part(Guid ItemId, int SurfaceIndex, OutfitMeshArrays Arrays, int[] Remap);

    private sealed class Group<TKey>(TKey key) where TKey : notnull
    {
        public TKey Key { get; } = key;
        public List<Part> Parts { get; } = new();
    }

    /// <summary>Concatenates the parts: streams copied, bones through each part's remap, indices offset.
    /// A stream a part lacks is filled with its neutral value, so the result is always consistent.</summary>
    private static OutfitMeshArrays Concat(List<Part> parts, out List<OutfitItemRange> ranges)
    {
        int vertexCount = 0, indexCount = 0;
        foreach (var part in parts)
        {
            vertexCount += part.Arrays.VertexCount;
            indexCount += part.Arrays.Indices.Length > 0 ? part.Arrays.Indices.Length : part.Arrays.VertexCount;
        }

        var positions = new Vector3[vertexCount];
        var normals = new Vector3[vertexCount];
        var tangents = new float[vertexCount * 4];
        var uvs = new Vector2[vertexCount];
        var bones = new int[vertexCount * 4];
        var weights = new float[vertexCount * 4];
        var idx = new int[indexCount];
        ranges = new List<OutfitItemRange>(parts.Count);

        int vOff = 0, iOff = 0;
        foreach (var part in parts)
        {
            var a = part.Arrays;
            int n = a.VertexCount;
            var remap = part.Remap;
            int fallback = remap[0];

            a.Positions.CopyTo(positions, vOff);
            if (a.Normals.Length == n) a.Normals.CopyTo(normals, vOff);
            else for (int v = 0; v < n; v++) normals[vOff + v] = Vector3.UnitY;
            if (a.Tangents.Length == n * 4) a.Tangents.CopyTo(tangents, vOff * 4);
            else for (int v = 0; v < n; v++) { tangents[(vOff + v) * 4] = 1f; tangents[(vOff + v) * 4 + 3] = 1f; }
            if (a.UVs.Length == n) a.UVs.CopyTo(uvs, vOff);

            if (a.Bones.Length == n * 4 && a.Weights.Length == n * 4)
            {
                int baseK = vOff * 4;
                for (int k = 0; k < n * 4; k++)
                {
                    int slot = a.Bones[k];
                    bones[baseK + k] = slot >= 0 && slot < remap.Length ? remap[slot] : fallback;
                }
                a.Weights.CopyTo(weights, baseK);
            }
            else
            {
                for (int v = 0; v < n; v++)
                {
                    int b = (vOff + v) * 4;
                    bones[b] = bones[b + 1] = bones[b + 2] = bones[b + 3] = fallback;
                    weights[b] = 1f;
                }
            }

            int partIndices;
            if (a.Indices.Length > 0)
            {
                partIndices = a.Indices.Length;
                for (int k = 0; k < partIndices; k++) idx[iOff + k] = a.Indices[k] + vOff;
            }
            else
            {
                partIndices = n;
                for (int k = 0; k < n; k++) idx[iOff + k] = vOff + k;
            }

            ranges.Add(new OutfitItemRange(part.ItemId, part.SurfaceIndex, vOff, n, iOff, partIndices));
            vOff += n;
            iOff += partIndices;
        }

        return new OutfitMeshArrays(positions, normals, tangents, uvs, bones, weights, idx);
    }
}
