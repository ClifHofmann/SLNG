using Xunit;

namespace SLNG.Core.Tests;

// BUG-GRID-01. The point of one path builder: the same account name, and the same region handle, on
// two different grids must never land in the same file or directory.
public sealed class GridDataPathsTests : IDisposable
{
    private const string Agni = "https://login.agni.lindenlab.com/cgi-bin/login.cgi";
    private const string OsGrid = "http://hg.osgrid.org/";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "slng-gridpaths-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void The_same_account_name_on_two_grids_gets_two_background_files()
    {
        var paths = new GridDataPaths(_root);

        string sl = paths.AccountFile(Agni, "Clifton", "Howlett", "last_session_bg", ".png");
        string os = paths.AccountFile(OsGrid, "Clifton", "Howlett", "last_session_bg", ".png");

        Assert.NotEqual(sl, os);
        Assert.EndsWith("/agni/last_session_bg_clifton_howlett.png", sl);
        Assert.EndsWith("/hg.osgrid.org/last_session_bg_clifton_howlett.png", os);
    }

    [Fact]
    public void The_same_region_handle_on_two_grids_gets_two_cache_directories()
    {
        // OSGrid's default region sits at grid 1000,1000 -- also a real Second Life region -- so the
        // handle alone cannot say which grid a cached object belongs to. The directory has to.
        var paths = new GridDataPaths(_root);

        string sl = paths.CacheDirectory(Agni, "objects");
        string os = paths.CacheDirectory(OsGrid, "objects");

        Assert.NotEqual(sl, os);
        Assert.Equal(_root.Replace('\\', '/') + "/agni/cache/objects", sl.Replace('\\', '/'));
        Assert.Equal(_root.Replace('\\', '/') + "/hg.osgrid.org/cache/objects", os.Replace('\\', '/'));
    }

    [Fact]
    public void Two_Linden_grids_do_not_share_a_directory()
    {
        var paths = new GridDataPaths(_root);

        Assert.NotEqual(
            paths.CacheDirectory(Agni, "maptiles"),
            paths.CacheDirectory("https://login.aditi.lindenlab.com/cgi-bin/login.cgi", "maptiles"));
    }

    [Fact]
    public void The_same_grid_written_two_ways_shares_its_directory()
    {
        var paths = new GridDataPaths(_root);

        Assert.Equal(paths.CacheDirectory("http://hg.osgrid.org/", "objects"), paths.CacheDirectory("HG.osgrid.org:80", "objects"));
    }

    [Fact]
    public void Two_accounts_on_one_grid_get_two_directories_and_never_the_cache_directory()
    {
        var paths = new GridDataPaths(_root);

        string a = paths.AccountDirectory(OsGrid, "Clifton", "Howlett");
        string b = paths.AccountDirectory(OsGrid, "Denise1976", "Resident");

        Assert.NotEqual(a, b);
        Assert.NotEqual(paths.CacheDirectory(OsGrid, "cache"), paths.AccountDirectory(OsGrid, "cache", ""));
        Assert.NotEqual(paths.GridDirectory(OsGrid) + "/cache", paths.AccountDirectory(OsGrid, "cache", ""));
    }

    [Fact]
    public void Hostile_names_stay_inside_the_grid_directory()
    {
        var paths = new GridDataPaths(_root);

        string file = paths.AccountFile(OsGrid, "../../x", "a/b", "last_session_bg", ".png");

        string expectedPrefix = paths.GridDirectory(OsGrid) + "/";
        Assert.StartsWith(expectedPrefix, file);
        Assert.DoesNotContain('/', file[expectedPrefix.Length..]);
        Assert.DoesNotContain('\\', file[expectedPrefix.Length..]);
        Assert.DoesNotContain("..", file[expectedPrefix.Length..]);
    }

    [Fact]
    public void A_trailing_separator_on_the_root_makes_no_difference()
    {
        Assert.Equal(
            new GridDataPaths(_root).GridDirectory(OsGrid),
            new GridDataPaths(_root + "/").GridDirectory(OsGrid));
        Assert.Equal(
            new GridDataPaths(_root).GridDirectory(OsGrid),
            new GridDataPaths(_root + "\\").GridDirectory(OsGrid));
    }

    [Fact]
    public void A_virtual_root_keeps_its_scheme()
    {
        Assert.Equal("user://grids/agni", new GridDataPaths("user://grids").GridDirectory(Agni));
    }

    [Fact]
    public void A_cache_kind_must_be_a_plain_name()
    {
        var paths = new GridDataPaths(_root);

        Assert.Throws<ArgumentException>(() => paths.CacheDirectory(Agni, "../objects"));
        Assert.Throws<ArgumentException>(() => paths.CacheDirectory(Agni, ""));
        Assert.Throws<ArgumentException>(() => paths.CacheDirectory(Agni, "Objects"));
        Assert.Throws<ArgumentException>(() => paths.AccountFile(Agni, "a", "b", "x/y", ".png"));
    }

    [Fact]
    public void Building_a_path_touches_nothing_on_disk()
    {
        var paths = new GridDataPaths(_root);

        _ = paths.CacheDirectory(OsGrid, "objects");
        _ = paths.AccountFile(OsGrid, "a", "b", "bg", ".png");
        _ = paths.AccountDirectory(OsGrid, "a", "b");

        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void Existing_cache_directories_are_those_a_grid_actually_has()
    {
        var paths = new GridDataPaths(_root);
        Assert.Empty(paths.ExistingCacheDirectories("objects")); // no root at all

        Directory.CreateDirectory(paths.CacheDirectory(Agni, "objects"));
        Directory.CreateDirectory(paths.CacheDirectory(OsGrid, "objects"));
        Directory.CreateDirectory(paths.CacheDirectory(OsGrid, "maptiles"));
        Directory.CreateDirectory(paths.GridDirectory("http://empty.example/")); // a grid with no cache

        var found = paths.ExistingCacheDirectories("objects").Select(d => d.Replace('\\', '/')).OrderBy(d => d).ToList();

        Assert.Equal(2, found.Count);
        Assert.EndsWith("/agni/cache/objects", found[0]);
        Assert.EndsWith("/hg.osgrid.org/cache/objects", found[1]);
        Assert.Single(paths.ExistingCacheDirectories("maptiles"));
    }
}
