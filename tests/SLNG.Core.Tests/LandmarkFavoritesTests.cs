using System;
using System.Linq;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

public class LandmarkFavoritesTests
{
    [Fact]
    public void Add_ValidItem_AddsSuccessfully()
    {
        var list = new LandmarkFavoritesList();
        var item = new LandmarkFavoriteItem(Guid.NewGuid(), Guid.NewGuid(), "Puris Town");

        bool added = list.Add(item);

        Assert.True(added);
        Assert.Single(list.Items);
        Assert.Equal("Puris Town", list.Items[0].Name);
    }

    [Fact]
    public void Add_DuplicateAssetId_RefusesDuplicate()
    {
        var list = new LandmarkFavoritesList();
        var assetId = Guid.NewGuid();
        list.Add(new LandmarkFavoriteItem(Guid.NewGuid(), assetId, "Spot 1"));

        bool added = list.Add(new LandmarkFavoriteItem(Guid.NewGuid(), assetId, "Spot 2"));

        Assert.False(added);
        Assert.Single(list.Items);
    }

    [Fact]
    public void Add_DuplicateItemId_RefusesDuplicate()
    {
        var list = new LandmarkFavoritesList();
        var itemId = Guid.NewGuid();
        list.Add(new LandmarkFavoriteItem(itemId, Guid.NewGuid(), "Spot 1"));

        bool added = list.Add(new LandmarkFavoriteItem(itemId, Guid.NewGuid(), "Spot 2"));

        Assert.False(added);
        Assert.Single(list.Items);
    }

    [Fact]
    public void Add_EmptyName_RefusesAddition()
    {
        var list = new LandmarkFavoritesList();
        bool added = list.Add(new LandmarkFavoriteItem(Guid.NewGuid(), Guid.NewGuid(), "   "));

        Assert.False(added);
        Assert.Empty(list.Items);
    }

    [Fact]
    public void Add_OverlyLongName_Truncates()
    {
        var list = new LandmarkFavoritesList();
        string longName = new string('A', 100);
        list.Add(new LandmarkFavoriteItem(Guid.NewGuid(), Guid.NewGuid(), longName));

        Assert.Equal(LandmarkFavoritesList.MaxNameLength, list.Items[0].Name.Length);
    }

    [Fact]
    public void Remove_ByItemIdOrAssetId_RemovesCorrectly()
    {
        var list = new LandmarkFavoritesList();
        var itemId1 = Guid.NewGuid();
        var assetId1 = Guid.NewGuid();
        var itemId2 = Guid.NewGuid();
        var assetId2 = Guid.NewGuid();

        list.Add(new LandmarkFavoriteItem(itemId1, assetId1, "Item 1"));
        list.Add(new LandmarkFavoriteItem(itemId2, assetId2, "Item 2"));

        Assert.True(list.Remove(itemId1));
        Assert.Single(list.Items);
        Assert.Equal("Item 2", list.Items[0].Name);

        Assert.True(list.Remove(assetId2));
        Assert.Empty(list.Items);
    }

    [Fact]
    public void Move_ReordersElements()
    {
        var list = new LandmarkFavoritesList();
        list.Add(new LandmarkFavoriteItem(Guid.NewGuid(), Guid.NewGuid(), "A"));
        list.Add(new LandmarkFavoriteItem(Guid.NewGuid(), Guid.NewGuid(), "B"));
        list.Add(new LandmarkFavoriteItem(Guid.NewGuid(), Guid.NewGuid(), "C"));

        bool moved = list.Move(0, 2);

        Assert.True(moved);
        Assert.Equal("B", list.Items[0].Name);
        Assert.Equal("C", list.Items[1].Name);
        Assert.Equal("A", list.Items[2].Name);
    }

    [Fact]
    public void JsonSerialization_RoundTripPreservesItems()
    {
        var list = new LandmarkFavoritesList();
        var id1 = Guid.NewGuid();
        var asset1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var asset2 = Guid.NewGuid();

        list.Add(new LandmarkFavoriteItem(id1, asset1, "Welcome Hub"));
        list.Add(new LandmarkFavoriteItem(id2, asset2, "Sandbox 42"));

        string json = list.ToJson();
        var restored = LandmarkFavoritesList.FromJson(json);

        Assert.Equal(2, restored.Items.Count);
        Assert.Equal(id1, restored.Items[0].ItemId);
        Assert.Equal(asset1, restored.Items[0].AssetId);
        Assert.Equal("Welcome Hub", restored.Items[0].Name);
        Assert.Equal(id2, restored.Items[1].ItemId);
        Assert.Equal(asset2, restored.Items[1].AssetId);
        Assert.Equal("Sandbox 42", restored.Items[1].Name);
    }

    [Fact]
    public void JsonSerialization_InvalidJson_YieldsEmptyList()
    {
        var list = LandmarkFavoritesList.FromJson("invalid { [ json");
        Assert.Empty(list.Items);

        var listNull = LandmarkFavoritesList.FromJson(null);
        Assert.Empty(listNull.Items);
    }
}
