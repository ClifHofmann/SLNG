using SLNG.Core;
using SLNG.Net.ObjectCache;
using Xunit;

namespace SLNG.Net.Tests;

// BUG-GRID-01. The object cache is named by region handle plus the cache id the simulator hands out,
// and a save deletes "the same region under another cache id". Second Life and OpenSim place regions
// on the same grid coordinates (OSGrid's default sits at 1000,1000, which is also a real Second Life
// region), so two grids sharing ONE directory take each other's files away. One directory per grid
// is what stops it.
public sealed class ObjectCachePerGridTests : IDisposable
{
    private const string Agni = "https://login.agni.lindenlab.com/cgi-bin/login.cgi";
    private const string OsGrid = "http://hg.osgrid.org/";

    // Grid 1000,1000 as a region handle: (1000 * 256) << 32 | (1000 * 256).
    private const ulong SameHandle = ((1000UL * 256) << 32) | (1000UL * 256);

    private static readonly RegionKey OnSecondLife = new(SameHandle, Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"));
    private static readonly RegionKey OnOsGrid = new(SameHandle, Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"));

    private readonly string _root = Path.Combine(Path.GetTempPath(), "slng-pergrid-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private static CachedObject Object(uint localId) => new(localId, localId * 3, 0, new byte[64]);

    [Fact]
    public void One_directory_per_grid_keeps_both_grids_files_when_the_region_handle_is_the_same()
    {
        var paths = new GridDataPaths(_root);
        var secondLife = new ObjectCacheDisk(paths.CacheDirectory(Agni, "objects"));
        var osGrid = new ObjectCacheDisk(paths.CacheDirectory(OsGrid, "objects"));

        Assert.True(secondLife.TrySave(OnSecondLife, new[] { Object(1), Object(2) }));
        Assert.True(osGrid.TrySave(OnOsGrid, new[] { Object(7) }));

        // Neither save removed the other grid's file...
        Assert.True(secondLife.TryLoad(OnSecondLife, out var sl));
        Assert.Equal(new uint[] { 1, 2 }, sl.Select(o => o.LocalId).OrderBy(i => i));
        Assert.True(osGrid.TryLoad(OnOsGrid, out var os));
        Assert.Equal(new uint[] { 7 }, os.Select(o => o.LocalId));

        // ...and neither grid can be handed the other's objects.
        Assert.False(secondLife.TryLoad(OnOsGrid, out _));
        Assert.False(osGrid.TryLoad(OnSecondLife, out _));
    }

    [Fact]
    public void A_shared_directory_loses_a_file_every_time_the_other_grid_saves_that_region()
    {
        // The mechanism behind the bug, pinned so the reason for the per-grid directories stays visible:
        // saving a region deletes every other generation of the same handle.
        var shared = new ObjectCacheDisk(Path.Combine(_root, "shared"));

        shared.TrySave(OnSecondLife, new[] { Object(1) });
        shared.TrySave(OnOsGrid, new[] { Object(7) });

        Assert.False(shared.TryLoad(OnSecondLife, out _));
        Assert.True(shared.TryLoad(OnOsGrid, out _));
    }

    [Fact]
    public void Clearing_one_grids_cache_leaves_the_other_alone()
    {
        var paths = new GridDataPaths(_root);
        var secondLife = new ObjectCacheDisk(paths.CacheDirectory(Agni, "objects"));
        var osGrid = new ObjectCacheDisk(paths.CacheDirectory(OsGrid, "objects"));
        secondLife.TrySave(OnSecondLife, new[] { Object(1) });
        osGrid.TrySave(OnOsGrid, new[] { Object(7) });

        osGrid.Clear();

        Assert.False(osGrid.TryLoad(OnOsGrid, out _));
        Assert.True(secondLife.TryLoad(OnSecondLife, out _));
    }

    [Fact]
    public void The_size_budget_is_per_grid()
    {
        var paths = new GridDataPaths(_root);
        var busy = new ObjectCacheDisk(paths.CacheDirectory(OsGrid, "objects"), maxBytes: 1);
        var quiet = new ObjectCacheDisk(paths.CacheDirectory(Agni, "objects"));
        quiet.TrySave(OnSecondLife, new[] { Object(1) });

        busy.TrySave(OnOsGrid, new[] { Object(7) }); // over its own budget: it trims itself

        Assert.True(quiet.TryLoad(OnSecondLife, out _)); // not the other grid's files
    }
}
