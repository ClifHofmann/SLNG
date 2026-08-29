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
