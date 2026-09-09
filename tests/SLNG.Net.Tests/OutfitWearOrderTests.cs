using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>
/// FEAT-AVATAR-01 / FEAT-INV-04: the order a saved outfit's items are put on in.
///
/// <para>This is not cosmetic. <c>GridSession.WearWearableAsync</c> gives a newly worn layer the
/// index "however many of that type are already on", so the sequence these ids come out in becomes
/// the avatar's actual layer stack — wear a five-layer tattoo outfit in the wrong order and the
/// wrong face ends up on top (the failure <see cref="WearableLayerOrder"/> was written for). The
/// grid half cannot be tested offline; this decision can, so it is pinned here.</para>
/// </summary>
public class OutfitWearOrderTests
{
    private const int Clothing = 5;   // LibreMetaverse.AssetType.Clothing
    private const int Bodypart = 13;  // LibreMetaverse.AssetType.Bodypart
    private const int Object = 6;     // LibreMetaverse.AssetType.Object

    private const int Tattoo = 20;
    private const int Shirt = 4;

    private static GridSession.OutfitWearRow Row(
        Guid id, int assetType, int wearableType = -1, string? token = null, string name = "item") =>
        new(id, assetType, wearableType, token, name);

    [Fact]
    public void BodyPartsGoOnFirstAndAttachmentsLast()
    {
        var attachment = Guid.NewGuid();
        var shirt = Guid.NewGuid();
        var skin = Guid.NewGuid();

        var order = GridSession.OrderOutfitForWearing(new[]
        {
            Row(attachment, Object),
            Row(shirt, Clothing, Shirt, WearableLayerOrder.BuildOrderString(Shirt, 0)),
            Row(skin, Bodypart),
        });

        Assert.Equal(new[] { skin, shirt, attachment }, order);
    }

    [Fact]
    public void ClothingOfOneTypeIsWornBottomLayerFirst()
    {
        // Saved outfit lists them in whatever order AIS returned; the tokens are the truth.
        var top = Guid.NewGuid();
        var middle = Guid.NewGuid();
        var bottom = Guid.NewGuid();

        var order = GridSession.OrderOutfitForWearing(new[]
        {
            Row(top, Clothing, Tattoo, WearableLayerOrder.BuildOrderString(Tattoo, 2), "top"),
            Row(bottom, Clothing, Tattoo, WearableLayerOrder.BuildOrderString(Tattoo, 0), "bottom"),
            Row(middle, Clothing, Tattoo, WearableLayerOrder.BuildOrderString(Tattoo, 1), "middle"),
        });

        Assert.Equal(new[] { bottom, middle, top }, order);
    }

    [Fact]
    public void UntokenedLayersSinkBelowTokenedOnes()
    {
        // An outfit saved by something that did not write the order token still has to end up
        // somewhere deterministic -- WearablesOrderComparator sorts those below, by name.
        var tokened = Guid.NewGuid();
        var untokenedB = Guid.NewGuid();
        var untokenedA = Guid.NewGuid();

        var order = GridSession.OrderOutfitForWearing(new[]
        {
            Row(untokenedB, Clothing, Tattoo, null, "b"),
            Row(tokened, Clothing, Tattoo, WearableLayerOrder.BuildOrderString(Tattoo, 0), "z"),
            Row(untokenedA, Clothing, Tattoo, null, "a"),
        });

        Assert.Equal(new[] { tokened, untokenedA, untokenedB }, order);
    }

    [Fact]
    public void DifferentClothingTypesDoNotInterleave()
    {
        var shirt0 = Guid.NewGuid();
        var shirt1 = Guid.NewGuid();
        var tattoo0 = Guid.NewGuid();

        var order = GridSession.OrderOutfitForWearing(new[]
        {
            Row(shirt0, Clothing, Shirt, WearableLayerOrder.BuildOrderString(Shirt, 0)),
            Row(tattoo0, Clothing, Tattoo, WearableLayerOrder.BuildOrderString(Tattoo, 0)),
            Row(shirt1, Clothing, Shirt, WearableLayerOrder.BuildOrderString(Shirt, 1)),
        });

        // Groups keep first-appearance order; within a group the tokens decide.
        Assert.Equal(new[] { shirt0, shirt1, tattoo0 }, order);
    }

    [Fact]
    public void UnresolvedRowsAreKeptAndSortLast()
    {
        // The inventory store has not resolved this link's target yet, so its type is unknown.
        // Dropping it would silently lose an item from the outfit; AttachItemAsync re-classifies
        // it when it gets there.
        var unknown = Guid.NewGuid();
        var skin = Guid.NewGuid();

        var order = GridSession.OrderOutfitForWearing(new[] { Row(unknown, -1), Row(skin, Bodypart) });

        Assert.Equal(new[] { skin, unknown }, order);
    }

    [Fact]
    public void EmptyOutfitProducesNoWears()
    {
        Assert.Empty(GridSession.OrderOutfitForWearing(Array.Empty<GridSession.OutfitWearRow>()));
    }

    [Fact]
    public void EveryItemIsWornExactlyOnce()
    {
        var rows = new List<GridSession.OutfitWearRow>
        {
            Row(Guid.NewGuid(), Bodypart),
            Row(Guid.NewGuid(), Bodypart),
            Row(Guid.NewGuid(), Clothing, Shirt, WearableLayerOrder.BuildOrderString(Shirt, 0)),
            Row(Guid.NewGuid(), Clothing, Tattoo, null),
            Row(Guid.NewGuid(), Object),
            Row(Guid.NewGuid(), -1),
        };

        var order = GridSession.OrderOutfitForWearing(rows);

        Assert.Equal(rows.Count, order.Count);
        Assert.Equal(rows.Select(r => r.ItemId).OrderBy(g => g), order.OrderBy(g => g));
    }
}
