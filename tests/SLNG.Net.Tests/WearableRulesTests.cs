using LibreMetaverse;
using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>
/// FEAT-AVATAR-01: pins the rules that stop a wardrobe edit from leaving an avatar in a state it
/// cannot be in.
///
/// <para>Shape, Skin, Hair and Eyes are body parts — an avatar has exactly one of each, always, and
/// a real viewer offers no take-off for them. SLNG had neither rule: removing deleted the COF link
/// like any other, which leaves an avatar with no shape, and wearing added a second body part
/// beside the first instead of replacing it. Since wearing now also writes a layer-ordering token,
/// that second one would have been given a position in a stack that cannot exist.</para>
///
/// <para>Everything else layers, including Alpha, Tattoo, Universal and Physics — they are clothing
/// in Second Life's model regardless of what they do, and treating one of them as a body part would
/// make an alpha layer impossible to take off.</para>
/// </summary>
public class WearableRulesTests
{
    public static TheoryData<WearableType> BodyParts() => new()
    {
        WearableType.Shape, WearableType.Skin, WearableType.Hair, WearableType.Eyes,
    };

    public static TheoryData<WearableType> Clothing() => new()
    {
        WearableType.Shirt, WearableType.Pants, WearableType.Shoes, WearableType.Socks,
        WearableType.Jacket, WearableType.Gloves, WearableType.Undershirt, WearableType.Underpants,
        WearableType.Skirt, WearableType.Alpha, WearableType.Tattoo, WearableType.Universal,
        WearableType.Physics,
    };

    [Theory]
    [MemberData(nameof(BodyParts))]
    public void A_body_part_cannot_be_taken_off(WearableType type)
    {
        Assert.True(WearableRules.IsBodyPart(AssetType.Bodypart, type));
        Assert.False(WearableRules.CanTakeOff(AssetType.Bodypart, type));
        Assert.True(WearableRules.ReplacesSameType(AssetType.Bodypart, type));
    }

    /// <summary>Alpha and Tattoo do body-part-ish things and are still clothing. Getting this wrong
    /// would make an alpha layer impossible to remove — the exact opposite of what it is for.</summary>
    [Theory]
    [MemberData(nameof(Clothing))]
    public void Clothing_layers_and_comes_off(WearableType type)
    {
        Assert.False(WearableRules.IsBodyPart(AssetType.Clothing, type));
        Assert.True(WearableRules.CanTakeOff(AssetType.Clothing, type));
        Assert.False(WearableRules.ReplacesSameType(AssetType.Clothing, type));
    }

    /// <summary>The asset type decides even when the wearable type says otherwise: an item's own
    /// AssetType is what the grid stored, and a wearable type can arrive unset (Invalid) from a link
    /// whose target has not resolved yet. Erring towards "body part" there is the safe direction —
    /// it refuses a take-off rather than performing an irreversible one.</summary>
    [Fact]
    public void The_asset_type_alone_is_enough_to_protect_a_body_part()
    {
        Assert.True(WearableRules.IsBodyPart(AssetType.Bodypart, WearableType.Invalid));
        Assert.False(WearableRules.CanTakeOff(AssetType.Bodypart, WearableType.Invalid));
    }

    /// <summary>…and the wearable type alone is enough too, for a path that only has that.</summary>
    [Fact]
    public void The_wearable_type_alone_is_enough_to_protect_a_body_part()
    {
        Assert.True(WearableRules.IsBodyPart(AssetType.Unknown, WearableType.Shape));
        Assert.False(WearableRules.CanTakeOff(AssetType.Unknown, WearableType.Shape));
    }

    /// <summary>The refusal is shown to the user, so it has to name what was refused rather than
    /// read like an error.</summary>
    [Theory]
    [MemberData(nameof(BodyParts))]
    public void The_refusal_explains_itself(WearableType type)
    {
        string reason = WearableRules.TakeOffRefusedReason(type);

        Assert.Contains("ersetzen", reason);
        Assert.DoesNotContain("Fehler", reason);
        Assert.True(reason.Length > 20, "a refusal the user reads should be a sentence");
    }
}
