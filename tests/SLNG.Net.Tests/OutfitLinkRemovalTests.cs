using System;
using System.Collections.Generic;
using System.Linq;
using SLNG.Core;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>
/// FEAT-INV-05, "Aus diesem Outfit entfernen". The decision half of
/// <see cref="GridSession.RemoveItemFromOutfitFolderAsync"/> is pinned here because its failure
/// modes are silent and destructive in opposite directions: delete too little and the item stays in
/// the outfit while the UI says it was removed; delete the wrong entry and a real inventory item is
/// gone over a menu entry that promised to edit an outfit.
/// </summary>
public class OutfitLinkRemovalTests
{
    private static InventoryEntry Link(Guid linkId, Guid targetId, string name = "link") =>
        new(linkId, Guid.NewGuid(), Guid.NewGuid(), name, IsFolder: false, PreferredFolderType: -1,
            AssetId: Guid.Empty, AssetType: 6, InventoryType: 6, IsLink: true, LinkTargetId: targetId,
            CanCopy: true, CanModify: true, CanTransfer: true);

    private static InventoryEntry Item(Guid id, string name = "item") =>
        new(id, Guid.NewGuid(), Guid.NewGuid(), name, IsFolder: false, PreferredFolderType: -1,
            AssetId: Guid.NewGuid(), AssetType: 6, InventoryType: 6, IsLink: false, LinkTargetId: Guid.Empty,
            CanCopy: true, CanModify: true, CanTransfer: true);

    private static InventoryEntry Folder(Guid id, string name = "folder") =>
        new(id, Guid.NewGuid(), Guid.NewGuid(), name, IsFolder: true, PreferredFolderType: -1,
            AssetId: Guid.Empty, AssetType: 0, InventoryType: 0, IsLink: false, LinkTargetId: Guid.Empty,
            CanCopy: true, CanModify: true, CanTransfer: true);

    [Fact]
    public void SelectsTheLinkPointingAtTheItem()
    {
        var wanted = Guid.NewGuid();
        var link = Guid.NewGuid();
        var children = new[] { Link(link, wanted), Link(Guid.NewGuid(), Guid.NewGuid()) };

        var (linkIds, nonLink) = GridSession.SelectOutfitLinksToRemove(children, wanted);

        Assert.Equal(new[] { link }, linkIds);
        Assert.Equal(0, nonLink);
    }

    [Fact]
    public void SelectsEveryDuplicateLinkToTheSameItem()
    {
        var wanted = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var (linkIds, _) = GridSession.SelectOutfitLinksToRemove(
            new[] { Link(a, wanted), Link(b, wanted) }, wanted);

        // Leaving the second link behind would read as "the remove did nothing".
        Assert.Equal(2, linkIds.Count);
        Assert.Contains(a, linkIds);
        Assert.Contains(b, linkIds);
    }

    [Fact]
    public void NeverSelectsALinkToADifferentItem()
    {
        var wanted = Guid.NewGuid();
        var children = new[] { Link(Guid.NewGuid(), Guid.NewGuid()), Link(Guid.NewGuid(), Guid.NewGuid()) };

        var (linkIds, nonLink) = GridSession.SelectOutfitLinksToRemove(children, wanted);

        Assert.Empty(linkIds);
        Assert.Equal(0, nonLink);
    }

    [Fact]
    public void AMatchingRealItemIsReportedButNeverSelectedForDeletion()
    {
        // An outfit folder should hold only links, but if a real item has been dropped into one,
        // deleting it would destroy inventory over an "edit this outfit" menu entry.
        var wanted = Guid.NewGuid();

        var (linkIds, nonLink) = GridSession.SelectOutfitLinksToRemove(new[] { Item(wanted) }, wanted);

        Assert.Empty(linkIds);
        Assert.Equal(1, nonLink);
    }

    [Fact]
    public void FoldersAreIgnoredEvenWhenTheIdMatches()
    {
        var wanted = Guid.NewGuid();

        var (linkIds, nonLink) = GridSession.SelectOutfitLinksToRemove(new[] { Folder(wanted) }, wanted);

        Assert.Empty(linkIds);
        Assert.Equal(0, nonLink);
    }

    [Fact]
    public void AnEmptyItemIdSelectsNothing()
    {
        // Guards the caller's early-out: a Guid.Empty target must never match a link whose
        // LinkTargetId is also empty (a broken link), or "remove from outfit" on a row with no id
        // would delete every broken link in the folder.
        var children = new[] { Link(Guid.NewGuid(), Guid.Empty), Item(Guid.Empty) };

        var (linkIds, nonLink) = GridSession.SelectOutfitLinksToRemove(children, Guid.Empty);

        Assert.Empty(linkIds);
        Assert.Equal(0, nonLink);
    }

    [Fact]
    public void ALinkWithNoTargetFallsBackToItsOwnIdAndIsNotMatchedByAnotherItem()
    {
        var wanted = Guid.NewGuid();
        var broken = Link(Guid.NewGuid(), Guid.Empty);

        var (linkIds, _) = GridSession.SelectOutfitLinksToRemove(new[] { broken }, wanted);

        Assert.Empty(linkIds);
    }
}
