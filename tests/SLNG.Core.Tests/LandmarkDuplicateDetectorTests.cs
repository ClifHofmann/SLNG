using System;
using System.Collections.Generic;
using System.Linq;
using SLNG.Core.Landmarks;
using Xunit;

namespace SLNG.Core.Tests;

public class LandmarkDuplicateDetectorTests
{
    [Fact]
    public void FindDuplicates_with_no_items_returns_empty()
    {
        var result = LandmarkDuplicateDetector.FindDuplicates(Array.Empty<LandmarkInventoryItem>());
        Assert.Empty(result);
    }

    [Fact]
    public void FindDuplicates_detects_exact_asset_duplicates()
    {
        var assetId = Guid.NewGuid();
        var item1 = new LandmarkInventoryItem(Guid.NewGuid(), Guid.NewGuid(), assetId, "Club A", "Landmarks", "Landmarks");
        var item2 = new LandmarkInventoryItem(Guid.NewGuid(), Guid.NewGuid(), assetId, "Club A (copy)", "Received Items", "Received Items/Box");

        var result = LandmarkDuplicateDetector.FindDuplicates(new[] { item1, item2 });

        Assert.Single(result);
        var group = result[0];
        Assert.Equal(LandmarkDuplicateMatchKind.ExactAsset, group.MatchKind);
        Assert.Equal(2, group.Items.Count);
        Assert.Equal(item1.Id, group.RecommendedKeepItemId); // item1 is in Landmarks folder, item2 in Received Items
    }

    [Fact]
    public void FindDuplicates_detects_exact_name_duplicates_across_distinct_assets()
    {
        var item1 = new LandmarkInventoryItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Frank's Place", "Clubs", "Landmarks/Clubs");
        var item2 = new LandmarkInventoryItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "frank's place ", "Unpack", "Received Items/Unpack");

        var result = LandmarkDuplicateDetector.FindDuplicates(new[] { item1, item2 });

        Assert.Single(result);
        var group = result[0];
        Assert.Equal(LandmarkDuplicateMatchKind.ExactName, group.MatchKind);
        Assert.Equal(2, group.Items.Count);
        Assert.Equal(item1.Id, group.RecommendedKeepItemId);
    }

    [Fact]
    public void ScoreAndSelectKeepItem_prefers_favorite_item()
    {
        var favId = Guid.NewGuid();
        var item1 = new LandmarkInventoryItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Beach", "Landmarks", "Landmarks");
        var item2 = new LandmarkInventoryItem(favId, Guid.NewGuid(), Guid.NewGuid(), "Beach", "Random", "Random/Folder");

        var favorites = new HashSet<Guid> { favId };
        var result = LandmarkDuplicateDetector.FindDuplicates(new[] { item1, item2 }, favoriteIds: favorites);

        Assert.Single(result);
        Assert.Equal(favId, result[0].RecommendedKeepItemId);
    }

    [Fact]
    public void ScoreAndSelectKeepItem_prefers_primary_landmarks_folder()
    {
        var item1 = new LandmarkInventoryItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Store", "Shops", "Landmarks/Shops");
        var item2 = new LandmarkInventoryItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Store", "Objects", "Objects/Gifts");

        var result = LandmarkDuplicateDetector.FindDuplicates(new[] { item1, item2 });

        Assert.Single(result);
        Assert.Equal(item1.Id, result[0].RecommendedKeepItemId);
    }

    [Fact]
    public void ScoreAndSelectKeepItem_prefers_newest_creation_date()
    {
        var oldDate = new DateTime(2021, 5, 10, 12, 0, 0, DateTimeKind.Utc);
        var newDate = new DateTime(2026, 3, 15, 14, 30, 0, DateTimeKind.Utc);
        var itemOld = new LandmarkInventoryItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Club Nova", "Landmarks", "Landmarks/Clubs", oldDate);
        var itemNew = new LandmarkInventoryItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Club Nova", "Received Items", "Received Items/Gifts", newDate);

        var result = LandmarkDuplicateDetector.FindDuplicates(new[] { itemOld, itemNew });

        Assert.Single(result);
        Assert.Equal(itemNew.Id, result[0].RecommendedKeepItemId);
    }

    [Fact]
    public void Unique_items_are_not_reported_as_duplicates()
    {
        var item1 = new LandmarkInventoryItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Place A", "Landmarks", "Landmarks");
        var item2 = new LandmarkInventoryItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Place B", "Landmarks", "Landmarks");

        var result = LandmarkDuplicateDetector.FindDuplicates(new[] { item1, item2 });

        Assert.Empty(result);
    }
}
