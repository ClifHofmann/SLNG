using System.Numerics;
using Xunit;

namespace SLNG.Assets.Tests;

// FEAT-PERF-23 Phase 1: the pure array half of the outfit merge. One avatar's worn rigged items are
// concatenated per linkset bucket and material key onto one shared skin; these pin what the renderer
// relies on to make the merged skinning identical to the per-item skins it replaces.
public sealed class OutfitMergerTests
{
    private static readonly Matrix4x4 T1 = Matrix4x4.CreateTranslation(0.1f, 0.2f, 0.3f);
    private static readonly Matrix4x4 T2 = Matrix4x4.CreateRotationZ(0.4f) * Matrix4x4.CreateTranslation(0f, 0.5f, 0f);

    /// <summary>A quad (4 vertices, 2 triangles) at height <paramref name="z"/>, every vertex weighted
    /// 0.75 / 0.25 to item slots <paramref name="slotA"/> / <paramref name="slotB"/>.</summary>
    private static OutfitMeshArrays Quad(float z, int slotA = 0, int slotB = 1)
    {
        var positions = new[] { new Vector3(0, 0, z), new Vector3(1, 0, z), new Vector3(0, 1, z), new Vector3(1, 1, z) };
        var normals = Enumerable.Repeat(Vector3.UnitZ, 4).ToArray();
        var tangents = Enumerable.Range(0, 4).SelectMany(_ => new[] { 1f, 0f, 0f, -1f }).ToArray();
        var uvs = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
        var bones = Enumerable.Range(0, 4).SelectMany(_ => new[] { slotA, slotB, 0, 0 }).ToArray();
        var weights = Enumerable.Range(0, 4).SelectMany(_ => new[] { 0.75f, 0.25f, 0f, 0f }).ToArray();
        return new OutfitMeshArrays(positions, normals, tangents, uvs, bones, weights, new[] { 0, 2, 1, 1, 2, 3 });
    }

    private static OutfitSurfaceInput<string> Surface(string key, OutfitSurfaceRole role, OutfitMeshArrays arrays, float sortingOffset = 0f) =>
        new() { MaterialKey = key, Role = role, SortingOffset = sortingOffset, Arrays = arrays };

    private static OutfitItemInput<string> Item(Guid id, long major, long minor, OutfitSlotKey[] slots,
        params OutfitSurfaceInput<string>[] surfaces) =>
        new() { Id = id, OrderMajor = major, OrderMinor = minor, Slots = slots, Surfaces = surfaces };

    private static OutfitBucketInput<string> Bucket(long key, params OutfitItemInput<string>[] items) => new() { Key = key, Items = items };

    private static OutfitMergeResult<string> Merge(IReadOnlyList<OutfitSlotKey> existing, params OutfitBucketInput<string>[] buckets) =>
        OutfitMerger.Merge(buckets, existing);

    [Fact]
    public void An_identity_inverse_bind_is_the_rest_marker_and_matrices_compare_bit_for_bit()
    {
        Assert.True(OutfitSlotKey.For(3, Matrix4x4.Identity).IsRest);
        Assert.Equal(OutfitSlotKey.Rest(3), OutfitSlotKey.For(3, Matrix4x4.Identity));
        Assert.NotEqual(OutfitSlotKey.Rest(3), OutfitSlotKey.Rest(4));
        Assert.Equal(OutfitSlotKey.For(1, T1), OutfitSlotKey.For(1, T1));
        Assert.NotEqual(OutfitSlotKey.For(1, T1), OutfitSlotKey.For(2, T1));

        // -0 and +0 are == as floats but not the same bits; the engine bind would be the same, the key
        // is still told apart, which costs at most a duplicate slot and never a wrong one.
        var negativeZero = T1 with { M12 = -0f };
        Assert.NotEqual(OutfitSlotKey.For(1, T1), OutfitSlotKey.For(1, negativeZero));
        Assert.Equal(OutfitSlotKey.For(1, T1).GetHashCode(), OutfitSlotKey.For(1, T1 * Matrix4x4.Identity).GetHashCode());
    }

    [Fact]
    public void Equal_bone_and_inverse_bind_share_one_slot_and_every_influence_is_remapped_to_it()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        // A: (bone 0, rest), (bone 1, T1). B: (bone 1, T1), (bone 2, T2). Three distinct keys.
        var itemA = Item(a, 10, 1, new[] { OutfitSlotKey.Rest(0), OutfitSlotKey.For(1, T1) },
            Surface("m", OutfitSurfaceRole.Merge, Quad(0f)));
        var itemB = Item(b, 20, 2, new[] { OutfitSlotKey.For(1, T1), OutfitSlotKey.For(2, T2) },
            Surface("m", OutfitSurfaceRole.Merge, Quad(1f)));

        var result = Merge(Array.Empty<OutfitSlotKey>(), Bucket(10, itemA, itemB));

        Assert.Equal(0, result.FirstNewSlot);
        Assert.Equal(new[] { OutfitSlotKey.Rest(0), OutfitSlotKey.For(1, T1), OutfitSlotKey.For(2, T2) }, result.NewSlots);
        Assert.Equal(new[] { 0, 1 }, result.Remap[a]);
        Assert.Equal(new[] { 1, 2 }, result.Remap[b]);

        var merged = Assert.Single(Assert.Single(result.Buckets).Surfaces);
        // A's quad first (slots 0/1 -> 0/1), then B's (slots 0/1 -> 1/2). The zero-weight influences
        // keep pointing at the item's slot 0, through the remap.
        Assert.Equal(new[] { 0, 1, 0, 0 }, merged.Arrays.Bones[..4]);
        Assert.Equal(new[] { 1, 2, 1, 1 }, merged.Arrays.Bones[16..20]);
        Assert.Equal(Quad(0f).Weights.Concat(Quad(1f).Weights), merged.Arrays.Weights);
    }

    [Fact]
    public void Existing_slots_are_reused_and_new_ones_are_appended_after_them()
    {
        var existing = new[] { OutfitSlotKey.For(2, T2), OutfitSlotKey.Rest(7) };
        var id = Guid.NewGuid();
        var item = Item(id, 1, 1, new[] { OutfitSlotKey.Rest(0), OutfitSlotKey.For(2, T2) },
            Surface("m", OutfitSurfaceRole.Merge, Quad(0f)));

        var result = Merge(existing, Bucket(1, item));

        Assert.Equal(2, result.FirstNewSlot);
        Assert.Equal(new[] { OutfitSlotKey.Rest(0) }, result.NewSlots);
        Assert.Equal(new[] { 2, 0 }, result.Remap[id]);
    }

    [Fact]
    public void An_out_of_range_slot_falls_back_to_the_items_own_first_slot()
    {
        // RiggedMeshBuilder pins orphaned vertices to item slot 0; a stray index must land there too,
        // which in the outfit is wherever the ITEM's slot 0 went -- not outfit slot 0.
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var arrays = Quad(0f, slotA: 5, slotB: -1);
        var result = Merge(Array.Empty<OutfitSlotKey>(),
            Bucket(1,
                Item(first, 1, 1, new[] { OutfitSlotKey.Rest(0) }, Surface("m", OutfitSurfaceRole.Merge, Quad(0f, 0, 0))),
                Item(second, 1, 2, new[] { OutfitSlotKey.Rest(9), OutfitSlotKey.Rest(8) }, Surface("m", OutfitSurfaceRole.Merge, arrays))));

        Assert.Equal(new[] { 1, 2 }, result.Remap[second]);
        var merged = Assert.Single(Assert.Single(result.Buckets).Surfaces);
        for (int v = 4; v < 8; v++)
        {
            Assert.Equal(1, merged.Arrays.Bones[v * 4]);     // slot 5 -> the item's slot 0 -> outfit 1
            Assert.Equal(1, merged.Arrays.Bones[v * 4 + 1]); // slot -1 -> the same
        }
    }

    [Fact]
    public void Surfaces_merge_per_material_key_and_never_across_buckets()
    {
        var slots = new[] { OutfitSlotKey.Rest(0), OutfitSlotKey.Rest(1) };
        var result = Merge(Array.Empty<OutfitSlotKey>(),
            Bucket(1,
                Item(Guid.NewGuid(), 1, 1, slots, Surface("skin", OutfitSurfaceRole.Merge, Quad(0f)), Surface("cloth", OutfitSurfaceRole.Merge, Quad(1f))),
                Item(Guid.NewGuid(), 1, 2, slots, Surface("skin", OutfitSurfaceRole.Merge, Quad(2f)))),
            Bucket(2,
                Item(Guid.NewGuid(), 2, 3, slots, Surface("skin", OutfitSurfaceRole.Merge, Quad(3f)))));

        Assert.Equal(2, result.Buckets.Count);
        var first = result.Buckets.Single(b => b.Key == 1);
        Assert.Equal(new[] { "skin", "cloth" }, first.Surfaces.Select(s => s.MaterialKey));
        Assert.Equal(8, first.Surfaces[0].Arrays.Positions.Length);
        Assert.Equal(4, first.Surfaces[1].Arrays.Positions.Length);
        var second = result.Buckets.Single(b => b.Key == 2);
        Assert.Equal(4, Assert.Single(second.Surfaces).Arrays.Positions.Length);
        // One slot table across all buckets of the avatar.
        Assert.Equal(2, result.NewSlots.Count);
    }

    [Fact]
    public void The_same_outfit_merges_to_the_same_bytes_whatever_order_the_items_come_in()
    {
        var slotsA = new[] { OutfitSlotKey.Rest(0), OutfitSlotKey.For(1, T1) };
        var slotsB = new[] { OutfitSlotKey.For(2, T2), OutfitSlotKey.Rest(0) };
        var idA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000000");
        var idB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000000");
        var a = Item(idA, 5, 7, slotsA, Surface("m", OutfitSurfaceRole.Merge, Quad(0f)), Surface("s", OutfitSurfaceRole.Sorted, Quad(0.5f), 0.03f));
        var b = Item(idB, 5, 3, slotsB, Surface("m", OutfitSurfaceRole.Merge, Quad(1f)));

        var forward = Merge(Array.Empty<OutfitSlotKey>(), Bucket(5, a, b));
        var backward = Merge(Array.Empty<OutfitSlotKey>(), Bucket(5, b, a));

        Assert.Equal(forward.NewSlots, backward.NewSlots);
        var f = Assert.Single(forward.Buckets);
        var r = Assert.Single(backward.Buckets);
        Assert.Equal(new[] { idB, idA }, f.Items); // (major, minor) order: B's minor 3 before A's 7
        Assert.Equal(f.Items, r.Items);
        Assert.Equal(f.Surfaces[0].Arrays.Positions, r.Surfaces[0].Arrays.Positions);
        Assert.Equal(f.Surfaces[0].Arrays.Bones, r.Surfaces[0].Arrays.Bones);
        Assert.Equal(f.Surfaces[0].Arrays.Indices, r.Surfaces[0].Arrays.Indices);
        // B came first, so its slots were handed out first.
        Assert.Equal(OutfitSlotKey.For(2, T2), forward.NewSlots[0]);
    }

    [Fact]
    public void Sorted_surfaces_stay_apart_with_their_offset_and_remapped_bones()
    {
        var id = Guid.NewGuid();
        var result = Merge(new[] { OutfitSlotKey.Rest(4) },
            Bucket(1, Item(id, 1, 1, new[] { OutfitSlotKey.Rest(0), OutfitSlotKey.Rest(4) },
                Surface("opaque", OutfitSurfaceRole.Merge, Quad(0f)),
                Surface("hair", OutfitSurfaceRole.Sorted, Quad(1f), 0.03011f),
                Surface("hair2", OutfitSurfaceRole.Sorted, Quad(2f), 0.03012f))));

        var bucket = Assert.Single(result.Buckets);
        Assert.Equal("opaque", Assert.Single(bucket.Surfaces).MaterialKey);
        Assert.Equal(2, bucket.Sorted.Count);
        Assert.Equal((id, 1, 0.03011f, "hair"), (bucket.Sorted[0].ItemId, bucket.Sorted[0].SurfaceIndex, bucket.Sorted[0].SortingOffset, bucket.Sorted[0].MaterialKey));
        Assert.Equal((id, 2, 0.03012f), (bucket.Sorted[1].ItemId, bucket.Sorted[1].SurfaceIndex, bucket.Sorted[1].SortingOffset));
        // Item slot 0 (rest 0) is new -> outfit 1; item slot 1 (rest 4) is the existing outfit 0.
        Assert.Equal(new[] { 1, 0, 1, 1 }, bucket.Sorted[0].Arrays.Bones[..4]);
        Assert.Equal(Quad(1f).Positions, bucket.Sorted[0].Arrays.Positions);
        Assert.Equal(Quad(1f).Indices, bucket.Sorted[0].Arrays.Indices);
    }

    [Fact]
    public void Each_item_surface_is_found_again_by_its_vertex_and_index_range()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var slots = new[] { OutfitSlotKey.Rest(0), OutfitSlotKey.Rest(1) };
        var result = Merge(Array.Empty<OutfitSlotKey>(),
            Bucket(1,
                Item(a, 1, 1, slots, Surface("m", OutfitSurfaceRole.Merge, Quad(0f)), Surface("x", OutfitSurfaceRole.Merge, Quad(9f)), Surface("m", OutfitSurfaceRole.Merge, Quad(1f))),
                Item(b, 1, 2, slots, Surface("m", OutfitSurfaceRole.Merge, Quad(2f)))));

        var m = Assert.Single(result.Buckets).Surfaces[0];
        Assert.Equal(new[]
        {
            new OutfitItemRange(a, 0, 0, 4, 0, 6),
            new OutfitItemRange(a, 2, 4, 4, 6, 6),
            new OutfitItemRange(b, 0, 8, 4, 12, 6),
        }, m.Ranges);
        // Indices are offset by the vertices before them.
        Assert.Equal(new[] { 4, 6, 5, 5, 6, 7 }, m.Arrays.Indices[6..12]);
        Assert.Equal(new[] { 8, 10, 9, 9, 10, 11 }, m.Arrays.Indices[12..18]);
        Assert.Equal(new Vector3(0, 0, 2f), m.Arrays.Positions[8]);
        Assert.Equal(Quad(0f).Tangents.Concat(Quad(1f).Tangents).Concat(Quad(2f).Tangents), m.Arrays.Tangents);
    }

    [Fact]
    public void An_empty_or_all_hidden_item_contributes_nothing_not_even_slots()
    {
        var hidden = Guid.NewGuid();
        var empty = Guid.NewGuid();
        var result = Merge(Array.Empty<OutfitSlotKey>(),
            Bucket(1,
                Item(hidden, 1, 1, new[] { OutfitSlotKey.Rest(3) }, Surface("m", OutfitSurfaceRole.Skip, Quad(0f))),
                Item(empty, 1, 2, new[] { OutfitSlotKey.Rest(4) })));

        Assert.Empty(result.NewSlots);
        Assert.False(result.Remap.ContainsKey(hidden));
        var bucket = Assert.Single(result.Buckets);
        Assert.Empty(bucket.Surfaces);
        Assert.Empty(bucket.Sorted);
        Assert.Empty(bucket.Items);
        Assert.Equal(0, result.Vertices);
    }

    [Fact]
    public void A_material_group_over_the_vertex_limit_is_cut_between_parts_never_inside_one()
    {
        var slots = new[] { OutfitSlotKey.Rest(0), OutfitSlotKey.Rest(1) };
        var items = Enumerable.Range(0, 5)
            .Select(i => Item(Guid.NewGuid(), 1, i, slots, Surface("m", OutfitSurfaceRole.Merge, Quad(i))))
            .ToArray();

        var result = OutfitMerger.Merge(new[] { Bucket(1, items) }, Array.Empty<OutfitSlotKey>(), maxVerticesPerSurface: 10);

        var surfaces = Assert.Single(result.Buckets).Surfaces;
        Assert.Equal(new[] { 8, 8, 4 }, surfaces.Select(s => s.Arrays.Positions.Length));
        Assert.All(surfaces, s => Assert.Equal("m", s.MaterialKey));
        Assert.Equal(new[] { 0, 2, 1, 1, 2, 3, 4, 6, 5, 5, 6, 7 }, surfaces[1].Arrays.Indices);
    }

    [Fact]
    public void Missing_tangents_get_the_neutral_tangent_and_the_counts_add_up()
    {
        var bare = Quad(0f) with { Tangents = Array.Empty<float>() };
        var result = Merge(Array.Empty<OutfitSlotKey>(),
            Bucket(1, Item(Guid.NewGuid(), 1, 1, new[] { OutfitSlotKey.Rest(0), OutfitSlotKey.Rest(1) },
                Surface("m", OutfitSurfaceRole.Merge, bare), Surface("s", OutfitSurfaceRole.Sorted, Quad(1f)))));

        var m = Assert.Single(result.Buckets).Surfaces[0];
        Assert.Equal(new[] { 1f, 0f, 0f, 1f }, m.Arrays.Tangents[..4]);
        Assert.Equal(8, result.Vertices);
        Assert.Equal(12, result.Indices);
        Assert.Equal(2, result.SurfacesIn);
        Assert.Equal(2, result.SurfacesOut);
    }
}
