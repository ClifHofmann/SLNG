namespace SLNG.Core;

/// <summary>Broad grouping for the inventory "Worn" tab (FEAT-UI-16).</summary>
public enum WornCategory
{
    /// <summary>Shape, Skin, Eyes, Hair — <c>AssetType.Bodypart</c>. Cannot be taken off, only replaced.</summary>
    BodyPart,

    /// <summary>Shirt, Pants, Alpha, Tattoo, Universal, … — <c>AssetType.Clothing</c>.</summary>
    Clothing,

    /// <summary>A rezzed object on a body attachment point.</summary>
    Attachment,

    /// <summary>A rezzed object on a HUD attachment point.</summary>
    Hud
}

/// <summary>
/// One item currently worn by the local avatar — a system wearable or a rezzed attachment — as
/// listed in the inventory "Worn" (Angezogen) tab. Engine- and protocol-neutral; produced by
/// <c>SLNG.Net.GridSession.GetWornItems</c> (no LibreMetaverse type crosses that boundary).
/// </summary>
/// <param name="ItemId">Inventory item id of the real item (never a Current-Outfit link).</param>
/// <param name="Name">Display name, resolved from the inventory store; may be empty if the store
/// has not cached the item yet.</param>
/// <param name="Category">Which group the row sorts under.</param>
/// <param name="AttachPoint">Localised attachment-point label for <see cref="WornCategory.Attachment"/>
/// / <see cref="WornCategory.Hud"/> (e.g. "Skull", "HUD Center"); <c>null</c> for wearables.</param>
/// <param name="AssetType">SL <c>AssetType</c> wire value.</param>
/// <param name="Live">True when the item is actually attached / in the live wearables set right
/// now; false when it is only referenced by a Current-Outfit link (stale, or a pending wear).</param>
public sealed record WornItem(
    Guid ItemId,
    string Name,
    WornCategory Category,
    string? AttachPoint,
    int AssetType,
    bool Live);

/// <summary>One saved outfit — a direct subfolder of the <c>#Outfits</c> system folder
/// (FEAT-INV-04). Engine/protocol-neutral; produced by <c>SLNG.Net.GridSession.GetSavedOutfitsAsync</c>.</summary>
/// <param name="FolderId">Inventory folder id of the outfit.</param>
/// <param name="Name">Folder name = the outfit's display name.</param>
/// <param name="IsCurrent">This is the outfit the avatar is wearing — the Current Outfit Folder
/// carries a folder-link to it (the mechanism SL / Firestorm use).</param>
public sealed record OutfitEntry(Guid FolderId, string Name, bool IsCurrent = false);

/// <summary>Outcome of <c>SLNG.Net.GridSession.CleanUpCurrentOutfit</c> (FEAT-INV-03) — how many
/// Current-Outfit links were deleted, by reason. A COF link has no asset, so deleting one only
/// drops the outfit entry; the linked inventory item is untouched.</summary>
/// <param name="DeadLinks">Links that resolved to no target at all.</param>
/// <param name="TrashedTargetLinks">Links whose target item was itself already in Trash.</param>
/// <param name="UnwornAttachmentLinks">Links to attachment/object items that were not attached.</param>
/// <param name="Deferred">Nothing was touched because the inventory store or the scene was still
/// loading — acting on a half-loaded COF once deleted two real links and left the avatar with no
/// bake (live regression 2026-09-03). The caller should tell the user to retry in a moment.</param>
public sealed record OutfitCleanupResult(
    int DeadLinks, int TrashedTargetLinks, int UnwornAttachmentLinks, bool Deferred = false)
{
    /// <summary>Total links deleted.</summary>
    public int Total => DeadLinks + TrashedTargetLinks + UnwornAttachmentLinks;
}
