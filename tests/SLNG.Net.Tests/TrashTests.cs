using LibreMetaverse;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>
/// FEAT-INV-10: emptying the Trash, deleting one thing in it for good, and taking one thing back
/// out. These are the only inventory actions with no way back, so the decisions behind them are
/// pinned here over a real LibreMetaverse store rather than trusted from reading the code.
/// </summary>
public class TrashTests
{
    private readonly UUID _owner = UUID.Random();
    private readonly UUID _root = UUID.Random();
    private readonly UUID _trash = UUID.Random();
    private readonly UUID _objects = UUID.Random();
    private readonly UUID _clothing = UUID.Random();
    private readonly UUID _photos = UUID.Random();
    private readonly Inventory _store;

    public TrashTests()
    {
        _store = new Inventory(new GridClient(), _owner)
        {
            RootFolder = Folder(_root, UUID.Zero, "My Inventory", FolderType.Root),
        };
        _store.UpdateNodeFor(Folder(_trash, _root, "Trash", FolderType.Trash));
        _store.UpdateNodeFor(Folder(_objects, _root, "Objects", FolderType.Object));
        _store.UpdateNodeFor(Folder(_clothing, _root, "Clothing", FolderType.Clothing));
        _store.UpdateNodeFor(Folder(_photos, _root, "Photo Album", FolderType.Snapshot));
    }

    private InventoryFolder Folder(UUID id, UUID parent, string name, FolderType type = FolderType.None)
        => new(id) { ParentUUID = parent, Name = name, OwnerID = _owner, PreferredType = type };

    private UUID AddFolder(UUID parent, string name, bool fetched = true)
    {
        var id = UUID.Random();
        _store.UpdateNodeFor(Folder(id, parent, name));
        _store.GetNodeFor(id).NeedsUpdate = !fetched;
        return id;
    }

    private UUID AddItem(UUID parent, string name,
        AssetType type = AssetType.Object, InventoryType inv = InventoryType.Object, UUID linkTo = default)
    {
        var id = UUID.Random();
        _store.UpdateNodeFor(new InventoryItem(id)
        {
            ParentUUID = parent,
            Name = name,
            OwnerID = _owner,
            AssetType = type,
            InventoryType = inv,
            AssetUUID = linkTo,
        });
        return id;
    }

    private void TrashFetched() => _store.GetNodeFor(_trash).NeedsUpdate = false;

    // ---- Which folder is the Trash ------------------------------------------------------------

    [Fact]
    public void The_trash_is_only_the_trash_when_the_folder_itself_says_so()
    {
        Assert.Equal(_trash, GridSession.VerifiedTrashFolder(_store, _trash));

        // LibreMetaverse's FindFolderForType answers "no Trash folder" with the ROOT. Taking that at
        // its word, "empty the Trash" purges the descendants of the root -- the whole inventory.
        Assert.Equal(UUID.Zero, GridSession.VerifiedTrashFolder(_store, _root));
        Assert.Equal(UUID.Zero, GridSession.VerifiedTrashFolder(_store, _objects));
        Assert.Equal(UUID.Zero, GridSession.VerifiedTrashFolder(_store, UUID.Random()));
        Assert.Equal(UUID.Zero, GridSession.VerifiedTrashFolder(_store, UUID.Zero));
        Assert.Equal(UUID.Zero, GridSession.VerifiedTrashFolder(null, _trash));
    }

    [Fact]
    public void Inside_the_trash_means_below_it_at_any_depth_but_not_the_trash_itself()
    {
        var sub = AddFolder(_trash, "Old stuff");
        var deep = AddItem(sub, "Old hat");
        var direct = AddItem(_trash, "Old shoe");
        var elsewhere = AddItem(_objects, "Keeper");

        Assert.True(GridSession.IsBelow(_store, direct, _trash));
        Assert.True(GridSession.IsBelow(_store, sub, _trash));
        Assert.True(GridSession.IsBelow(_store, deep, _trash));

        Assert.False(GridSession.IsBelow(_store, _trash, _trash));
        Assert.False(GridSession.IsBelow(_store, elsewhere, _trash));
        Assert.False(GridSession.IsBelow(_store, _objects, _trash));
        Assert.False(GridSession.IsBelow(_store, UUID.Random(), _trash));
        Assert.False(GridSession.IsBelow(null, direct, _trash));
    }

    // ---- What is in it ------------------------------------------------------------------------

    [Fact]
    public void The_summary_counts_everything_below_the_trash_and_nothing_outside_it()
    {
        TrashFetched();
        var sub = AddFolder(_trash, "Old stuff");
        AddItem(sub, "Old hat");
        AddItem(sub, "Old coat");
        AddItem(_trash, "Old shoe");
        AddFolder(sub, "Empty");
        AddItem(_objects, "Keeper");

        var s = GridSession.SummarizeBelow(_store, _trash, new HashSet<Guid>());

        Assert.Equal(3, s.Items);
        Assert.Equal(2, s.Folders);
        Assert.True(s.Complete);
        Assert.Empty(s.WornNames);
        Assert.False(s.IsEmpty);
    }

    [Fact]
    public void A_folder_never_fetched_makes_the_count_a_lower_bound()
    {
        TrashFetched();
        AddItem(_trash, "Old shoe");
        AddFolder(_trash, "Never opened", fetched: false);

        var s = GridSession.SummarizeBelow(_store, _trash, new HashSet<Guid>());

        Assert.Equal(1, s.Items);
        Assert.Equal(1, s.Folders);
        Assert.False(s.Complete);
    }

    [Fact]
    public void An_unfetched_trash_is_not_known_to_be_empty()
    {
        // The Trash node as the login skeleton leaves it: present, contents never asked for.
        var s = GridSession.SummarizeBelow(_store, _trash, new HashSet<Guid>());

        Assert.True(s.IsEmpty);
        Assert.False(s.Complete);
    }

    [Fact]
    public void Something_worn_below_the_trash_is_named_and_worn_things_elsewhere_are_not()
    {
        TrashFetched();
        var sub = AddFolder(_trash, "Old stuff");
        var hat = AddItem(sub, "Old hat");
        AddItem(_trash, "Old shoe");
        var keeper = AddItem(_objects, "Keeper");

        var s = GridSession.SummarizeBelow(_store, _trash, new HashSet<Guid> { hat.Guid, keeper.Guid });

        Assert.Equal(new[] { "Old hat" }, s.WornNames);
    }

    [Fact]
    public void Worn_below_answers_for_an_item_and_for_a_folder()
    {
        var sub = AddFolder(_trash, "Old stuff");
        var hat = AddItem(sub, "Old hat");
        var shoe = AddItem(_trash, "Old shoe");
        var worn = new HashSet<Guid> { hat.Guid };

        Assert.Equal(new[] { "Old hat" }, GridSession.WornBelow(_store, hat, worn));
        Assert.Equal(new[] { "Old hat" }, GridSession.WornBelow(_store, sub, worn));
        Assert.Empty(GridSession.WornBelow(_store, shoe, worn));
        Assert.Empty(GridSession.WornBelow(_store, UUID.Random(), worn));
    }

    // ---- Emptying it ----------------------------------------------------------------------------

    [Fact]
    public void Forgetting_the_contents_keeps_the_trash_itself()
    {
        TrashFetched();
        var sub = AddFolder(_trash, "Old stuff");
        var deep = AddItem(sub, "Old hat");
        var direct = AddItem(_trash, "Old shoe");
        var keeper = AddItem(_objects, "Keeper");

        int dropped = GridSession.ForgetContents(_store, _trash);

        Assert.Equal(2, dropped);
        Assert.False(_store.Contains(sub));
        Assert.False(_store.Contains(deep));
        Assert.False(_store.Contains(direct));
        Assert.Empty(_store.GetContents(_trash));
        Assert.True(_store.Contains(keeper));

        // The point of the whole helper. LibreMetaverse's own purge drops the folder it purged along
        // with its contents; with the Trash gone from the store, FindFolderForType(Trash) falls back
        // to the root and the next Delete moves things to the top of the inventory.
        Assert.True(_store.Contains(_trash));
        Assert.Equal(_trash, GridSession.VerifiedTrashFolder(_store, _trash));
        Assert.Equal(_trash, GridSession.FindSystemFolder(_store, (int)FolderType.Trash));

        var after = GridSession.SummarizeBelow(_store, _trash, new HashSet<Guid>());
        Assert.True(after.IsEmpty);
        Assert.True(after.Complete);
    }

    // ---- Taking something back out --------------------------------------------------------------

    [Theory]
    [InlineData(AssetType.Object, InventoryType.Object, FolderType.Object)]
    [InlineData(AssetType.Clothing, InventoryType.Wearable, FolderType.Clothing)]
    [InlineData(AssetType.Bodypart, InventoryType.Wearable, FolderType.BodyPart)]
    [InlineData(AssetType.Texture, InventoryType.Texture, FolderType.Texture)]
    [InlineData(AssetType.Texture, InventoryType.Snapshot, FolderType.Snapshot)]
    [InlineData(AssetType.Notecard, InventoryType.Notecard, FolderType.Notecard)]
    [InlineData(AssetType.LSLText, InventoryType.LSL, FolderType.LSLText)]
    [InlineData(AssetType.Landmark, InventoryType.Landmark, FolderType.Landmark)]
    [InlineData(AssetType.Animation, InventoryType.Animation, FolderType.Animation)]
    [InlineData(AssetType.Gesture, InventoryType.Gesture, FolderType.Gesture)]
    [InlineData(AssetType.Sound, InventoryType.Sound, FolderType.Sound)]
    [InlineData(AssetType.CallingCard, InventoryType.CallingCard, FolderType.CallingCard)]
    public void An_item_goes_back_to_the_system_folder_for_its_type(
        AssetType asset, InventoryType inv, FolderType expected)
    {
        Assert.Equal((int)expected, GridSession.RestoreFolderTypeFor(false, (int)asset, (int)inv));
    }

    [Fact]
    public void A_folder_goes_back_to_the_top_of_the_inventory()
    {
        Assert.Equal((int)FolderType.Root, GridSession.RestoreFolderTypeFor(true, -1, -1));
    }

    [Fact]
    public void An_unknown_type_goes_to_the_top_not_into_one_of_the_users_own_folders()
    {
        // -1 cast straight through is FolderType.None -- the preferred type of every plain folder.
        Assert.Equal((int)FolderType.Root, GridSession.RestoreFolderTypeFor(false, -1, -1));

        AddFolder(_root, "My own stuff");
        Assert.Equal(_root, GridSession.FindSystemFolder(_store, -1));
    }

    [Fact]
    public void Restore_resolves_to_the_real_folders()
    {
        var box = AddItem(_trash, "Box");
        var shirt = AddItem(_trash, "Shirt", AssetType.Clothing, InventoryType.Wearable);
        var photo = AddItem(_trash, "Beach", AssetType.Texture, InventoryType.Snapshot);
        var folder = AddFolder(_trash, "Old stuff");
        // No Notecards folder in this inventory: the root, as FindFolderForType would answer.
        var note = AddItem(_trash, "Note", AssetType.Notecard, InventoryType.Notecard);

        Assert.Equal(_objects, GridSession.RestoreDestination(_store, box));
        Assert.Equal(_clothing, GridSession.RestoreDestination(_store, shirt));
        Assert.Equal(_photos, GridSession.RestoreDestination(_store, photo));
        Assert.Equal(_root, GridSession.RestoreDestination(_store, folder));
        Assert.Equal(_root, GridSession.RestoreDestination(_store, note));
        Assert.Equal(UUID.Zero, GridSession.RestoreDestination(_store, UUID.Random()));
    }

    [Fact]
    public void A_link_goes_where_the_thing_it_points_at_would_go()
    {
        var shirt = AddItem(_objects, "Shirt", AssetType.Clothing, InventoryType.Wearable);
        var link = AddItem(_trash, "Shirt", AssetType.Link, InventoryType.Wearable, linkTo: shirt);
        var dangling = AddItem(_trash, "Gone", AssetType.Link, InventoryType.Wearable, linkTo: UUID.Random());
        var folderLink = AddItem(_trash, "Outfit", AssetType.LinkFolder, InventoryType.Unknown, linkTo: _objects);

        Assert.Equal(_clothing, GridSession.RestoreDestination(_store, link));
        Assert.Equal(_root, GridSession.RestoreDestination(_store, dangling));
        Assert.Equal(_root, GridSession.RestoreDestination(_store, folderLink));
    }

    [Fact]
    public void Restore_never_puts_anything_back_into_the_trash()
    {
        // No real asset type maps onto the Trash's folder type, but the lookup is a plain cast, so
        // the guard is pinned rather than left to the absence of a value.
        var odd = AddItem(_trash, "Odd", (AssetType)(sbyte)FolderType.Trash, InventoryType.Unknown);

        Assert.Equal(_root, GridSession.RestoreDestination(_store, odd));
    }
}
