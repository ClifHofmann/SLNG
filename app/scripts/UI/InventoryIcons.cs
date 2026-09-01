using SLNG.Core;

namespace SLNG.App.UI;

/// <summary>
/// Icons for inventory rows, keyed by the SL wire type values on <see cref="InventoryEntry"/>.
///
/// <para>Presentation only, so it lives in the UI layer rather than next to the type constants in
/// <c>SLNG.Core</c> — those are protocol facts, these are a choice about how to draw them. Shared
/// across the inventory tree, the Worn tab and the Outfits tab so the same thing does not get three
/// different glyphs depending on which list it appears in.</para>
///
/// <para>Text glyphs rather than image resources: the inventory is a <c>Tree</c> whose rows are
/// already strings, so a prefix costs nothing to render, scales with the font, and needs no atlas
/// or import step.</para>
/// </summary>
public static class InventoryIcons
{
    /// <summary>Icon for an item, by SL AssetType. Unknown types get a neutral marker rather than
    /// nothing, so an unhandled type reads as "some item" instead of looking like a folder.</summary>
    public static string ForAssetType(int assetType) => assetType switch
    {
        AssetTypeIds.Texture => "🖼",
        AssetTypeIds.Sound => "🔊",
        AssetTypeIds.CallingCard => "📇",
        AssetTypeIds.Landmark => "🌐",
        AssetTypeIds.Clothing => "👕",
        AssetTypeIds.Object => "📦",
        AssetTypeIds.Notecard => "📄",
        AssetTypeIds.LslText => "📜",
        AssetTypeIds.LslBytecode => "⚙",
        AssetTypeIds.Bodypart => "🧍",
        AssetTypeIds.Animation => "🏃",
        AssetTypeIds.Gesture => "👋",
        AssetTypeIds.Mesh => "🔷",
        AssetTypeIds.Settings => "🌤",
        AssetTypeIds.Material => "🎨",
        _ => "•",
    };

    /// <summary>Icon for a worn item. Same table as everything else, with one exception: a HUD is an
    /// <c>Object</c> like any attachment as far as the asset type goes, but telling a HUD apart from
    /// something worn on the body is the main thing a worn list is read for.</summary>
    public static string ForWornItem(int assetType, WornCategory category)
        => category == WornCategory.Hud ? "🖥" : ForAssetType(assetType);

    /// <summary>Icon for a folder, by SL FolderType. The system folders people actually look for get
    /// their own glyph — finding Trash or Current Outfit in a long list is the whole point — and
    /// everything else is a plain folder.</summary>
    public static string ForFolderType(int preferredFolderType) => preferredFolderType switch
    {
        FolderTypeIds.Texture => "🖼",
        FolderTypeIds.Sound => "🔊",
        FolderTypeIds.CallingCard => "📇",
        FolderTypeIds.Landmark => "🌐",
        FolderTypeIds.Clothing => "👕",
        FolderTypeIds.Object => "📦",
        FolderTypeIds.Notecard => "📄",
        FolderTypeIds.LslText => "📜",
        FolderTypeIds.Bodypart => "🧍",
        FolderTypeIds.Trash => "🗑",
        FolderTypeIds.Snapshot => "📷",
        FolderTypeIds.LostAndFound => "🧭",
        FolderTypeIds.Animation => "🏃",
        FolderTypeIds.Gesture => "👋",
        FolderTypeIds.Favorites => "⭐",
        FolderTypeIds.CurrentOutfit => "✅",
        FolderTypeIds.Outfit or FolderTypeIds.MyOutfits => "👗",
        FolderTypeIds.Mesh => "🔷",
        FolderTypeIds.Inbox => "📥",
        FolderTypeIds.Outbox => "📤",
        FolderTypeIds.Settings => "🌤",
        FolderTypeIds.Material => "🎨",
        _ => "📁",
    };
}
