using System;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>
/// FEAT-INV-08's cut/copy/paste rules. The ones that matter are the refusals: a folder pasted into
/// its own descendant leaves that subtree hanging off nothing, and no part of the UI can then
/// reach it.
/// </summary>
public class InventoryClipboardTests
{
    private static readonly Guid Hats = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Winter = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Root = new("33333333-3333-3333-3333-333333333333");
    private static readonly Guid AnItem = new("44444444-4444-4444-4444-444444444444");

    [Fact]
    public void AnEmptyClipboardPastesNothing()
    {
        var clip = new InventoryClipboard();

        Assert.False(clip.HasContent);
        Assert.Equal(InventoryPasteCheck.Nothing, clip.CanPasteInto(Winter, null));
    }

    [Fact]
    public void CuttingAnItemCanBePastedIntoAFolder()
    {
        var clip = new InventoryClipboard();
        clip.Set(InventoryClipboardMode.Cut, AnItem, isFolder: false, "A hat", Root);

        Assert.True(clip.HasContent);
        Assert.Equal(InventoryPasteCheck.Ok, clip.CanPasteInto(Winter, new[] { Root }));
    }

    [Fact]
    public void CopyingAnItemIsAllowed()
    {
        // The common case, and the one the permission system decides -- not this class.
        var clip = new InventoryClipboard();
        clip.Set(InventoryClipboardMode.Copy, AnItem, isFolder: false, "A hat", Root);

        Assert.Equal(InventoryPasteCheck.Ok, clip.CanPasteInto(Winter, new[] { Root }));
    }

    [Fact]
    public void AFolderCannotBePastedIntoItself()
    {
        var clip = new InventoryClipboard();
        clip.Set(InventoryClipboardMode.Cut, Hats, isFolder: true, "Hats", Root);

        Assert.Equal(InventoryPasteCheck.IntoItself, clip.CanPasteInto(Hats, Array.Empty<Guid>()));
    }

    [Fact]
    public void AFolderCannotBePastedIntoSomethingItContains()
    {
        // Winter lives inside Hats. Moving Hats into Winter detaches both from the root, and the
        // tree no longer has a path to either -- there is no undo for that in any viewer.
        var clip = new InventoryClipboard();
        clip.Set(InventoryClipboardMode.Cut, Hats, isFolder: true, "Hats", Root);

        Assert.Equal(InventoryPasteCheck.IntoOwnDescendant,
                     clip.CanPasteInto(Winter, new[] { Hats, Root }));
    }

    [Fact]
    public void AFolderMayBeCutIntoAnUnrelatedFolder()
    {
        var clip = new InventoryClipboard();
        clip.Set(InventoryClipboardMode.Cut, Hats, isFolder: true, "Hats", Root);

        Assert.Equal(InventoryPasteCheck.Ok, clip.CanPasteInto(Winter, new[] { Root }));
    }

    [Fact]
    public void AFolderMayBeCopiedIntoAnUnrelatedFolder()
    {
        // FEAT-INV-09: a folder copy is a recursive rebuild -- create the folder, copy each item,
        // recurse -- which is exactly how the reference viewer does it. The clipboard's job is
        // only to say it is allowed; GridSession.CopyFolderAsync does it and counts what survived.
        var clip = new InventoryClipboard();
        clip.Set(InventoryClipboardMode.Copy, Hats, isFolder: true, "Hats", Root);

        Assert.Equal(InventoryPasteCheck.Ok, clip.CanPasteInto(Winter, new[] { Root }));
    }

    [Fact]
    public void ACopiedFolderStillCannotLandInsideItself()
    {
        // The self/descendant refusals are about the TREE, not about the mode -- and a copy into
        // its own subtree would recurse into the copy it is making.
        var clip = new InventoryClipboard();
        clip.Set(InventoryClipboardMode.Copy, Hats, isFolder: true, "Hats", Root);

        Assert.Equal(InventoryPasteCheck.IntoItself, clip.CanPasteInto(Hats, Array.Empty<Guid>()));
        Assert.Equal(InventoryPasteCheck.IntoOwnDescendant, clip.CanPasteInto(Winter, new[] { Hats, Root }));
    }

    [Fact]
    public void CuttingBackIntoTheSourceFolderIsANoOp()
    {
        var clip = new InventoryClipboard();
        clip.Set(InventoryClipboardMode.Cut, AnItem, isFolder: false, "A hat", Root);

        Assert.True(clip.IsNoOpInto(Root));
        Assert.False(clip.IsNoOpInto(Winter));
    }

    [Fact]
    public void CopyingIntoTheSourceFolderIsNotANoOp()
    {
        // It is how a duplicate is made, and treating it as pointless would silently do nothing.
        var clip = new InventoryClipboard();
        clip.Set(InventoryClipboardMode.Copy, AnItem, isFolder: false, "A hat", Root);

        Assert.False(clip.IsNoOpInto(Root));
    }

    [Fact]
    public void SettingNothingClearsTheClipboard()
    {
        var clip = new InventoryClipboard();
        clip.Set(InventoryClipboardMode.Cut, AnItem, isFolder: false, "A hat", Root);

        clip.Set(InventoryClipboardMode.None, AnItem, isFolder: false, "A hat", Root);
        Assert.False(clip.HasContent);

        clip.Set(InventoryClipboardMode.Cut, Guid.Empty, isFolder: false, "", Root);
        Assert.False(clip.HasContent);
    }

    [Fact]
    public void ClearForgetsEverything()
    {
        var clip = new InventoryClipboard();
        clip.Set(InventoryClipboardMode.Cut, Hats, isFolder: true, "Hats", Root);

        clip.Clear();

        Assert.False(clip.HasContent);
        Assert.Equal(Guid.Empty, clip.Id);
        Assert.Equal(string.Empty, clip.Name);
        Assert.Equal(InventoryClipboardMode.None, clip.Mode);
    }

    // ---- FEAT-INV-09: paste as link ---------------------------------------------------------
    //
    // The rules are the reference viewer's isClipboardPasteableAsLink (llinventorybridge.cpp:
    // 681-717) plus the two asset-id cases pasteLinkFromClipboard checks before it sends
    // anything. They are worth testing because every one of them is a case where the grid would
    // otherwise answer with a refusal the user never sees.

    private static InventoryClipboardFacts ItemFacts(
        int assetType, bool isLink = false, Guid assetId = default, bool inLibrary = false)
        => new(isLink, assetType, assetId == default ? Guid.NewGuid() : assetId, inLibrary, false);

    [Fact]
    public void AnOrdinaryItemCanBePastedAsALink()
    {
        var clip = new InventoryClipboard();
        clip.Set(InventoryClipboardMode.Copy, AnItem, isFolder: false, "A hat", Root,
                 ItemFacts(AssetTypeIds.Object));

        Assert.Equal(InventoryPasteCheck.Ok, clip.CanPasteLinkInto(Winter));
    }

    [Fact]
    public void ANoCopyItemCanStillBePastedAsALink()
    {
        // A link is not a copy -- it is a second name for the one item -- so copy permission has
        // nothing to say about it. This is the whole reason the entry exists next to Paste.
        var clip = new InventoryClipboard();
        clip.Set(InventoryClipboardMode.Copy, AnItem, isFolder: false, "A no-copy hat", Root,
                 ItemFacts(AssetTypeIds.Object));

        Assert.Equal(InventoryPasteCheck.Ok, clip.CanPasteLinkInto(Winter));
    }

    [Fact]
    public void ALinkCannotBeLinkedAgain()
    {
        var clip = new InventoryClipboard();
        clip.Set(InventoryClipboardMode.Copy, AnItem, isFolder: false, "A hat", Root,
                 ItemFacts(AssetTypeIds.Link, isLink: true));

        Assert.Equal(InventoryPasteCheck.AlreadyALink, clip.CanPasteLinkInto(Winter));
    }

    [Fact]
    public void AMeshCannotBeLinked()
    {
        // Mesh is one of the handful of types the viewer's asset dictionary marks can_link=false.
        var clip = new InventoryClipboard();
        clip.Set(InventoryClipboardMode.Copy, AnItem, isFolder: false, "A mesh", Root,
                 ItemFacts(AssetTypeIds.Mesh));

        Assert.Equal(InventoryPasteCheck.CannotBeLinked, clip.CanPasteLinkInto(Winter));
    }

    [Fact]
    public void AnUnknownTypeIsRefusedRatherThanAttempted()
    {
        // The rule is an allow-list: a type we have never heard of is not linkable by default.
        var clip = new InventoryClipboard();
        clip.Set(InventoryClipboardMode.Copy, AnItem, isFolder: false, "?", Root, ItemFacts(-1));

        Assert.Equal(InventoryPasteCheck.CannotBeLinked, clip.CanPasteLinkInto(Winter));
    }

    [Fact]
    public void ANotecardWithNoAssetYetCannotBeLinked()
    {
        // AIS answers this one with "Cannot link to items with a NULL asset_id", which is why the
        // viewer refuses it up front instead of sending it.
        var clip = new InventoryClipboard();
        clip.Set(InventoryClipboardMode.Copy, AnItem, isFolder: false, "New note", Root,
                 new InventoryClipboardFacts(false, AssetTypeIds.Notecard, Guid.Empty, false, false));

        Assert.Equal(InventoryPasteCheck.CannotBeLinked, clip.CanPasteLinkInto(Winter));
    }

    [Fact]
    public void AnObjectWithNoAssetIdIsStillLinkable()
    {
        // Only notecards and materials are gated on the asset existing; a momentarily empty asset
        // id on anything else is the indexing lag this project has been bitten by before.
        var clip = new InventoryClipboard();
        clip.Set(InventoryClipboardMode.Copy, AnItem, isFolder: false, "A hat", Root,
                 new InventoryClipboardFacts(false, AssetTypeIds.Object, Guid.Empty, false, false));

        Assert.Equal(InventoryPasteCheck.Ok, clip.CanPasteLinkInto(Winter));
    }

    [Fact]
    public void ALibraryItemCannotBeLinked()
    {
        // It is not in your inventory at all, so there is nothing of yours for a link to name.
        var clip = new InventoryClipboard();
        clip.Set(InventoryClipboardMode.Copy, AnItem, isFolder: false, "Starter hat", Root,
                 ItemFacts(AssetTypeIds.Object, inLibrary: true));

        Assert.Equal(InventoryPasteCheck.LibraryCannotBeLinked, clip.CanPasteLinkInto(Winter));
    }

    [Fact]
    public void AnOrdinaryFolderCanBePastedAsALink()
    {
        var clip = new InventoryClipboard();
        clip.Set(InventoryClipboardMode.Copy, Hats, isFolder: true, "Hats", Root,
                 new InventoryClipboardFacts(false, AssetTypeIds.Folder, Guid.Empty, false, false));

        Assert.Equal(InventoryPasteCheck.Ok, clip.CanPasteLinkInto(Winter));
    }

    [Fact]
    public void ASystemFolderCannotBePastedAsALink()
    {
        var clip = new InventoryClipboard();
        clip.Set(InventoryClipboardMode.Copy, Hats, isFolder: true, "Objects", Root,
                 new InventoryClipboardFacts(false, AssetTypeIds.Folder, Guid.Empty, false, true));

        Assert.Equal(InventoryPasteCheck.CannotBeLinked, clip.CanPasteLinkInto(Winter));
    }

    [Fact]
    public void AFolderLinkIntoItsOwnSubtreeIsAllowed()
    {
        // Unlike a MOVE, a link detaches nothing: the original keeps its place and the link is a
        // leaf. The reference viewer does not test for it either.
        var clip = new InventoryClipboard();
        clip.Set(InventoryClipboardMode.Copy, Hats, isFolder: true, "Hats", Root,
                 new InventoryClipboardFacts(false, AssetTypeIds.Folder, Guid.Empty, false, false));

        Assert.Equal(InventoryPasteCheck.Ok, clip.CanPasteLinkInto(Winter));
    }

    [Fact]
    public void AnEmptyClipboardLinksNothing()
    {
        var clip = new InventoryClipboard();

        Assert.Equal(InventoryPasteCheck.Nothing, clip.CanPasteLinkInto(Winter));
    }

    [Fact]
    public void TheLinkableTypeListMatchesTheViewersDictionary()
    {
        // Spot-checks of llassettype.cpp's can_link column, in both directions.
        Assert.True(InventoryLinkRules.CanLink(AssetTypeIds.Texture));
        Assert.True(InventoryLinkRules.CanLink(AssetTypeIds.Clothing));
        Assert.True(InventoryLinkRules.CanLink(AssetTypeIds.Bodypart));
        Assert.True(InventoryLinkRules.CanLink(AssetTypeIds.Folder));
        Assert.True(InventoryLinkRules.CanLink(AssetTypeIds.Settings));

        Assert.False(InventoryLinkRules.CanLink(AssetTypeIds.Link));
        Assert.False(InventoryLinkRules.CanLink(AssetTypeIds.LinkFolder));
        Assert.False(InventoryLinkRules.CanLink(AssetTypeIds.Mesh));
        Assert.False(InventoryLinkRules.CanLink(22));  // simstate
        Assert.False(InventoryLinkRules.CanLink(255)); // unknown
    }

    // ---------------------------------------------------------------- BUG-INV-10: what may be cut

    private static InventoryClipboardFacts Facts(bool inLibrary = false, bool systemFolder = false)
        => new(IsLink: false, AssetType: AssetTypeIds.Folder, AssetId: Guid.Empty,
               IsInLibrary: inLibrary, IsSystemFolder: systemFolder);

    [Fact]
    public void AnOrdinaryFolderMayBeCut()
    {
        Assert.Equal(InventoryCutCheck.Ok, InventoryClipboard.CanCut(Facts(), isFolder: true));
    }

    [Fact]
    public void ALibraryFolderMayNotBeCut()
    {
        // Reported in-world: Ctrl+X on a #Library folder produced an EMPTY folder at the
        // destination. The Library is not the agent's inventory, so a move out of it cannot
        // succeed on any grid -- llinventoryfunctions.cpp:828 refuses it at the root test.
        Assert.Equal(InventoryCutCheck.InLibrary,
                     InventoryClipboard.CanCut(Facts(inLibrary: true), isFolder: true));
    }

    [Fact]
    public void ALibraryITEMMayNotBeCutEither()
    {
        // The viewer uses the same root test for items, under the literal comment
        // "Can't delete an item that's in the library." (llinventoryfunctions.cpp:745).
        Assert.Equal(InventoryCutCheck.InLibrary,
                     InventoryClipboard.CanCut(Facts(inLibrary: true), isFolder: false));
    }

    [Fact]
    public void ASystemFolderMayNotBeCut()
    {
        Assert.Equal(InventoryCutCheck.SystemFolder,
                     InventoryClipboard.CanCut(Facts(systemFolder: true), isFolder: true));
    }

    [Fact]
    public void ASystemFolderInTheLibraryIsExplainedByTheLibrary()
    {
        // "Clothing" exists under both roots. The reason the user can act on is the Library one --
        // copy it -- so that is the one the check reports.
        Assert.Equal(InventoryCutCheck.InLibrary,
                     InventoryClipboard.CanCut(Facts(inLibrary: true, systemFolder: true), isFolder: true));
    }

    [Fact]
    public void TheSystemFolderRuleAppliesToFoldersOnly()
    {
        // An ITEM never carries a folder type, so the flag cannot speak about one.
        Assert.Equal(InventoryCutCheck.Ok,
                     InventoryClipboard.CanCut(Facts(systemFolder: true), isFolder: false));
    }
}
