using SLNG.Net.ObjectCache;
using Xunit;

namespace SLNG.Net.Tests;

// FEAT-NET-04. The object cache holds what a simulator already sent, so a region visited before can
// be shown from memory (and later disk) instead of streamed again. These pin the parts that decide
// what is trusted: the block layout, the probe verdict, the handshake flags, the eviction.
public class ObjectCacheTests
{
    private static readonly RegionKey Here = new(1000ul, Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly RegionKey There = new(2000ul, Guid.Parse("22222222-2222-2222-2222-222222222222"));

    // FullID(16) LocalID(4) PCode(1) State(1) CRC(4) and some body, as LibreMetaverse reads it.
    private static byte[] Block(uint localId, uint crc, byte pcode = 9, int body = 40)
    {
        var data = new byte[26 + body];
        Guid.NewGuid().TryWriteBytes(data.AsSpan(0, 16));
        BitConverter.GetBytes(localId).CopyTo(data, 16);
        data[20] = pcode;
        data[21] = 0; // state
        BitConverter.GetBytes(crc).CopyTo(data, 22);
        return data;
    }

    // ---- the block layout ----

    [Fact]
    public void A_compressed_block_gives_up_its_local_id_crc_and_pcode()
    {
        Assert.True(CompressedObjectBlock.TryRead(Block(0xAABBCCDD, 0x01020304, pcode: 9), out var head));

        Assert.Equal(0xAABBCCDDu, head.LocalId);
        Assert.Equal(0x01020304u, head.Crc);
        Assert.Equal((byte)9, head.PCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    public void A_block_too_short_to_hold_the_header_is_refused(int length)
    {
        Assert.False(CompressedObjectBlock.TryRead(new byte[length], out _));
    }

    [Fact]
    public void Nothing_to_read_is_refused_rather_than_thrown()
    {
        Assert.False(CompressedObjectBlock.TryRead(null, out _));
    }

    // ---- the probe verdict ----

    [Fact]
    public void A_probe_with_the_crc_we_hold_is_a_hit_and_returns_the_block()
    {
        var store = new ObjectCacheStore();
        var block = Block(5, 77);
        store.Put(Here, new CachedObject(5, 77, 0x10, block));

        var verdict = store.Probe(Here, 5, 77, out var held);

        Assert.Equal(CacheProbe.Hit, verdict);
        Assert.Equal(block, held.Block);
        Assert.Equal(0x10u, held.UpdateFlags);
    }

    [Fact]
    public void A_probe_with_another_crc_is_a_crc_miss_because_the_object_changed()
    {
        var store = new ObjectCacheStore();
        store.Put(Here, new CachedObject(5, 77, 0, Block(5, 77)));

        Assert.Equal(CacheProbe.CrcMiss, store.Probe(Here, 5, 78, out _));
    }

    [Fact]
    public void A_probe_for_an_object_we_never_saw_is_a_total_miss()
    {
        var store = new ObjectCacheStore();
        store.Put(Here, new CachedObject(5, 77, 0, Block(5, 77)));

        Assert.Equal(CacheProbe.TotalMiss, store.Probe(Here, 6, 77, out _));
    }

    [Fact]
    public void The_same_local_id_in_another_region_is_not_the_same_object()
    {
        var store = new ObjectCacheStore();
        store.Put(Here, new CachedObject(5, 77, 0, Block(5, 77)));

        Assert.Equal(CacheProbe.TotalMiss, store.Probe(There, 5, 77, out _));
    }

    [Fact]
    public void A_region_whose_cache_id_changed_is_a_different_region()
    {
        // The simulator's cache id is how it says "what you hold of me is void".
        var store = new ObjectCacheStore();
        store.Put(Here, new CachedObject(5, 77, 0, Block(5, 77)));

        var reset = Here with { CacheId = Guid.NewGuid() };

        Assert.Equal(CacheProbe.TotalMiss, store.Probe(reset, 5, 77, out _));
        Assert.True(store.IsEmpty(reset));
    }

    [Fact]
    public void A_newer_update_replaces_the_entry()
    {
        var store = new ObjectCacheStore();
        store.Put(Here, new CachedObject(5, 77, 0, Block(5, 77)));
        store.Put(Here, new CachedObject(5, 99, 0, Block(5, 99)));

        Assert.Equal(CacheProbe.Hit, store.Probe(Here, 5, 99, out _));
        Assert.Equal(CacheProbe.CrcMiss, store.Probe(Here, 5, 77, out _));
        Assert.Equal(1, store.Count(Here));
    }

    [Fact]
    public void Put_keeps_its_own_copy_of_the_block()
    {
        var store = new ObjectCacheStore();
        var block = Block(5, 77);
        store.Put(Here, new CachedObject(5, 77, 0, block));
        block[30] ^= 0xFF; // whoever handed it in reuses the buffer

        store.Probe(Here, 5, 77, out var held);

        Assert.NotEqual(block[30], held.Block[30]);
    }

    // ---- emptiness decides what the simulator is told ----

    [Fact]
    public void An_unknown_region_is_empty_and_a_filled_one_is_not()
    {
        var store = new ObjectCacheStore();
        Assert.True(store.IsEmpty(Here));

        store.Put(Here, new CachedObject(1, 1, 0, Block(1, 1)));

        Assert.False(store.IsEmpty(Here));
        Assert.True(store.IsEmpty(There));
    }

    [Theory]
    [InlineData(true, 0x7u)]   // culling + "my cache is empty, don't probe" + self appearance: what LibreMetaverse sends
    [InlineData(false, 0x5u)]  // culling + self appearance: probe me
    public void The_handshake_reply_says_whether_the_cache_is_empty(bool empty, uint expected)
    {
        Assert.Equal(expected, ObjectCacheProtocol.HandshakeFlags(empty));
    }

    // ---- eviction: the budget, and whose turn it is ----

    [Fact]
    public void Over_budget_the_region_not_used_for_longest_goes_first()
    {
        var store = new ObjectCacheStore(maxBytes: 3 * 100);
        store.Put(Here, new CachedObject(1, 1, 0, Block(1, 1, body: 74)));   // 100 bytes
        store.Put(There, new CachedObject(1, 1, 0, Block(1, 1, body: 74)));  // 100
        store.Probe(Here, 1, 1, out _);                                      // Here is now the recent one
        var third = new RegionKey(3000ul, Guid.NewGuid());

        store.Put(third, new CachedObject(1, 1, 0, Block(1, 1, body: 74)));  // 100 -> at the limit
        store.Put(third, new CachedObject(2, 2, 0, Block(2, 2, body: 74)));  // over: something must go

        Assert.True(store.IsEmpty(There));
        Assert.False(store.IsEmpty(Here));
        Assert.Equal(2, store.Count(third));
    }

    [Fact]
    public void Clear_empties_everything()
    {
        var store = new ObjectCacheStore();
        store.Put(Here, new CachedObject(1, 1, 0, Block(1, 1)));

        store.Clear();

        Assert.True(store.IsEmpty(Here));
        Assert.Equal(0, store.TotalBytes);
    }

    // ---- asking for what is missing ----

    [Fact]
    public void Misses_are_asked_for_in_messages_of_at_most_255_blocks_each_with_its_type()
    {
        var misses = Enumerable.Range(0, 600)
            .Select(i => new CacheMiss((uint)i, i % 2 == 0 ? CacheMissType.Total : CacheMissType.Crc))
            .ToList();

        var messages = ObjectCacheProtocol.Messages(misses).ToList();

        Assert.Equal(new[] { 255, 255, 90 }, messages.Select(m => m.Count));
        Assert.Equal(misses, messages.SelectMany(m => m));
    }

    [Fact]
    public void No_misses_means_no_message()
    {
        Assert.Empty(ObjectCacheProtocol.Messages(new List<CacheMiss>()));
    }
}

// The bookkeeping the disk side needs from the store.
public class ObjectCacheStoreDiskSupportTests
{
    private static readonly RegionKey Here = new(1000ul, Guid.Parse("11111111-1111-1111-1111-111111111111"));

    private static CachedObject Object(uint localId) => new(localId, localId, 0, new byte[30]);

    [Fact]
    public void A_region_written_to_is_dirty_once_until_it_changes_again()
    {
        var store = new ObjectCacheStore();
        store.Put(Here, Object(1));

        Assert.Equal(new[] { Here }, store.TakeDirty());
        Assert.Empty(store.TakeDirty());

        store.Put(Here, Object(2));
        Assert.Equal(new[] { Here }, store.TakeDirty());
    }

    [Fact]
    public void What_was_loaded_from_disk_is_not_dirty_because_the_file_already_says_so()
    {
        var store = new ObjectCacheStore();

        store.Load(Here, new[] { Object(1), Object(2) });

        Assert.Equal(2, store.Count(Here));
        Assert.Empty(store.TakeDirty());
    }

    [Fact]
    public void A_write_that_did_not_happen_can_put_the_region_back_on_the_list()
    {
        var store = new ObjectCacheStore();
        store.Put(Here, Object(1));
        store.TakeDirty();

        store.MarkDirty(Here);

        Assert.Equal(new[] { Here }, store.TakeDirty());
    }

    [Fact]
    public void Snapshot_holds_everything_the_region_holds()
    {
        var store = new ObjectCacheStore();
        store.Put(Here, Object(1));
        store.Put(Here, Object(2));

        Assert.Equal(new uint[] { 1, 2 }, store.Snapshot(Here).Select(o => o.LocalId).OrderBy(i => i));
        Assert.Empty(store.Snapshot(new RegionKey(9ul, Guid.NewGuid())));
    }

    [Fact]
    public void TryGet_finds_an_object_whatever_its_crc()
    {
        var store = new ObjectCacheStore();
        store.Put(Here, Object(7));

        Assert.True(store.TryGet(Here, 7, out var held));
        Assert.Equal(7u, held.LocalId);
        Assert.False(store.TryGet(Here, 8, out _));
    }
}

// FEAT-NET-04, phase 3. A simulator that does not probe (no Agni region tried so far, while the
// handshake said "cache empty" first
// does) never tells us which objects it would send, so the cache cannot be checked object by object.
// It is used optimistically instead: what is held is shown at once, nearest first, and then every one
// of those objects is asked for again; whatever the simulator does not answer for is gone and is
// taken back out. These pin the ordering and the verdict on "gone".
public class ObjectRestorePlanTests
{
    private static byte[] BlockAt(uint localId, float x, float y, float z)
    {
        var data = new byte[80];
        BitConverter.GetBytes(localId).CopyTo(data, 16);
        data[20] = 9;
        BitConverter.GetBytes(x).CopyTo(data, 40);
        BitConverter.GetBytes(y).CopyTo(data, 44);
        BitConverter.GetBytes(z).CopyTo(data, 48);
        return data;
    }

    private static CachedObject At(uint id, float x, float y, float z) => new(id, id, 0, BlockAt(id, x, y, z));

    [Fact]
    public void A_block_gives_up_its_position()
    {
        Assert.True(CompressedObjectBlock.TryReadPosition(BlockAt(1, 10f, 20f, 30f), out var x, out var y, out var z));
        Assert.Equal((10f, 20f, 30f), (x, y, z));
    }

    [Theory]
    [InlineData(0x20u | 0x100u, true)]   // a child prim with name-values: worn
    [InlineData(0x100u, false)]          // name-values on a root: not an attachment
    [InlineData(0x20u, false)]           // a child of a linkset
    [InlineData(0u, false)]
    public void An_attachment_is_a_child_prim_carrying_name_values(uint flags, bool expected)
    {
        var data = BlockAt(1, 0, 0, 0);
        BitConverter.GetBytes(flags).CopyTo(data, 64);

        Assert.Equal(expected, CompressedObjectBlock.IsAttachment(data));
    }

    [Fact]
    public void Too_little_data_to_tell_is_not_an_attachment()
    {
        Assert.False(CompressedObjectBlock.IsAttachment(new byte[60]));
        Assert.False(CompressedObjectBlock.IsAttachment(null));
    }

    [Fact]
    public void A_block_too_short_for_a_position_has_none()
    {
        Assert.False(CompressedObjectBlock.TryReadPosition(new byte[51], out _, out _, out _));
        Assert.False(CompressedObjectBlock.TryReadPosition(null, out _, out _, out _));
    }

    [Fact]
    public void Objects_come_nearest_first_from_where_the_avatar_stands()
    {
        var objects = new[] { At(1, 200, 200, 25), At(2, 130, 130, 25), At(3, 10, 10, 25), At(4, 128, 140, 25) };

        var ordered = ObjectRestorePlan.NearestFirst(objects, 128f, 128f, 25f);

        Assert.Equal(new uint[] { 2, 4, 1, 3 }, ordered.Select(o => o.LocalId));
    }

    [Fact]
    public void An_object_with_no_readable_position_goes_last_but_is_not_lost()
    {
        var broken = new CachedObject(9, 9, 0, new byte[30]);
        var objects = new[] { broken, At(1, 130, 130, 25) };

        var ordered = ObjectRestorePlan.NearestFirst(objects, 128f, 128f, 25f);

        Assert.Equal(new uint[] { 1, 9 }, ordered.Select(o => o.LocalId));
    }

    [Fact]
    public void What_was_shown_and_never_answered_for_is_gone()
    {
        var shown = new uint[] { 1, 2, 3, 4 };
        var answered = new HashSet<uint> { 2, 4, 99 };

        Assert.Equal(new uint[] { 1, 3 }, ObjectRestorePlan.Unanswered(shown, answered));
    }

    [Fact]
    public void When_everything_is_answered_nothing_is_gone()
    {
        Assert.Empty(ObjectRestorePlan.Unanswered(new uint[] { 1, 2 }, new HashSet<uint> { 1, 2 }));
    }

    // Deleting on silence is only safe once the simulator has stopped answering: one that is still
    // working through a long queue is slow, not silent. Flat for a good while, and never on a timeout.
    [Theory]
    [InlineData(new[] { 100, 400, 900, 900, 900, 900, 900, 900, 900, 900 }, 8, true)]
    [InlineData(new[] { 100, 400, 900, 900, 900, 901, 901, 901, 901, 901 }, 8, false)]
    [InlineData(new[] { 900, 900, 900 }, 8, false)]
    public void Silence_counts_only_after_the_answers_have_stopped_for_long_enough(int[] confirmed, int polls, bool settled)
    {
        Assert.Equal(settled, ObjectRecoveryPlan.HasSettled(confirmed, polls));
    }
}

// The cache reaches into LibreMetaverse in two places no public API offers: the compressed-update
// handler (to build a cached object) and the region handshake (to answer it with the cache's flags).
// Both are looked up by name and shape. If a LibreMetaverse upgrade moves either, the cache must
// notice here, not in a session that quietly stops using it -- or worse, stops connecting.
public class ObjectCacheLibreMetaverseContractTests
{
    [Fact]
    public void The_compressed_update_handler_is_where_the_cache_expects_it()
    {
        using var session = new GridSession();

        Assert.NotNull(session.FindLibreMetaverseCompressedHandler());
    }

    [Fact]
    public void The_region_handshake_can_be_taken_over_with_the_members_the_takeover_sets()
    {
        using var session = new GridSession();

        Assert.True(session.TryTakeOverRegionHandshake());
    }
}
