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
    public void CopyingAWholeFolderIsRefusedRatherThanHalfDone()
    {
        // Cutting a folder is one reparent; copying one is a recursive rebuild whose per-item
        // permissions decide what survives. Refusing it says so; attempting it would fail halfway
        // and look like it worked.
        var clip = new InventoryClipboard();
        clip.Set(InventoryClipboardMode.Copy, Hats, isFolder: true, "Hats", Root);

        Assert.Equal(InventoryPasteCheck.FolderCopyUnsupported, clip.CanPasteInto(Winter, new[] { Root }));
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
}
