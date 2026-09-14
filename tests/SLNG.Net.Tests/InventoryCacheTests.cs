using LibreMetaverse;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>
/// FEAT-INV-07: the inventory cache that lets an unchanged folder be drawn without a CAPS round
/// trip.
///
/// <para>SLNG does not implement the version comparison itself — LibreMetaverse's
/// <c>Inventory.RestoreFromDisk</c> already implements exactly the algorithm the reference viewer
/// uses in <c>LLInventoryModel::loadSkeleton</c> (llinventorymodel.cpp:2929-2946): compare each
/// cached folder's version against the version the login skeleton just reported, mark the
/// mismatches <c>NeedsUpdate</c>, and restore contents only for the folders that match.
/// <c>GridSession.FetchInventoryChildrenAsync</c> then reads <c>NeedsUpdate</c> and skips the
/// network when it is false.</para>
///
/// <para><b>These tests exist because that behaviour is a dependency, not an implementation
/// detail.</b> It was verified against the <i>pinned</i> 3.1.3 package rather than trusted from
/// the newer vendored checkout — this project has been burned before by source that reads right
/// but is unreachable or different in the pinned build. If a package bump changes it, the cache
/// would silently start serving a stale inventory, which is far worse than a slow one. Failing
/// here is how that gets noticed.</para>
/// </summary>
public class InventoryCacheTests : IDisposable
{
    private readonly string _path = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), $"slng-inv-cache-{Guid.NewGuid():N}.bin");

    public void Dispose()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch { /* best effort */ }
    }

    private static InventoryFolder Folder(UUID id, UUID parent, string name, int version, UUID owner)
        => new(id)
        {
            ParentUUID = parent,
            Name = name,
            Version = version,
            OwnerID = owner,
            PreferredType = FolderType.None,
        };

    /// <summary>A store as it looks after a session that actually fetched things: skeleton folders
    /// at known versions, one item inside, and the fetched folders marked clean.</summary>
    private static Inventory PopulatedStore(
        GridClient client, UUID owner, UUID rootId, UUID objectsId, UUID landmarksId, UUID itemId,
        int objectsVersion, int landmarksVersion)
    {
        var store = new Inventory(client, owner)
        {
            RootFolder = Folder(rootId, UUID.Zero, "My Inventory", 1, owner),
        };
        store.UpdateNodeFor(Folder(objectsId, rootId, "Objects", objectsVersion, owner));
        store.UpdateNodeFor(Folder(landmarksId, rootId, "Landmarks", landmarksVersion, owner));
        store.UpdateNodeFor(new InventoryItem(itemId)
        {
            ParentUUID = objectsId,
            Name = "Blatest",
            OwnerID = owner,
            AssetType = AssetType.Object,
        });
        return store;
    }

    // The whole feature in one test: save a session's inventory, come back with a skeleton in which
    // ONE folder's version has moved on, and confirm the unchanged folder is served from the cache
    // while the changed one is marked for refetch.
    [Fact]
    public void An_unchanged_folder_is_restored_and_a_changed_one_is_marked_for_refetch()
    {
        var client = new GridClient();
        var owner = UUID.Random();
        UUID rootId = UUID.Random(), objectsId = UUID.Random(), landmarksId = UUID.Random(), itemId = UUID.Random();

        // Session 1: Objects is at version 5, Landmarks at 7; both were fetched.
        var first = PopulatedStore(client, owner, rootId, objectsId, landmarksId, itemId, 5, 7);
        first.GetNodeFor(objectsId).NeedsUpdate = false;
        first.GetNodeFor(landmarksId).NeedsUpdate = false;
        first.SaveToDisk(_path);

        Assert.True(new FileInfo(_path).Length > 0);

        // Session 2: the login skeleton says Objects is still 5, but Landmarks has moved to 8.
        var second = new Inventory(client, owner)
        {
            RootFolder = Folder(rootId, UUID.Zero, "My Inventory", 1, owner),
        };
        second.UpdateNodeFor(Folder(objectsId, rootId, "Objects", 5, owner));
        second.UpdateNodeFor(Folder(landmarksId, rootId, "Landmarks", 8, owner));

        second.RestoreFromDisk(_path);

        // Unchanged -> trustworthy, and its contents came back. This is the round trip SLNG skips.
        Assert.False(second.GetNodeOrDefault(objectsId)!.NeedsUpdate);
        var restored = second.GetContents(objectsId);
        Assert.Single(restored);
        Assert.Equal("Blatest", restored[0].Name);

        // Changed -> must be refetched, and its stale contents must NOT have been restored.
        Assert.True(second.GetNodeOrDefault(landmarksId)!.NeedsUpdate);
        Assert.Empty(second.GetContents(landmarksId));
    }

    // A folder the account deleted between sessions is in the cache but not in the new skeleton.
    // It must not come back from the dead.
    [Fact]
    public void A_folder_missing_from_the_new_skeleton_is_not_restored()
    {
        var client = new GridClient();
        var owner = UUID.Random();
        UUID rootId = UUID.Random(), objectsId = UUID.Random(), landmarksId = UUID.Random(), itemId = UUID.Random();

        var first = PopulatedStore(client, owner, rootId, objectsId, landmarksId, itemId, 5, 7);
        first.GetNodeFor(objectsId).NeedsUpdate = false;
        first.SaveToDisk(_path);

        // New skeleton: Objects is gone.
        var second = new Inventory(client, owner)
        {
            RootFolder = Folder(rootId, UUID.Zero, "My Inventory", 1, owner),
        };
        second.UpdateNodeFor(Folder(landmarksId, rootId, "Landmarks", 7, owner));

        second.RestoreFromDisk(_path);

        Assert.Null(second.GetNodeOrDefault(objectsId));
        Assert.Null(second.GetNodeOrDefault(itemId));
    }

    // A folder that was never fetched is NeedsUpdate by default, which is what makes the whole
    // scheme safe: SLNG only skips the network when something actively cleared the flag.
    [Fact]
    public void A_skeleton_folder_starts_out_needing_an_update()
    {
        var client = new GridClient();
        var owner = UUID.Random();
        UUID rootId = UUID.Random(), objectsId = UUID.Random();

        var store = new Inventory(client, owner)
        {
            RootFolder = Folder(rootId, UUID.Zero, "My Inventory", 1, owner),
        };
        store.UpdateNodeFor(Folder(objectsId, rootId, "Objects", 5, owner));

        Assert.True(store.GetNodeOrDefault(objectsId)!.NeedsUpdate);
    }

    [Fact]
    public void A_corrupt_cache_file_is_refused_rather_than_half_loaded()
    {
        File.WriteAllBytes(_path, new byte[] { 0x42, 0x41, 0x44, 0x00, 0x01, 0x02, 0x03 });

        var client = new GridClient();
        var owner = UUID.Random();
        var rootId = UUID.Random();
        var store = new Inventory(client, owner)
        {
            RootFolder = Folder(rootId, UUID.Zero, "My Inventory", 1, owner),
        };

        // -1 is LibreMetaverse's "could not read it"; GridSession.OpenInventoryCache logs that and
        // carries on fetching, which is the pre-cache behaviour.
        Assert.Equal(-1, store.RestoreFromDisk(_path));
    }

    [Fact]
    public void A_missing_cache_file_is_refused_rather_than_throwing()
    {
        var client = new GridClient();
        var owner = UUID.Random();
        var rootId = UUID.Random();
        var store = new Inventory(client, owner)
        {
            RootFolder = Folder(rootId, UUID.Zero, "My Inventory", 1, owner),
        };

        Assert.Equal(-1, store.RestoreFromDisk(_path));
    }
}
