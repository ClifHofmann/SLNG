namespace SLNG.Core;

/// <summary>
/// One inventory node — folder or item — in engine- and protocol-neutral form. Produced by
/// <c>SLNG.Net.GridSession.FetchInventoryChildrenAsync</c> from LibreMetaverse's inventory
/// store; no LibreMetaverse type crosses that boundary (AGENTS.md layering rule).
///
/// Enum-ish fields (<see cref="PreferredFolderType"/>, <see cref="AssetType"/>,
/// <see cref="InventoryType"/>) carry the raw SL wire values rather than mirrored enums —
/// the UI only needs a handful of them (icons, "is this Trash"), and mirroring the full SL
/// enum sets before anything consumes them would just be churn. Add named enums in Core when
/// a consumer actually branches on more than a couple of values.
/// </summary>
/// <param name="Id">Inventory UUID of this folder/item (NOT an asset id).</param>
/// <param name="ParentId">Inventory UUID of the containing folder.</param>
/// <param name="OwnerId">Owning agent — differs from the logged-in agent for Library nodes.</param>
/// <param name="IsFolder">Folder vs item.</param>
/// <param name="PreferredFolderType">Folders only: SL FolderType (system-folder marker — Trash,
/// Objects, Current Outfit, …; -1 = ordinary folder). -1 for items.</param>
/// <param name="AssetId">Items only: the underlying asset UUID. Guid.Empty for folders AND for
/// links (a link's target is an inventory item, not an asset — see <paramref name="LinkTargetId"/>).</param>
/// <param name="AssetType">Items only: SL AssetType wire value; -1 for folders.</param>
/// <param name="InventoryType">Items only: SL InventoryType wire value (icon/category); -1 for folders.</param>
/// <param name="IsLink">True for inventory links (Current Outfit etc.).</param>
/// <param name="LinkTargetId">For links: the inventory UUID of the linked item; else Guid.Empty.</param>
/// <param name="CanCopy">Owner may copy (from the item's owner permission mask).</param>
/// <param name="CanModify">Owner may modify.</param>
/// <param name="CanTransfer">Owner may transfer.</param>
public sealed record InventoryEntry(
    Guid Id,
    Guid ParentId,
    Guid OwnerId,
    string Name,
    bool IsFolder,
    int PreferredFolderType,
    Guid AssetId,
    int AssetType,
    int InventoryType,
    bool IsLink,
    Guid LinkTargetId,
    bool CanCopy,
    bool CanModify,
    bool CanTransfer)
{
    /// <summary>True if the owner has all permissions (Copy, Modify, Transfer).</summary>
    public bool IsFullPerm => CanCopy && CanModify && CanTransfer;

    /// <summary>
    /// Returns the standard SL permission suffix string, e.g. " (no copy)(no modify)(no transfer)".
    /// Returns an empty string if all permissions are granted.
    /// </summary>
    public string GetPermissionSuffix()
    {
        if (IsFolder || IsFullPerm) return string.Empty;

        var suffix = new System.Text.StringBuilder();
        if (!CanCopy) suffix.Append(" (no copy)");
        if (!CanModify) suffix.Append(" (no modify)");
        if (!CanTransfer) suffix.Append(" (no transfer)");

        return suffix.ToString();
    }
}

/// <summary>SL AssetType wire values that UI code branches on by name instead of a magic
/// number. Deliberately not the full SL enum -- see the <see cref="InventoryEntry.AssetType"/>
/// doc comment; add more members only when a consumer actually needs them.</summary>
public static class AssetTypeIds
{
    public const int Texture = 0;
    public const int Sound = 1;
    public const int CallingCard = 2;
    public const int Landmark = 3;
    public const int Clothing = 5;
    public const int Object = 6;
    public const int Notecard = 7;
    public const int Folder = 8;
    public const int LslText = 10;
    public const int LslBytecode = 11;
    public const int Bodypart = 13;
    public const int Animation = 20;
    public const int Gesture = 21;
    public const int Link = 24;
    public const int LinkFolder = 25;
    public const int Mesh = 49;
    public const int Settings = 56;
    public const int Material = 57;
}

/// <summary>SL FolderType wire values for the system folders the UI marks out. Ordinary folders
/// carry -1. Same rule as <see cref="AssetTypeIds"/>: named only where something branches on it.</summary>
public static class FolderTypeIds
{
    public const int Texture = 0;
    public const int Sound = 1;
    public const int CallingCard = 2;
    public const int Landmark = 3;
    public const int Clothing = 5;
    public const int Object = 6;
    public const int Notecard = 7;
    public const int Root = 8;
    public const int LslText = 10;
    public const int Bodypart = 13;
    public const int Trash = 14;
    public const int Snapshot = 15;
    public const int LostAndFound = 16;
    public const int Animation = 20;
    public const int Gesture = 21;
    public const int Favorites = 23;
    public const int CurrentOutfit = 46;
    public const int Outfit = 47;
    public const int MyOutfits = 48;
    public const int Mesh = 49;
    public const int Inbox = 50;
    public const int Outbox = 51;
    public const int Settings = 56;
    public const int Material = 57;
}

/// <summary>
/// Detailed properties for an inventory item, used by the properties window.
/// </summary>
public sealed record InventoryItemProperties(
    Guid Id,
    string Name,
    string Description,
    bool NextOwnerCanCopy,
    bool NextOwnerCanModify,
    bool NextOwnerCanTransfer
);
