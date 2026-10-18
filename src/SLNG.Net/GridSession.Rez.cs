using LibreMetaverse;
using SLNG.Core;

namespace SLNG.Net;

public sealed partial class GridSession
{
    /// <summary>
    /// Checks whether the agent has build permissions at the specified region-local position (FEAT-INV-15).
    /// Returns true if building is permitted or if parcel information is not yet available.
    /// </summary>
    public bool CanBuildAt(System.Numerics.Vector3 position, out string reason)
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
            var options = ParcelInfoMapper.OptionsFrom(parcel);
            var groupIds = _groups?.Select(g => g.Id).ToList() ?? (IReadOnlyCollection<Guid>)Array.Empty<Guid>();

            bool allowed = BuildPermission.CanBuild(
                _client.Self.AgentID.Guid,
                _client.Self.ActiveGroup.Guid,
                groupIds,
                parcel.OwnerID.Guid,
                parcel.IsGroupOwned,
                parcel.GroupID.Guid,
                options);

            if (!allowed)
            {
                reason = "No permission to build on this parcel";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Rezzes an object from inventory into the world at the specified region-local position (FEAT-INV-15).
    /// </summary>
    public RezObjectResult RezObjectFromInventory(
        Guid itemId,
        System.Numerics.Vector3 position,
        System.Numerics.Quaternion? rotation = null)
    {
        var sim = _client.Network.CurrentSim;
        if (sim == null || !_client.Network.Connected)
        {
            return RezObjectResult.NotConnected;
        }

        var store = _client.Inventory.Store;
        var itemUuid = new LibreMetaverse.UUID(itemId);
        var itemNode = store?.GetNodeOrDefault(itemUuid);

        if (itemNode?.Data is LibreMetaverse.InventoryItem item && item.IsLink())
        {
            itemUuid = item.ResolvedItemID;
            itemNode = store?.GetNodeOrDefault(itemUuid);
        }

        if (itemNode?.Data is not LibreMetaverse.InventoryItem realItem)
        {
            return RezObjectResult.ItemNotFound;
        }

        bool isObject = realItem.AssetType == LibreMetaverse.AssetType.Object
            || realItem.InventoryType == LibreMetaverse.InventoryType.Object
            || realItem.InventoryType == LibreMetaverse.InventoryType.Attachment;

        if (!isObject)
        {
            return RezObjectResult.NotAnObject;
        }

        if (!CanBuildAt(position, out _))
        {
            return RezObjectResult.NoBuildRights;
        }

        UUID groupOwner = UUID.Zero;
        var lmvPos = new LibreMetaverse.Vector3(position.X, position.Y, position.Z);
        int localId = _client.Parcels.GetParcelLocalID(sim, lmvPos);
        if (localId > 0 && sim.Parcels.TryGetValue(localId, out var parcel) && parcel.GroupID != UUID.Zero)
        {
            if (_client.Self.ActiveGroup == parcel.GroupID ||
                (_groups != null && _groups.Any(g => g.Id == parcel.GroupID.Guid)))
            {
                groupOwner = parcel.GroupID;
            }
        }

        var rot = rotation.HasValue
            ? new LibreMetaverse.Quaternion(rotation.Value.X, rotation.Value.Y, rotation.Value.Z, rotation.Value.W)
            : LibreMetaverse.Quaternion.Identity;

        _client.Inventory.RequestRezFromInventory(sim, rot, lmvPos, realItem, groupOwner);
        return RezObjectResult.Success;
    }

    /// <summary>
    /// Checks whether the inventory item (or its link target) represents an in-world rezzable object (FEAT-INV-15).
    /// </summary>
    public bool IsObjectItem(Guid itemId)
    {
        var store = _client.Inventory.Store;
        if (store == null) return false;

        var itemUuid = new LibreMetaverse.UUID(itemId);
        var itemNode = store.GetNodeOrDefault(itemUuid);

        if (itemNode?.Data is LibreMetaverse.InventoryItem item && item.IsLink())
        {
            itemUuid = item.ResolvedItemID;
            itemNode = store.GetNodeOrDefault(itemUuid);
        }

        if (itemNode?.Data is not LibreMetaverse.InventoryItem realItem)
        {
            return false;
        }

        return realItem.AssetType == LibreMetaverse.AssetType.Object
            || realItem.InventoryType == LibreMetaverse.InventoryType.Object
            || realItem.InventoryType == LibreMetaverse.InventoryType.Attachment;
    }
}
