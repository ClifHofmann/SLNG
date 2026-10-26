using LibreMetaverse;
using LibreMetaverse.Packets;
using SLNG.Core;
using NVector3 = System.Numerics.Vector3;

namespace SLNG.Net;

// GridSession, rez-from-inventory part. FEAT-INV-15.
//
// Mirrors the reference viewer's LLToolDragAndDrop::dropObject (lltooldraganddrop.cpp:1846-1997) and the
// two callers that decide what to pass it, dad3dRezObjectOnLand (:2393) and dad3dRezObjectOnObject (:2454).
public sealed partial class GridSession
{
    /// <summary>
    /// A HINT for the drag card: would the reference viewer say the agent may build at this region-local
    /// position? Never a gate -- <see cref="RezObjectFromInventory"/> sends regardless, because the viewer
    /// does not check parcel rights on a drop either and the simulator decides. See
    /// <see cref="BuildPermission.CanBuild"/> for the rule and where it comes from.
    /// Returns true when the parcel is not known yet: no warning is better than a wrong one.
    /// </summary>
    public bool CanBuildAt(NVector3 position, out string reason)
    {
        var sim = _client.Network.CurrentSim;
        if (sim == null || !_client.Network.Connected)
        {
            reason = "Not connected";
            return false;
        }

        var lmvPos = new LibreMetaverse.Vector3(position.X, position.Y, position.Z);
        int localId = _client.Parcels.GetParcelLocalID(sim, lmvPos);

        if (localId > 0 && sim.Parcels.TryGetValue(localId, out var parcel))
        {
            bool allowed = BuildPermission.CanBuild(
                _client.Self.AgentID.Guid,
                _client.Self.ActiveGroup.Guid,
                parcel.OwnerID.Guid,
                parcel.GroupID.Guid,
                ParcelInfoMapper.OptionsFrom(parcel),
                HasLandCreatePowerIn);

            if (!allowed)
            {
                reason = "No permission to build on this parcel";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>Whether the agent holds the "always rez on group-owned land" role power
    /// (<c>GP_LAND_ALLOW_CREATE</c>) in this group, from the membership data the simulator sent
    /// (<c>AgentGroupDataUpdate</c>). False for a group not (yet) known.</summary>
    private bool HasLandCreatePowerIn(Guid groupId)
        => _groupPowers is { } powers && powers.TryGetValue(groupId, out var p) && HasLandCreatePower(p);

    /// <summary><c>GP_LAND_ALLOW_CREATE</c> is bit 25 (<c>roles_constants.h</c>:101); LibreMetaverse calls it
    /// <c>GroupPowers.AllowRez</c> and a test pins that the two agree.</summary>
    internal static bool HasLandCreatePower(ulong powers) => (powers & (ulong)GroupPowers.AllowRez) != 0;

    /// <summary>Group powers by group id, replaced wholesale by <see cref="OnCurrentGroups"/> (same
    /// swap-the-reference discipline as <c>_groups</c>).</summary>
    private volatile IReadOnlyDictionary<Guid, ulong>? _groupPowers;

    /// <summary>Whether a region-local position lies inside the region the agent is in. Rezzing only
    /// ever goes into the current region.</summary>
    public bool IsInCurrentRegion(NVector3 position)
    {
        var sim = _client.Network.CurrentSim;
        if (sim == null || !_client.Network.Connected) return false;
        float sizeX = sim.SizeX > 0 ? sim.SizeX : 256f;
        float sizeY = sim.SizeY > 0 ? sim.SizeY : 256f;
        return RezRay.InsideRegion(position, sizeX, sizeY);
    }

    /// <summary>
    /// Whether this inventory item (or the item a link points at) may be rezzed at all: it exists, it is
    /// an object, and it is not being worn. Cheap enough for one call per drop, NOT for one per mouse
    /// move -- the worn check walks the scene.
    /// </summary>
    public RezObjectResult CheckRezItem(Guid itemId)
        => ResolveRezItem(_client.Inventory.Store, new UUID(itemId), id => LiveWornItemIds().Contains(id), out _);

    /// <summary>
    /// Rezzes an inventory object into the current region the way the reference viewer's drag and drop
    /// does, and sends a <c>RezObject</c> packet built here rather than through LibreMetaverse's
    /// <c>RequestRezFromInventory</c>, whose overloads fix <c>RemoveItem</c> to false, start and end the ray
    /// on the same point with <c>BypassRaycast</c> on, and send the inventory type in <c>Type</c>.
    /// </summary>
    /// <remarks>
    /// <para>Always sends when the item is fine and the point is in the region: no parcel-rights check
    /// (the simulator decides). The group on the packet is the agent's ACTIVE group
    /// (<c>gAgent.getGroupID()</c>, :1935), not the parcel's. See <see cref="RezRay.For"/> for the ray.
    /// A no-copy item is sent with <c>RemoveItem</c> and dropped from the local inventory at once, "so
    /// that users cannot easily bypass copy protection in laggy situations" (:1990-1997).</para>
    /// </remarks>
    /// <param name="itemId">The inventory item (a link is followed to its target).</param>
    /// <param name="cameraRegionPos">The camera, region-local: the ray start.</param>
    /// <param name="hitRegionPos">Where the pick ray hit the surface, region-local, as it was hit.</param>
    /// <param name="hitObjectId">The full UUID of the object that was hit, or <see cref="Guid.Empty"/> for land.</param>
    public RezObjectResult RezObjectFromInventory(
        Guid itemId,
        NVector3 cameraRegionPos,
        NVector3 hitRegionPos,
        Guid hitObjectId)
    {
        var sim = _client.Network.CurrentSim;
        if (sim == null || !_client.Network.Connected)
        {
            return RezObjectResult.NotConnected;
        }

        var store = _client.Inventory.Store;
        var result = ResolveRezItem(store, new UUID(itemId), id => LiveWornItemIds().Contains(id), out var item);
        if (result != RezObjectResult.Success || item == null || store == null)
        {
            return result;
        }

        if (!IsInCurrentRegion(hitRegionPos))
        {
            return RezObjectResult.OutsideRegion;
        }

        var ray = RezRay.For(cameraRegionPos, hitRegionPos, hitObjectId);
        var self = _client.Self;
        bool agentCanCopy = AgentCanCopy(self.AgentID.Guid, item);
        bool removeItem = RezRules.ShouldRemoveItem(agentCanCopy);

        // FolderID: nothing when it comes from the Trash or the Library, "since it's coming from the
        // library or trash, we want to not 'take' it back to the same place" (:1956-1964).
        var library = store.LibraryFolder?.UUID ?? UUID.Zero;
        var folderId = RezFolderId(store, item, VerifiedTrash(), library);

        var packet = BuildRezObjectPacket(self.AgentID, self.SessionID, self.ActiveGroup, item, folderId, ray, removeItem);
        _client.Network.SendPacket(packet, sim);

        if (removeItem)
        {
            var folder = item.ParentUUID.Guid;
            store.RemoveNodeFor(item);
            InventoryItemRemoved?.Invoke(this, new InventoryItemRemovedEvent(item.UUID.Guid, folder));
        }

        return RezObjectResult.Success;
    }

    /// <summary>
    /// Checks whether the inventory item (or its link target) represents an in-world rezzable object (FEAT-INV-15).
    /// </summary>
    public bool IsObjectItem(Guid itemId)
        => ResolveItem(_client.Inventory.Store, new UUID(itemId)) is { } item && IsRezzableType(item);

    // ---- The decisions and the packet, as pure functions so tests can pin them ----------------------

    /// <summary>Follows a link to the item it points at; null when the store does not hold the item.</summary>
    internal static InventoryItem? ResolveItem(Inventory? store, UUID itemId)
    {
        var node = store?.GetNodeOrDefault(itemId);
        if (node?.Data is InventoryItem link && link.IsLink())
        {
            node = store!.GetNodeOrDefault(link.ResolvedItemID);
        }
        return node?.Data as InventoryItem;
    }

    internal static bool IsRezzableType(InventoryItem item)
        => item.AssetType == AssetType.Object
           || item.InventoryType == InventoryType.Object
           || item.InventoryType == InventoryType.Attachment;

    /// <summary>
    /// The item checks of a rez, in the viewer's terms: the item must exist, be an object, and not be
    /// worn (<c>isWearingAttachment</c> -> <c>ACCEPT_NO</c>, <c>lltooldraganddrop.cpp</c>:2408 and :2469).
    /// A link is followed FIRST and the worn test is on the item it points at -- a worn attachment is
    /// known by its real item id, never by a link to it.
    /// </summary>
    /// <param name="isWorn">Asked with the resolved item's id, and only once the item is known to be an
    /// object, so the (scene-walking) worn lookup is not paid for anything else.</param>
    internal static RezObjectResult ResolveRezItem(
        Inventory? store, UUID itemId, Func<Guid, bool> isWorn, out InventoryItem? item)
    {
        item = null;
        var real = ResolveItem(store, itemId);
        if (real == null) return RezObjectResult.ItemNotFound;
        if (!IsRezzableType(real)) return RezObjectResult.NotAnObject;
        if (isWorn(real.UUID.Guid)) return RezObjectResult.Worn;

        item = real;
        return RezObjectResult.Success;
    }

    /// <summary><c>item->getPermissions().allowCopyBy(gAgent.getID())</c> -- see <see cref="RezRules.AgentCanCopy"/>.</summary>
    internal static bool AgentCanCopy(Guid agentId, InventoryItem item)
    {
        var p = item.Permissions;
        return RezRules.AgentCanCopy(
            agentId, item.OwnerID.Guid, item.GroupID.Guid, item.GroupOwned, (uint)p.OwnerMask, (uint)p.EveryoneMask);
    }

    /// <summary>The <c>FolderID</c> the packet carries: the item's parent, or nothing for an item in the
    /// Trash or the Library.</summary>
    internal static UUID RezFolderId(Inventory store, InventoryItem item, UUID trash, UUID library)
        => IsBelow(store, item.UUID, trash) || IsBelow(store, item.UUID, library) ? UUID.Zero : item.ParentUUID;

    /// <summary>
    /// The <c>RezObject</c> packet as <c>dropObject</c> packs it (:1932-1977). <c>RezSelected</c> is false
    /// (it is "whether we are editing", and we are not), <c>RayEndIsIntersection</c> false, <c>FromTaskID</c>
    /// empty. <c>InventoryData</c> is <c>LLViewerInventoryItem::packMessage</c> (llviewerinventory.cpp:539-556):
    /// <c>Type</c> is the item's ASSET type and <c>InvType</c> its inventory type; <c>TransactionID</c> is the
    /// item's transaction id, which is empty for an item that already exists.
    /// <c>CRC</c> is left 0 as LibreMetaverse does: the simulator takes the item from its own inventory by
    /// id and the permission-slam fields are "cruft -- the server no longer pays attention to this data"
    /// (<c>pack_permissions_slam</c>, :3456).
    /// </summary>
    internal static RezObjectPacket BuildRezObjectPacket(
        UUID agentId, UUID sessionId, UUID activeGroupId, InventoryItem item, UUID folderId, RezRay ray, bool removeItem)
    {
        var perms = item.Permissions;
        return new RezObjectPacket
        {
            Header = { Reliable = true },
            AgentData =
            {
                AgentID = agentId,
                SessionID = sessionId,
                GroupID = activeGroupId,
            },
            RezData =
            {
                FromTaskID = UUID.Zero,
                BypassRaycast = (byte)(ray.BypassRaycast ? 1 : 0),
                RayStart = new LibreMetaverse.Vector3(ray.Start.X, ray.Start.Y, ray.Start.Z),
                RayEnd = new LibreMetaverse.Vector3(ray.End.X, ray.End.Y, ray.End.Z),
                RayTargetID = new UUID(ray.TargetId),
                RayEndIsIntersection = false,
                RezSelected = false,
                RemoveItem = removeItem,
                ItemFlags = (uint)item.Flags,
                GroupMask = (uint)perms.GroupMask,
                EveryoneMask = (uint)perms.EveryoneMask,
                NextOwnerMask = (uint)perms.NextOwnerMask,
            },
            InventoryData =
            {
                ItemID = item.UUID,
                FolderID = folderId,
                CreatorID = item.CreatorID,
                OwnerID = item.OwnerID,
                GroupID = item.GroupID,
                BaseMask = (uint)perms.BaseMask,
                OwnerMask = (uint)perms.OwnerMask,
                GroupMask = (uint)perms.GroupMask,
                EveryoneMask = (uint)perms.EveryoneMask,
                NextOwnerMask = (uint)perms.NextOwnerMask,
                GroupOwned = item.GroupOwned,
                TransactionID = UUID.Zero,
                Type = (sbyte)item.AssetType,
                InvType = (sbyte)item.InventoryType,
                Flags = (uint)item.Flags,
                SaleType = (byte)item.SaleType,
                SalePrice = item.SalePrice,
                Name = Utils.StringToBytes(item.Name),
                Description = Utils.StringToBytes(item.Description),
                CreationDate = (int)Utils.DateTimeToUnixTime(item.CreationDate),
            },
        };
    }
}
