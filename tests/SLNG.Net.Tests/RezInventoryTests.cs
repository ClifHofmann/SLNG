using LibreMetaverse;
using SLNG.Core;
using Xunit;
using NVector3 = System.Numerics.Vector3;

namespace SLNG.Net.Tests;

/// <summary>
/// FEAT-INV-15: rezzing an inventory object. The packet and the decisions behind it are pinned against
/// the reference viewer's <c>LLToolDragAndDrop::dropObject</c> (lltooldraganddrop.cpp:1846-1997); the part
/// that needs a live connection (sending) is not tested here.
/// </summary>
public class RezInventoryTests
{
    private readonly UUID _owner = UUID.Random();
    private readonly UUID _agent = UUID.Random();
    private readonly UUID _session = UUID.Random();
    private readonly UUID _activeGroup = UUID.Random();
    private readonly UUID _root = UUID.Random();
    private readonly UUID _trash = UUID.Random();
    private readonly UUID _library = UUID.Random();
    private readonly UUID _objects = UUID.Random();
    private readonly Inventory _store;

    private static readonly NVector3 Camera = new(10f, 20f, 30f);
    private static readonly NVector3 Hit = new(14f, 20f, 26f);

    public RezInventoryTests()
    {
        _store = new Inventory(new GridClient(), _owner)
        {
            RootFolder = Folder(_root, UUID.Zero, "My Inventory", FolderType.Root),
        };
        _store.UpdateNodeFor(Folder(_trash, _root, "Trash", FolderType.Trash));
        _store.UpdateNodeFor(Folder(_library, _root, "Library", FolderType.None));
        _store.UpdateNodeFor(Folder(_objects, _root, "Objects", FolderType.Object));
    }

    private InventoryFolder Folder(UUID id, UUID parent, string name, FolderType type)
        => new(id) { ParentUUID = parent, Name = name, OwnerID = _owner, PreferredType = type };

    private InventoryItem Add(
        UUID parent,
        string name,
        AssetType type = AssetType.Object,
        InventoryType inv = InventoryType.Object,
        uint ownerMask = 0x7FFFFFFF,
        UUID linkTo = default)
    {
        var item = new InventoryItem(UUID.Random())
        {
            ParentUUID = parent,
            Name = name,
            Description = "a thing",
            OwnerID = _agent,
            CreatorID = UUID.Random(),
            GroupID = UUID.Random(),
            AssetType = type,
            InventoryType = inv,
            AssetUUID = linkTo,
            Flags = 0x12,
            SalePrice = 7,
            SaleType = SaleType.Copy,
            Permissions = new Permissions
            {
                BaseMask = (PermissionMask)0x7FFFFFFF,
                OwnerMask = (PermissionMask)ownerMask,
                GroupMask = (PermissionMask)0x1111,
                EveryoneMask = (PermissionMask)0x2222,
                NextOwnerMask = (PermissionMask)0x3333,
            },
        };
        _store.UpdateNodeFor(item);
        return item;
    }

    // ---- Without a connection ------------------------------------------------------------------

    [Fact]
    public void RezObjectFromInventory_returns_NotConnected_when_disconnected()
    {
        using var session = new GridSession();
        var result = session.RezObjectFromInventory(Guid.NewGuid(), Camera, Hit, Guid.Empty);

        Assert.Equal(RezObjectResult.NotConnected, result);
    }

    [Fact]
    public void CanBuildAt_returns_false_when_disconnected()
    {
        using var session = new GridSession();
        bool canBuild = session.CanBuildAt(NVector3.Zero, out var reason);

        Assert.False(canBuild);
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void IsInCurrentRegion_is_false_when_disconnected()
    {
        using var session = new GridSession();
        Assert.False(session.IsInCurrentRegion(new NVector3(10f, 10f, 10f)));
    }

    [Fact]
    public void IsObjectItem_returns_false_when_item_not_in_store()
    {
        using var session = new GridSession();
        bool isObject = session.IsObjectItem(Guid.NewGuid());

        Assert.False(isObject);
    }

    // ---- Which items may be rezzed -------------------------------------------------------------

    [Fact]
    public void An_object_that_is_not_worn_may_be_rezzed()
    {
        var item = Add(_objects, "Chair");

        var result = GridSession.ResolveRezItem(_store, item.UUID, _ => false, out var resolved);

        Assert.Equal(RezObjectResult.Success, result);
        Assert.Equal(item.UUID, resolved!.UUID);
    }

    [Fact]
    public void An_attachment_item_may_be_rezzed_by_its_inventory_type()
    {
        var item = Add(_objects, "Hat", AssetType.Unknown, InventoryType.Attachment);

        Assert.Equal(RezObjectResult.Success, GridSession.ResolveRezItem(_store, item.UUID, _ => false, out _));
    }

    [Fact]
    public void An_item_the_store_does_not_hold_is_not_found()
    {
        var result = GridSession.ResolveRezItem(_store, UUID.Random(), _ => false, out var resolved);

        Assert.Equal(RezObjectResult.ItemNotFound, result);
        Assert.Null(resolved);
        Assert.Equal(RezObjectResult.ItemNotFound, GridSession.ResolveRezItem(null, UUID.Random(), _ => false, out _));
    }

    [Fact]
    public void Something_that_is_not_an_object_is_refused_without_asking_what_is_worn()
    {
        var texture = Add(_objects, "Pic", AssetType.Texture, InventoryType.Texture);
        bool askedWorn = false;

        var result = GridSession.ResolveRezItem(_store, texture.UUID, _ => askedWorn = true, out _);

        Assert.Equal(RezObjectResult.NotAnObject, result);
        Assert.False(askedWorn); // the worn lookup walks the scene; it is not paid for a texture
    }

    [Fact]
    public void A_worn_object_is_refused_like_the_viewers_isWearingAttachment()
    {
        var worn = Add(_objects, "Sword");

        var result = GridSession.ResolveRezItem(_store, worn.UUID, id => id == worn.UUID.Guid, out var resolved);

        Assert.Equal(RezObjectResult.Worn, result);
        Assert.Null(resolved);
    }

    [Fact]
    public void A_link_is_followed_and_the_worn_test_is_on_the_item_it_points_at()
    {
        var real = Add(_objects, "Sword");
        var link = Add(_objects, "Sword", AssetType.Link, InventoryType.Object, linkTo: real.UUID);
        var asked = new List<Guid>();

        var result = GridSession.ResolveRezItem(_store, link.UUID, id => { asked.Add(id); return id == real.UUID.Guid; }, out _);

        Assert.Equal(RezObjectResult.Worn, result);
        Assert.Equal(new[] { real.UUID.Guid }, asked); // the real item, never the link
    }

    [Fact]
    public void A_link_to_an_item_that_is_not_worn_resolves_to_the_target()
    {
        var real = Add(_objects, "Sword");
        var link = Add(_objects, "Sword", AssetType.Link, InventoryType.Object, linkTo: real.UUID);

        var result = GridSession.ResolveRezItem(_store, link.UUID, _ => false, out var resolved);

        Assert.Equal(RezObjectResult.Success, result);
        Assert.Equal(real.UUID, resolved!.UUID);
    }

    [Fact]
    public void A_link_whose_target_is_gone_is_not_found()
    {
        var link = Add(_objects, "Dangling", AssetType.Link, InventoryType.Object, linkTo: UUID.Random());

        Assert.Equal(RezObjectResult.ItemNotFound, GridSession.ResolveRezItem(_store, link.UUID, _ => false, out _));
    }

    // ---- The packet ---------------------------------------------------------------------------

    private LibreMetaverse.Packets.RezObjectPacket Packet(
        InventoryItem item, Guid hitObjectId, bool removeItem, UUID? folder = null)
        => GridSession.BuildRezObjectPacket(
            _agent, _session, _activeGroup, item, folder ?? item.ParentUUID,
            RezRay.For(Camera, Hit, hitObjectId), removeItem);

    [Fact]
    public void The_packet_carries_the_agents_ACTIVE_group_not_a_parcel_group()
    {
        var packet = Packet(Add(_objects, "Chair"), Guid.Empty, removeItem: false);

        Assert.Equal(_agent, packet.AgentData.AgentID);
        Assert.Equal(_session, packet.AgentData.SessionID);
        Assert.Equal(_activeGroup, packet.AgentData.GroupID);
    }

    [Fact]
    public void The_packet_is_reliable_and_does_not_ask_for_the_object_to_be_selected()
    {
        var packet = Packet(Add(_objects, "Chair"), Guid.Empty, removeItem: false);

        Assert.True(packet.Header.Reliable);
        Assert.False(packet.RezData.RezSelected);
        Assert.False(packet.RezData.RayEndIsIntersection);
        Assert.Equal(UUID.Zero, packet.RezData.FromTaskID);
    }

    [Fact]
    public void A_land_drop_bypasses_the_raycast_and_ends_on_the_hit()
    {
        var packet = Packet(Add(_objects, "Chair"), Guid.Empty, removeItem: false);

        Assert.Equal(1, packet.RezData.BypassRaycast);
        AssertVector(Camera, packet.RezData.RayStart);
        AssertVector(Hit, packet.RezData.RayEnd);
        Assert.Equal(UUID.Zero, packet.RezData.RayTargetID);
    }

    [Fact]
    public void An_object_drop_leaves_the_raycast_to_the_sim_doubles_the_ray_and_names_the_target()
    {
        var target = Guid.NewGuid();

        var packet = Packet(Add(_objects, "Chair"), target, removeItem: false);

        Assert.Equal(0, packet.RezData.BypassRaycast);
        AssertVector(Camera, packet.RezData.RayStart);
        AssertVector(new NVector3(18f, 20f, 22f), packet.RezData.RayEnd);
        Assert.Equal(new UUID(target), packet.RezData.RayTargetID);
    }

    [Fact]
    public void RemoveItem_follows_the_decision_it_is_given()
    {
        var item = Add(_objects, "Chair");

        Assert.True(Packet(item, Guid.Empty, removeItem: true).RezData.RemoveItem);
        Assert.False(Packet(item, Guid.Empty, removeItem: false).RezData.RemoveItem);
    }

    [Fact]
    public void The_inventory_block_is_packMessage_with_the_ASSET_type_in_Type()
    {
        var item = Add(_objects, "Chair");

        var data = Packet(item, Guid.Empty, removeItem: false).InventoryData;

        Assert.Equal(item.UUID, data.ItemID);
        Assert.Equal(item.ParentUUID, data.FolderID);
        Assert.Equal(item.CreatorID, data.CreatorID);
        Assert.Equal(item.OwnerID, data.OwnerID);
        Assert.Equal(item.GroupID, data.GroupID);
        Assert.Equal((sbyte)AssetType.Object, data.Type);
        Assert.Equal((sbyte)InventoryType.Object, data.InvType);
        Assert.Equal(0x12u, data.Flags);
        Assert.Equal((byte)SaleType.Copy, data.SaleType);
        Assert.Equal(7, data.SalePrice);
        Assert.Equal(UUID.Zero, data.TransactionID);
        Assert.Equal(0x7FFFFFFFu, data.BaseMask);
        Assert.Equal(0x1111u, data.GroupMask);
        Assert.Equal(0x2222u, data.EveryoneMask);
        Assert.Equal(0x3333u, data.NextOwnerMask);
    }

    [Fact]
    public void An_attachment_item_sends_its_asset_type_and_its_attachment_inventory_type()
    {
        var item = Add(_objects, "Hat", AssetType.Object, InventoryType.Attachment);

        var data = Packet(item, Guid.Empty, removeItem: false).InventoryData;

        // Object is 6 in both enums, so only an attachment can tell Type (asset) from InvType (inventory).
        Assert.Equal((sbyte)AssetType.Object, data.Type);
        Assert.Equal((sbyte)InventoryType.Attachment, data.InvType);
        Assert.NotEqual(data.Type, data.InvType);
    }

    [Fact]
    public void The_slam_fields_come_from_the_item()
    {
        var item = Add(_objects, "Chair");

        var rez = Packet(item, Guid.Empty, removeItem: false).RezData;

        Assert.Equal(0x12u, rez.ItemFlags);
        Assert.Equal(0x1111u, rez.GroupMask);
        Assert.Equal(0x2222u, rez.EveryoneMask);
        Assert.Equal(0x3333u, rez.NextOwnerMask);
    }

    [Fact]
    public void Name_and_description_round_trip()
    {
        var item = Add(_objects, "Chair");

        var data = Packet(item, Guid.Empty, removeItem: false).InventoryData;

        Assert.Equal("Chair", Utils.BytesToString(data.Name));
        Assert.Equal("a thing", Utils.BytesToString(data.Description));
    }

    // ---- FolderID, RemoveItem -----------------------------------------------------------------

    [Fact]
    public void An_item_in_the_trash_is_sent_with_no_folder()
    {
        var inTrash = Add(_trash, "Old chair");
        var deep = UUID.Random();
        _store.UpdateNodeFor(Folder(deep, _trash, "Old stuff", FolderType.None));
        var inSubfolder = Add(deep, "Older chair");

        Assert.Equal(UUID.Zero, GridSession.RezFolderId(_store, inTrash, _trash, _library));
        Assert.Equal(UUID.Zero, GridSession.RezFolderId(_store, inSubfolder, _trash, _library));

        var packet = Packet(inTrash, Guid.Empty, removeItem: false, folder: GridSession.RezFolderId(_store, inTrash, _trash, _library));
        Assert.Equal(UUID.Zero, packet.InventoryData.FolderID);
    }

    [Fact]
    public void An_item_in_the_library_is_sent_with_no_folder()
    {
        var item = Add(_library, "Library chair");

        Assert.Equal(UUID.Zero, GridSession.RezFolderId(_store, item, _trash, _library));
    }

    [Fact]
    public void An_item_anywhere_else_keeps_its_folder()
    {
        var item = Add(_objects, "Chair");

        Assert.Equal(_objects, GridSession.RezFolderId(_store, item, _trash, _library));
        // No Trash and no Library known: nothing is below a zero id.
        Assert.Equal(_objects, GridSession.RezFolderId(_store, item, UUID.Zero, UUID.Zero));
    }

    [Fact]
    public void A_no_copy_item_is_removed_and_a_copyable_one_is_not()
    {
        const uint Copy = RezRules.PermCopy;
        var noCopy = Add(_objects, "One-off", ownerMask: 0x7FFFFFFF & ~Copy);
        var copy = Add(_objects, "Chair", ownerMask: 0x7FFFFFFF);

        Assert.False(GridSession.AgentCanCopy(_agent.Guid, noCopy));
        Assert.True(GridSession.AgentCanCopy(_agent.Guid, copy));
        Assert.True(RezRules.ShouldRemoveItem(GridSession.AgentCanCopy(_agent.Guid, noCopy)));
        Assert.False(RezRules.ShouldRemoveItem(GridSession.AgentCanCopy(_agent.Guid, copy)));
    }

    // ---- Group powers -------------------------------------------------------------------------

    [Fact]
    public void The_land_create_power_is_the_viewers_GP_LAND_ALLOW_CREATE_bit()
    {
        // roles_constants.h:101 -- 0x1LL << 25. LibreMetaverse calls it AllowRez.
        Assert.Equal(1UL << 25, (ulong)GroupPowers.AllowRez);
        Assert.True(GridSession.HasLandCreatePower(1UL << 25));
        Assert.True(GridSession.HasLandCreatePower((1UL << 25) | 1UL));
        Assert.False(GridSession.HasLandCreatePower(1UL << 24)); // GP_LAND_ALLOW_FLY
        Assert.False(GridSession.HasLandCreatePower(1UL << 23)); // GP_LAND_ALLOW_EDIT_LAND
        Assert.False(GridSession.HasLandCreatePower(0));
    }

    private static void AssertVector(NVector3 expected, LibreMetaverse.Vector3 actual)
    {
        Assert.Equal(expected.X, actual.X);
        Assert.Equal(expected.Y, actual.Y);
        Assert.Equal(expected.Z, actual.Z);
    }
}
