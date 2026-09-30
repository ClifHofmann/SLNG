using SLNG.Net.ObjectCache;
using Xunit;

namespace SLNG.Net.Tests;

// FEAT-NET-04, phase 2. A cache file is read back on the next login, long after whatever wrote it,
// by a build that may be newer. It must round-trip exactly and refuse anything that is not exactly
// what it claims to be: a wrong region, a half-written file, a flipped bit. A refused file is an
// empty cache, never an exception and never a wrong object.
public class ObjectCacheFileTests
{
    private static readonly RegionKey Key = new(741070837455616ul, Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));

    private static CachedObject Object(uint localId, uint crc, int length)
    {
        var block = new byte[length];
        new Random((int)localId).NextBytes(block);
        return new CachedObject(localId, crc, 0x1234, block);
    }

    private static byte[] Written(params CachedObject[] objects)
    {
        using var stream = new MemoryStream();
        ObjectCacheFile.Write(stream, Key, objects, new DateTimeOffset(2026, 9, 30, 19, 0, 0, TimeSpan.Zero));
        return stream.ToArray();
    }

    private static bool TryRead(byte[] bytes, RegionKey key, out List<CachedObject> objects)
    {
        using var stream = new MemoryStream(bytes);
        return ObjectCacheFile.TryRead(stream, key, out objects);
    }

    [Fact]
    public void What_was_written_reads_back_exactly()
    {
        var objects = new[] { Object(1, 11, 40), Object(2, 22, 300), Object(900000, 33, 26) };

        Assert.True(TryRead(Written(objects), Key, out var back));

        Assert.Equal(objects.Length, back.Count);
        for (int i = 0; i < objects.Length; i++)
        {
            Assert.Equal(objects[i].LocalId, back[i].LocalId);
            Assert.Equal(objects[i].Crc, back[i].Crc);
            Assert.Equal(objects[i].UpdateFlags, back[i].UpdateFlags);
            Assert.Equal(objects[i].Block, back[i].Block);
        }
    }

    [Fact]
    public void An_empty_cache_is_a_valid_file()
    {
        Assert.True(TryRead(Written(), Key, out var back));
        Assert.Empty(back);
    }

    [Fact]
    public void A_file_for_another_region_is_refused()
    {
        var bytes = Written(Object(1, 11, 40));

        Assert.False(TryRead(bytes, Key with { Handle = Key.Handle + 1 }, out var back));
        Assert.Empty(back);
    }

    [Fact]
    public void A_file_for_the_same_region_under_another_cache_id_is_refused()
    {
        // The simulator changed the id: what the file holds is void.
        var bytes = Written(Object(1, 11, 40));

        Assert.False(TryRead(bytes, Key with { CacheId = Guid.NewGuid() }, out _));
    }

    [Fact]
    public void A_truncated_file_is_refused_at_any_length()
    {
        var bytes = Written(Object(1, 11, 40), Object(2, 22, 60));

        for (int length = 0; length < bytes.Length; length++)
            Assert.False(TryRead(bytes[..length], Key, out _), $"accepted a file cut to {length} of {bytes.Length} bytes");
    }

    [Fact]
    public void A_flipped_bit_anywhere_is_refused()
    {
        var bytes = Written(Object(1, 11, 40), Object(2, 22, 60));

        for (int i = 0; i < bytes.Length; i++)
        {
            var damaged = (byte[])bytes.Clone();
            damaged[i] ^= 0x01;
            Assert.False(TryRead(damaged, Key, out _), $"accepted a file with byte {i} damaged");
        }
    }

    [Fact]
    public void Something_that_is_not_a_cache_file_at_all_is_refused()
    {
        Assert.False(TryRead(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }, Key, out _));
        Assert.False(TryRead(System.Text.Encoding.ASCII.GetBytes("<html>not a cache</html>"), Key, out _));
    }

    [Fact]
    public void A_file_claiming_an_absurd_number_of_objects_is_refused_before_it_allocates_anything()
    {
        var bytes = Written(Object(1, 11, 40));
        // The count sits after magic(8) version(4) handle(8) cacheId(16) saved(8) = offset 44.
        BitConverter.GetBytes(int.MaxValue).CopyTo(bytes, 44);

        Assert.False(TryRead(bytes, Key, out _));
    }

    [Fact]
    public void The_time_it_was_saved_can_be_read_without_reading_the_objects()
    {
        using var stream = new MemoryStream(Written(Object(1, 11, 40)));

        Assert.True(ObjectCacheFile.TryReadSaved(stream, out var saved));
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 19, 0, 0, TimeSpan.Zero), saved);
    }

    // ---- the file name: one file per region, named by what identifies it ----

    [Fact]
    public void The_file_name_carries_the_handle_and_the_cache_id()
    {
        Assert.Equal("00000000a8d40000-aaaaaaaabbbbccccddddeeeeeeeeeeee".Length + ObjectCacheFile.Extension.Length,
            ObjectCacheFile.FileName(Key with { Handle = 0xa8d40000 }).Length);
        Assert.EndsWith(ObjectCacheFile.Extension, ObjectCacheFile.FileName(Key));
        Assert.Contains("aaaaaaaabbbbccccddddeeeeeeeeeeee", ObjectCacheFile.FileName(Key));
    }

    [Fact]
    public void Two_regions_never_share_a_file_name()
    {
        Assert.NotEqual(ObjectCacheFile.FileName(Key), ObjectCacheFile.FileName(Key with { Handle = Key.Handle + 1 }));
        Assert.NotEqual(ObjectCacheFile.FileName(Key), ObjectCacheFile.FileName(Key with { CacheId = Guid.NewGuid() }));
    }
}

// The directory the files live in: one per region, swapped in whole, inside a budget.
public sealed class ObjectCacheDiskTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "slng-objcache-" + Guid.NewGuid().ToString("N"));

    private static readonly RegionKey Here = new(1000ul, Guid.Parse("11111111-1111-1111-1111-111111111111"));

    private static CachedObject Object(uint localId, int length = 100) => new(localId, localId * 3, 0, new byte[length]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void A_saved_region_loads_again()
    {
        var disk = new ObjectCacheDisk(_dir);

        Assert.True(disk.TrySave(Here, new[] { Object(1), Object(2) }));

        Assert.True(disk.TryLoad(Here, out var back));
        Assert.Equal(new uint[] { 1, 2 }, back.Select(o => o.LocalId).OrderBy(i => i));
    }

    [Fact]
    public void A_region_never_saved_loads_nothing()
    {
        Assert.False(new ObjectCacheDisk(_dir).TryLoad(Here, out var back));
        Assert.Empty(back);
    }

    [Fact]
    public void Saving_again_replaces_the_file()
    {
        var disk = new ObjectCacheDisk(_dir);
        disk.TrySave(Here, new[] { Object(1), Object(2) });

        disk.TrySave(Here, new[] { Object(3) });

        Assert.True(disk.TryLoad(Here, out var back));
        Assert.Equal(new uint[] { 3 }, back.Select(o => o.LocalId));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp")); // the swap leaves nothing behind
    }

    [Fact]
    public void A_file_that_does_not_read_back_whole_is_deleted_so_it_is_not_tried_again()
    {
        var disk = new ObjectCacheDisk(_dir);
        disk.TrySave(Here, new[] { Object(1) });
        var path = Directory.GetFiles(_dir, "*" + ObjectCacheFile.Extension).Single();
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });

        Assert.False(disk.TryLoad(Here, out _));

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void A_region_reset_by_its_simulator_loses_the_file_of_its_old_cache_id()
    {
        var disk = new ObjectCacheDisk(_dir);
        disk.TrySave(Here, new[] { Object(1) });
        var reset = Here with { CacheId = Guid.NewGuid() };

        disk.TrySave(reset, new[] { Object(2) });

        Assert.False(disk.TryLoad(Here, out _));
        Assert.True(disk.TryLoad(reset, out _));
        Assert.Single(Directory.GetFiles(_dir, "*" + ObjectCacheFile.Extension));
    }

    [Fact]
    public void Over_its_budget_the_region_written_longest_ago_goes_first()
    {
        var disk = new ObjectCacheDisk(_dir, maxBytes: 2500);
        var older = new RegionKey(1ul, Guid.NewGuid());
        var middle = new RegionKey(2ul, Guid.NewGuid());
        var newest = new RegionKey(3ul, Guid.NewGuid());
        disk.TrySave(older, new[] { Object(1, 1000) });
        File.SetLastWriteTimeUtc(Directory.GetFiles(_dir, "*" + ObjectCacheFile.Extension).Single(), DateTime.UtcNow.AddHours(-2));
        disk.TrySave(middle, new[] { Object(1, 1000) });

        disk.TrySave(newest, new[] { Object(1, 1000) }); // three files of ~1 KB do not fit in 2500 bytes

        Assert.False(disk.TryLoad(older, out _));
        Assert.True(disk.TryLoad(middle, out _));
        Assert.True(disk.TryLoad(newest, out _));
        Assert.True(disk.TotalBytes() <= 2500);
    }

    [Fact]
    public void Clear_removes_every_file()
    {
        var disk = new ObjectCacheDisk(_dir);
        disk.TrySave(Here, new[] { Object(1) });
        disk.TrySave(new RegionKey(2ul, Guid.NewGuid()), new[] { Object(1) });

        disk.Clear();

        Assert.Equal(0, disk.TotalBytes());
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void A_directory_that_cannot_be_created_is_a_failed_save_not_an_exception()
    {
        var blocked = Path.Combine(_dir, "file");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(blocked, "a file where the directory should be");

        Assert.False(new ObjectCacheDisk(Path.Combine(blocked, "sub")).TrySave(Here, new[] { Object(1) }));
    }
}

// The "clear cache" button in the preferences: one button, both caches.
public sealed class ObjectCacheClearTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "slng-objclear-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void Clearing_removes_the_cache_files_and_leaves_everything_else_alone()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "0000000000000001-aaaaaaaabbbbccccddddeeeeeeeeeeee" + ObjectCacheFile.Extension), "x");
        File.WriteAllText(Path.Combine(_dir, "0000000000000002-aaaaaaaabbbbccccddddeeeeeeeeeeee" + ObjectCacheFile.Extension + ".tmp"), "x");
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "not ours");
        using var session = new GridSession();
        session.UseObjectCacheDirectory(_dir);

        session.ClearObjectCache();

        Assert.Equal(new[] { "notes.txt" }, Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void Clearing_with_no_directory_set_is_harmless()
    {
        using var session = new GridSession();

        session.ClearObjectCache();
    }
}
