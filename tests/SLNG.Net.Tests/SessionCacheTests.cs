using System.Reflection;
using LibreMetaverse;
using SLNG.Core;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>
/// BUG-INV-12: the two per-account caches — FEAT-INV-07's inventory cache and FEAT-UI-31's Display
/// Names — are opened by the login itself.
///
/// <para>Both used to be opened by <c>Boot</c> straight after <c>new GridSession()</c>, before the
/// login: no agent id to key the file by, no skeleton to compare against, and both returned without
/// a word. No cache file was ever read or written. GridSession now opens them from a LibreMetaverse
/// login-response callback, which the library runs after its own AgentManager has taken the agent
/// id and its InventoryManager has built the store from the skeleton.</para>
///
/// <para>So these tests replay a login response through LibreMetaverse's real callback chain rather
/// than calling an open method directly: the order of that chain is the thing under test. A package
/// bump that built the store later, or a GridSession that stopped opening the caches itself, fails
/// here instead of quietly leaving them closed again.</para>
///
/// <para>BUG-INV-13: and a session the grid ends saves them as it ends. The app saves only while it
/// is still connected, which a region restart, a kick or a timeout never is by the time anybody
/// clicks anything.</para>
/// </summary>
public class SessionCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"slng-session-cache-{Guid.NewGuid():N}");
    private string InventoryDir => Path.Combine(_root, "inventory");
    private string NamesDir => Path.Combine(_root, "displaynames");
    private string InventoryFile => Path.Combine(InventoryDir, $"{_agent.Guid:N}.inv.cache");

    private readonly UUID _agent = UUID.Random();
    private readonly UUID _rootId = UUID.Random();
    private readonly UUID _objectsId = UUID.Random();
    private readonly UUID _landmarksId = UUID.Random();
    private readonly UUID _visitedId = UUID.Random();   // a subfolder of Landmarks
    private readonly UUID _libraryId = UUID.Random();

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private GridSession NewSession()
    {
        var session = new GridSession();
        session.UseCacheDirectories(InventoryDir, NamesDir);
        return session;
    }

    private static GridClient Client(GridSession session) => (GridClient)typeof(GridSession)
        .GetField("_client", BindingFlags.NonPublic | BindingFlags.Instance)!
        .GetValue(session)!;

    private static DisplayNameCache NameCache(GridSession session) => (DisplayNameCache)typeof(GridSession)
        .GetField("_displayNameCache", BindingFlags.NonPublic | BindingFlags.Instance)!
        .GetValue(session)!;

    private InventoryFolder Folder(UUID id, UUID parent, string name, int version) => new(id)
    {
        ParentUUID = parent,
        Name = name,
        Version = version,
        OwnerID = _agent,
    };

    /// <summary>What the grid answers to a successful login, played through every callback
    /// LibreMetaverse has registered for it, in registration order — which is exactly what
    /// NetworkManager does with a real reply.</summary>
    private void LogIn(GridSession session, UUID agent, int objectsVersion = 5)
    {
        var reply = new LoginResponseData
        {
            AgentID = agent,
            SessionID = UUID.Random(),
            FirstName = "Cache",
            LastName = "Tester",
            LookAt = new Vector3(1f, 0f, 0f),
            InventoryRoot = _rootId,
            InventorySkeleton = new[]
            {
                Folder(_rootId, UUID.Zero, "My Inventory", 1),
                Folder(_objectsId, _rootId, "Objects", objectsVersion),
                // Version 1 on purpose: it is what a folder LibreMetaverse invents for a missing
                // parent gets, so a cache that left this folder out would have it trusted.
                Folder(_landmarksId, _rootId, "Landmarks", 1),
                Folder(_visitedId, _landmarksId, "Visited", 3),
            },
            LibraryRoot = _libraryId,
            LibraryOwner = UUID.Random(),
            LibrarySkeleton = new[] { Folder(_libraryId, UUID.Zero, "Library", 1) },
        };

        var callbacks = (NetworkManager.LoginResponseCallback?)typeof(NetworkManager)
            .GetField("OnLoginResponse", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(Client(session).Network);
        Assert.NotNull(callbacks);
        callbacks!(true, false, string.Empty, string.Empty, reply);
    }

    private void LogIn(GridSession session, int objectsVersion = 5) => LogIn(session, _agent, objectsVersion);

    /// <summary>A folder's contents arriving this session, the way the FetchInventoryDescendents2
    /// reply leaves them: the items in the store and the folder no longer flagged.</summary>
    private void Fetched(GridSession session, UUID folder, params (UUID Id, string Name)[] items)
    {
        var store = Client(session).Inventory.Store!;
        foreach (var (id, name) in items)
        {
            store.UpdateNodeFor(new InventoryItem(id)
            {
                ParentUUID = folder,
                Name = name,
                OwnerID = _agent,
                AssetType = AssetType.Object,
            });
        }
        store.GetNodeFor(folder).NeedsUpdate = false;
    }

    // The bug itself. OpenInventoryCache creates its directory as soon as it is past its guards, and
    // the directory being absent on disk is how this was found.
    [Fact]
    public void The_login_opens_the_inventory_cache_and_the_session_can_save_it()
    {
        using var session = NewSession();
        Assert.False(Directory.Exists(InventoryDir));

        LogIn(session);
        Assert.True(Directory.Exists(InventoryDir));

        Fetched(session, _objectsId, (UUID.Random(), "Blatest"));
        session.SaveInventoryCache();
        Assert.True(File.Exists(InventoryFile));
    }

    [Fact]
    public async Task A_second_login_serves_what_the_first_one_fetched()
    {
        var itemId = UUID.Random();
        using (var first = NewSession())
        {
            LogIn(first);
            Fetched(first, _objectsId, (itemId, "Blatest"));
            first.SaveInventoryCache();
        }

        using var second = NewSession();
        LogIn(second);

        Assert.True(second.IsFolderLocal(_objectsId.Guid));
        // The session is not connected, so these contents can only have come from the disk.
        var contents = await second.FetchInventoryChildrenAsync(_objectsId.Guid);
        var entry = Assert.Single(contents);
        Assert.Equal(itemId.Guid, entry.Id);
        Assert.Equal("Blatest", entry.Name);
    }

    // What LibreMetaverse would do with the live store as it is: a folder this session never
    // opened still carries the skeleton's current version, so the next login would mark it up to
    // date with nothing in it — and never ask the grid (see InventoryCacheTests).
    [Fact]
    public void A_folder_the_first_session_never_fetched_is_fetched_again_by_the_next()
    {
        using (var first = NewSession())
        {
            LogIn(first);
            Fetched(first, _objectsId, (UUID.Random(), "Blatest"));
            // Landmarks itself never opened; only its subfolder was (a search, the prefetch).
            Fetched(first, _visitedId, (UUID.Random(), "Home"));
            first.SaveInventoryCache();
        }

        using var second = NewSession();
        LogIn(second);   // same versions: nothing changed on the grid

        Assert.True(second.IsFolderLocal(_objectsId.Guid));
        Assert.True(second.IsFolderLocal(_visitedId.Guid));
        Assert.False(second.IsFolderLocal(_landmarksId.Guid));
    }

    // RestoreFromDisk never compares a root's version, so anything cached under a root would come
    // back whether it still exists or not — and a later fetch adds, it does not prune.
    [Fact]
    public void An_item_directly_in_the_root_is_not_brought_back_unchecked()
    {
        var looseItem = UUID.Random();
        using (var first = NewSession())
        {
            LogIn(first);
            Fetched(first, _rootId, (looseItem, "Loose in My Inventory"));
            Fetched(first, _objectsId, (UUID.Random(), "Blatest"));
            first.SaveInventoryCache();
        }

        using var second = NewSession();
        LogIn(second);

        Assert.Null(Client(second).Inventory.Store!.GetNodeOrDefault(looseItem));
        Assert.False(second.IsFolderLocal(_rootId.Guid));
    }

    [Fact]
    public void A_session_that_learned_nothing_leaves_the_previous_cache_alone()
    {
        using (var first = NewSession())
        {
            LogIn(first);
            Fetched(first, _objectsId, (UUID.Random(), "Blatest"));
            first.SaveInventoryCache();
        }
        byte[] before = File.ReadAllBytes(InventoryFile);

        using (var second = NewSession())
        {
            // Objects changed on the grid in between, so the cache supplied nothing, and the
            // session ends before anything was fetched.
            LogIn(second, objectsVersion: 6);
            Assert.False(second.IsFolderLocal(_objectsId.Guid));
            second.SaveInventoryCache();
        }

        Assert.Equal(before, File.ReadAllBytes(InventoryFile));
    }

    [Fact]
    public void The_login_opens_the_display_name_cache()
    {
        var friend = Guid.NewGuid();
        using (var first = NewSession())
        {
            LogIn(first);
            NameCache(first).Set(friend, "Remembered Name", DateTime.UtcNow);
            first.SaveDisplayNameCache();
        }
        Assert.True(File.Exists(Path.Combine(NamesDir, $"{_agent.Guid:N}.names.json")));

        using var second = NewSession();
        LogIn(second);

        Assert.Equal(DisplayNameCache.Freshness.Fresh, NameCache(second).Lookup(friend, DateTime.UtcNow, out var name));
        Assert.Equal("Remembered Name", name);
    }

    /// <summary>LibreMetaverse telling us the connection is gone for a reason that is not our own
    /// logout — the handler it calls from <c>NetworkManager.ShutdownAsync</c>.</summary>
    private static void GridDisconnects(GridSession session, NetworkManager.DisconnectType why) =>
        typeof(GridSession).GetMethod("OnNetworkDisconnected", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(session, new object?[] { null, new DisconnectedEventArgs(why, "test") });

    // BUG-INV-13. A region restart, a kick or a timeout: by the time anybody clicks anything the
    // connection is gone, and the app saves only while it is connected.
    [Fact]
    public void A_session_the_grid_ends_saves_both_caches_before_the_app_hears_of_it()
    {
        var itemId = UUID.Random();
        var friend = Guid.NewGuid();
        using (var first = NewSession())
        {
            LogIn(first);
            Fetched(first, _objectsId, (itemId, "Blatest"));
            NameCache(first).Set(friend, "Remembered Name", DateTime.UtcNow);

            // Checked from inside the handler: the app tears the session down in reaction to this
            // event, so whatever is not on disk by then is lost.
            bool savedWhenTold = false;
            first.SessionEnded += (_, _) => savedWhenTold = File.Exists(InventoryFile);

            GridDisconnects(first, NetworkManager.DisconnectType.NetworkTimeout);

            Assert.True(savedWhenTold);
            Assert.True(File.Exists(Path.Combine(NamesDir, $"{_agent.Guid:N}.names.json")));
        }

        // And it is a cache the next login can use, not merely a file.
        using var second = NewSession();
        LogIn(second);
        Assert.True(second.IsFolderLocal(_objectsId.Guid));
        Assert.Equal(DisplayNameCache.Freshness.Fresh, NameCache(second).Lookup(friend, DateTime.UtcNow, out _));
    }

    // The other way a session ends from the grid's side (BUG-NET-20/22): SLNG gives up on a region
    // whose event queue died and logs out itself. It raises SessionEnded before the logout, while
    // still connected, so the app's own save may or may not get there first.
    [Fact]
    public void A_dead_event_queue_saves_both_caches_as_it_ends_the_session()
    {
        using var session = NewSession();
        LogIn(session);
        Fetched(session, _objectsId, (UUID.Random(), "Blatest"));

        typeof(GridSession).GetMethod("EndSessionOverDeadEventQueue", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(session, new object?[] { "Millenium" });

        Assert.True(File.Exists(InventoryFile));
        Assert.True(File.Exists(Path.Combine(NamesDir, $"{_agent.Guid:N}.names.json")));
    }

    // Both files are keyed by the agent id. Without one there is nothing to key them by, and a file
    // named after the zero id would be shared by every account on the machine.
    [Fact]
    public void A_login_reply_without_an_agent_id_writes_no_cache()
    {
        using var session = NewSession();

        LogIn(session, UUID.Zero);
        session.SaveInventoryCache();
        session.SaveDisplayNameCache();

        Assert.False(Directory.Exists(InventoryDir));
        Assert.False(Directory.Exists(NamesDir));
    }
}
