using LibreMetaverse;

namespace SLNG.Net;

/// <summary>
/// What Second Life allows to be worn, stacked and taken off — the rules that stop a wardrobe
/// edit from leaving an avatar in a state it cannot be in.
///
/// <para><b>Body parts are replace-only.</b> Shape, Skin, Hair and Eyes are not clothing: an avatar
/// has exactly one of each, always. A real viewer offers no "take off" for them at all, and wearing
/// a second one replaces the first rather than layering it. SLNG had neither rule — removal deleted
/// the COF link like any other, which leaves an avatar with no shape, and wearing stacked a second
/// body part alongside the first. Since <c>WearWearableAsync</c> also writes a layer-ordering token
/// now, that second one would even have been given a position in a stack that cannot exist.</para>
///
/// <para>Clothing (including Alpha, Tattoo, Universal and Physics, which are clothing despite what
/// they do) layers freely and comes off freely.</para>
/// </summary>
internal static class WearableRules
{
    /// <summary>The four body-part types, by SL wearable type value. Kept as the fallback for when
    /// only a wearable type is known; prefer <see cref="IsBodyPart(AssetType, WearableType)"/>,
    /// which trusts the asset type the item actually carries.</summary>
    private static readonly HashSet<WearableType> BodyPartTypes = new()
    {
        WearableType.Shape, WearableType.Skin, WearableType.Hair, WearableType.Eyes,
    };

    /// <summary>True when this item is a body part, and therefore replace-only.</summary>
    internal static bool IsBodyPart(AssetType assetType, WearableType wearableType)
        => assetType == AssetType.Bodypart || BodyPartTypes.Contains(wearableType);

    /// <summary>True when taking this item off is allowed at all. An avatar cannot be without a
    /// shape, skin, hair or eyes, so the answer for those is no — swapping in a different one is
    /// the only way to change them.</summary>
    internal static bool CanTakeOff(AssetType assetType, WearableType wearableType)
        => !IsBodyPart(assetType, wearableType);

    /// <summary>True when wearing this item must first take off whatever of its type is already on.
    /// Body parts replace; clothing layers.</summary>
    internal static bool ReplacesSameType(AssetType assetType, WearableType wearableType)
        => IsBodyPart(assetType, wearableType);

    /// <summary>Why a take-off was refused, for the user rather than the log.</summary>
    internal static string TakeOffRefusedReason(WearableType wearableType)
        => $"{Describe(wearableType)} kann nicht abgelegt werden — Körperteile lassen sich nur ersetzen.";

    private static string Describe(WearableType wearableType) => wearableType switch
    {
        WearableType.Shape => "Die Form (Shape)",
        WearableType.Skin => "Die Haut (Skin)",
        WearableType.Hair => "Das Haar (Hair)",
        WearableType.Eyes => "Die Augen (Eyes)",
        _ => "Dieses Körperteil",
    };
}
