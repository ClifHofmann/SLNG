using System.Net;
using System.Reflection;
using LibreMetaverse;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>
/// BUG-INV-14: after a login that restored the inventory cache, the inventory window showed
/// "My Inventory" with "(empty)" under it, Library fine, and the log said nothing.
///
/// <para><b>The cache cannot do that.</b> Whatever is on disk, the login skeleton has already put every
/// folder under the root, and a restore only adds items below folders the skeleton listed — it never
/// removes a folder and never touches the root's own flag. The first two tests run the whole cycle
/// (a session that starts with no cache, the one that restores what it wrote, the one after that)
/// against a local stand-in for the grid's inventory capability, through LibreMetaverse's real reply
/// parser, and pin that the root is complete in every generation.</para>
///
/// <para><b>A refused request can.</b> LibreMetaverse never reads the HTTP status of a capability
/// POST, so a rate-limited or overloaded service looks, from the call, exactly like an empty
/// folder: an empty list, no exception, no log line. The caller drew it as an empty folder and the
/// panel never asked again. The rest pin that SLNG tells the two apart.</para>
/// </summary>
public class InventoryRootTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"slng-inv-root-{Guid.NewGuid():N}");
    private string InventoryDir => Path.Combine(_dir, "inventory");
    private string NamesDir => Path.Combine(_dir, "displaynames");

    private readonly UUID _agent = UUID.Random();
    private readonly UUID _rootId = UUID.Random();
    private readonly UUID _objectsId = UUID.Random();
    private readonly UUID _landmarksId = UUID.Random();
    private readonly UUID _visitedId = UUID.Random();
    private readonly UUID _libraryId = UUID.Random();
    private readonly UUID _libraryFolderId = UUID.Random();
    private readonly UUID _looseItem = UUID.Random();

    private readonly FakeInventoryCaps _grid;

    public InventoryRootTests()
    {
        _grid = new FakeInventoryCaps(_agent);
        _grid.AddFolder(_rootId, UUID.Zero, "My Inventory", 10);
        _grid.AddFolder(_objectsId, _rootId, "Objects", 5);
        _grid.AddFolder(_landmarksId, _rootId, "Landmarks", 7);
        _grid.AddFolder(_visitedId, _landmarksId, "Visited", 3);
        _grid.AddFolder(_libraryId, UUID.Zero, "Library", 1);
        _grid.AddFolder(_libraryFolderId, _libraryId, "Clothing Library", 2);
        _grid.AddItem(_looseItem, _rootId, "Loose in My Inventory");
        _grid.AddItem(UUID.Random(), _objectsId, "Blatest");
        _grid.AddItem(UUID.Random(), _objectsId, "Another box");
        _grid.AddItem(UUID.Random(), _visitedId, "Home");
        _grid.AddItem(UUID.Random(), _libraryFolderId, "Sample");
    }

    public void Dispose()
    {
        _grid.Dispose();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static GridClient Client(GridSession session) => (GridClient)typeof(GridSession)
        .GetField("_client", BindingFlags.NonPublic | BindingFlags.Instance)!
        .GetValue(session)!;

    private InventoryFolder Folder(UUID id, UUID parent, string name, int version) => new(id)
    {
        ParentUUID = parent,
        Name = name,
        Version = version,
        OwnerID = _agent,
    };

    /// <summary>A session whose folder requests go to <see cref="_grid"/> and which asks twice more at
    /// most, quickly — the real wait between asks is a second and a half.</summary>
    private GridSession NewSession(bool withCache = true)
    {
        var session = new GridSession
        {
            InventoryFetchUriOverride = _grid.Uri,
            InventoryFetchRetryDelay = TimeSpan.FromMilliseconds(10),
        };
        if (withCache) session.UseCacheDirectories(InventoryDir, NamesDir);
        return session;
    }

    /// <summary>The login reply for the folders the grid holds, run through every callback
    /// LibreMetaverse registered for it (see <c>SessionCacheTests.LogIn</c>).</summary>
    private void LogIn(GridSession session, IEnumerable<InventoryFolder>? skeleton = null,
        IEnumerable<InventoryFolder>? librarySkeleton = null)
    {
        var reply = new LoginResponseData
        {
            AgentID = _agent,
            SessionID = UUID.Random(),
            FirstName = "Root",
            LastName = "Tester",
            LookAt = new Vector3(1f, 0f, 0f),
            InventoryRoot = _rootId,
            InventorySkeleton = (skeleton ?? new[]
            {
                Folder(_rootId, UUID.Zero, "My Inventory", 10),
                Folder(_objectsId, _rootId, "Objects", 5),
                Folder(_landmarksId, _rootId, "Landmarks", 7),
                Folder(_visitedId, _landmarksId, "Visited", 3),
            }).ToArray(),
            LibraryRoot = _libraryId,
            LibraryOwner = UUID.Random(),
            LibrarySkeleton = (librarySkeleton ?? new[]
            {
                Folder(_libraryId, UUID.Zero, "Library", 1),
                Folder(_libraryFolderId, _libraryId, "Clothing Library", 2),
            }).ToArray(),
        };

        var callbacks = (NetworkManager.LoginResponseCallback?)typeof(NetworkManager)
            .GetField("OnLoginResponse", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(Client(session).Network);
        Assert.NotNull(callbacks);
        callbacks!(true, false, string.Empty, string.Empty, reply);
    }

    private static async Task<List<string>> RootNames(GridSession session, UUID root)
        => (await session.FetchInventoryChildrenAsync(root.Guid)).Select(e => e.Name).OrderBy(n => n).ToList();

    private static readonly string[] RootContents = { "Landmarks", "Loose in My Inventory", "Objects" };

    // ---- the cache is not what empties the root ----------------------------------------------

    // The sequence from the report: a session that starts with no cache and builds one, the one that
    // restores it, the one after. The root must be the same complete list in all three.
    [Fact]
    public async Task The_root_is_complete_in_every_generation_of_the_cache()
    {
        for (int generation = 1; generation <= 3; generation++)
        {
            using var session = NewSession();
            LogIn(session);
            _grid.Requested.Clear();

            // The store before anybody has asked the grid anything: the skeleton's folders, and — from
            // the second generation on — the items the cache brought back. Never an empty root.
            var store = Client(session).Inventory.Store!;
            Assert.Equal(2, store.GetContents(_rootId).Count);
            Assert.True(session.IsFolderLocal(_objectsId.Guid) == (generation > 1));

            // What the inventory window does when it opens, and the background fill 30 s later.
            Assert.Equal(RootContents, await RootNames(session, _rootId));
            await session.PrefetchInventoryAsync();

            Assert.True(session.IsFolderLocal(_rootId.Guid));
            Assert.True(session.IsFolderLocal(_objectsId.Guid));
            Assert.True(session.IsFolderLocal(_visitedId.Guid));
            Assert.Equal(RootContents, await RootNames(session, _rootId));

            // The cache is for exactly this: a restored generation asks the grid for the root only.
            if (generation > 1) Assert.Equal(new[] { _rootId }, _grid.Requested.Distinct().ToArray());

            session.SaveInventoryCache();
        }
    }

    // The restored folders are served from disk with their items; the root, which no restore ever
    // vouches for, is the one thing fetched.
    [Fact]
    public async Task A_restored_session_serves_its_folders_from_disk_and_fetches_only_the_root()
    {
        using (var first = NewSession())
        {
            LogIn(first);
            await first.FetchInventoryChildrenAsync(_rootId.Guid);
            await first.PrefetchInventoryAsync();
            first.SaveInventoryCache();
        }

        _grid.Requested.Clear();
        using var second = NewSession();
        LogIn(second);

        var objects = await second.FetchInventoryChildrenAsync(_objectsId.Guid);
        Assert.Equal(new[] { "Another box", "Blatest" }, objects.Select(e => e.Name).OrderBy(n => n));
        Assert.Empty(_grid.Requested);

        Assert.Equal(RootContents, await RootNames(second, _rootId));
        Assert.Equal(new[] { _rootId }, _grid.Requested.ToArray());
    }

    // ---- a refused request is not an empty folder ---------------------------------------------

    // Pins the library behaviour the fix works around: a refused POST comes back as an empty list,
    // with no exception, and the folder's flag untouched. If a package bump makes it throw, or report
    // failure some other way, the retry in FetchFolderFromGridAsync may no longer be needed.
    [Fact]
    public async Task LibreMetaverse_reports_a_refused_request_as_an_empty_folder()
    {
        using var session = NewSession(withCache: false);
        LogIn(session);
        _grid.RefuseNext(1);

        var contents = await Client(session).Inventory.RequestFolderContentsAsync(
            new List<InventoryFolder> { new(_rootId) { OwnerID = _agent } },
            _grid.Uri, fetchFolders: true, fetchItems: true, InventorySortOrder.ByName);

        Assert.Empty(contents);
        Assert.True(Client(session).Inventory.Store!.RootNode.NeedsUpdate);
        Assert.Equal(1, _grid.RequestCount);
    }

    // The report itself. Before the fix this returned an empty list and the window drew "(empty)".
    [Fact]
    public async Task A_grid_that_keeps_refusing_leaves_the_root_with_the_folders_the_login_listed()
    {
        using var session = NewSession(withCache: false);
        LogIn(session);
        _grid.RefuseNext(100);

        var names = await RootNames(session, _rootId);

        Assert.Equal(new[] { "Landmarks", "Objects" }, names);     // not empty
        Assert.False(session.IsFolderLocal(_rootId.Guid));         // and not claimed to be complete
        Assert.Equal(3, _grid.RequestCount);                       // asked, and asked again

        // Nothing has been remembered as the truth: when the grid recovers, the next call has all of it.
        _grid.RefuseNext(0);
        Assert.Equal(RootContents, await RootNames(session, _rootId));
        Assert.True(session.IsFolderLocal(_rootId.Guid));
    }

    [Fact]
    public async Task A_request_refused_once_is_answered_by_the_second_ask()
    {
        using var session = NewSession(withCache: false);
        LogIn(session);
        _grid.RefuseNext(1);

        Assert.Equal(RootContents, await RootNames(session, _rootId));
        Assert.True(session.IsFolderLocal(_rootId.Guid));
        Assert.Equal(2, _grid.RequestCount);
    }

    // A grid that answers 200 with no folder in it (the service's way of saying "not this one") is
    // the same story with a different status code.
    [Fact]
    public async Task A_reply_without_the_folder_is_not_an_empty_folder_either()
    {
        using var session = NewSession(withCache: false);
        LogIn(session);
        _grid.AnswerNothing = true;

        Assert.Equal(new[] { "Landmarks", "Objects" }, await RootNames(session, _rootId));
        Assert.False(session.IsFolderLocal(_rootId.Guid));
    }

    // A folder that really is empty is answered, and must still be shown as empty.
    [Fact]
    public async Task A_folder_the_grid_answered_with_nothing_in_it_is_empty()
    {
        var emptyId = UUID.Random();
        _grid.AddFolder(emptyId, _rootId, "Nothing here", 1);

        using var session = NewSession(withCache: false);
        LogIn(session, new[]
        {
            Folder(_rootId, UUID.Zero, "My Inventory", 10),
            Folder(_objectsId, _rootId, "Objects", 5),
            Folder(emptyId, _rootId, "Nothing here", 1),
        });

        Assert.Empty(await session.FetchInventoryChildrenAsync(emptyId.Guid));
        Assert.True(session.IsFolderLocal(emptyId.Guid));
        Assert.Equal(1, _grid.RequestCount);
    }

    // The background fill already treats a folder that stays flagged as one the grid would not fill.
    // The same flag is what the window's fetch now trusts.
    [Fact]
    public async Task The_background_fill_after_a_refusal_completes_the_root()
    {
        using var session = NewSession(withCache: false);
        LogIn(session);
        _grid.RefuseNext(100);
        await RootNames(session, _rootId);

        _grid.RefuseNext(0);
        await session.PrefetchInventoryAsync();

        Assert.True(session.IsFolderLocal(_rootId.Guid));
        Assert.Equal(RootContents, await RootNames(session, _rootId));
    }

    // ---- a restore that leaves no folders under My Inventory is dropped -----------------------

    // A skeleton that lists no folder under My Inventory: whatever the cache restores into the rest of
    // the store, this is not the account it was written for. The restore is taken back — the items
    // go and every folder is flagged for fetching — and the next fetch starts from the skeleton.
    [Fact]
    public async Task A_restore_that_leaves_My_Inventory_without_folders_is_discarded()
    {
        using (var first = NewSession())
        {
            LogIn(first);
            await first.FetchInventoryChildrenAsync(_rootId.Guid);
            await first.FetchInventoryChildrenAsync(_libraryFolderId.Guid);
            first.SaveInventoryCache();
        }

        // Without the self-check this restores the library folder as current, with its item.
        using (var control = NewSession())
        {
            LogIn(control);
            Assert.True(control.IsFolderLocal(_libraryFolderId.Guid));
            Assert.Single(Client(control).Inventory.Store!.GetContents(_libraryFolderId));
        }

        using var second = NewSession();
        LogIn(second, skeleton: new[] { Folder(_rootId, UUID.Zero, "My Inventory", 10) });

        Assert.False(second.IsFolderLocal(_libraryFolderId.Guid));
        Assert.Empty(Client(second).Inventory.Store!.GetContents(_libraryFolderId));
    }

    [Fact]
    public async Task A_restore_into_a_normal_inventory_is_kept()
    {
        using (var first = NewSession())
        {
            LogIn(first);
            await first.FetchInventoryChildrenAsync(_rootId.Guid);
            await first.PrefetchInventoryAsync();
            first.SaveInventoryCache();
        }

        using var second = NewSession();
        LogIn(second);

        Assert.True(second.IsFolderLocal(_objectsId.Guid));
        Assert.Equal(2, Client(second).Inventory.Store!.GetContents(_objectsId).Count);
    }
}
