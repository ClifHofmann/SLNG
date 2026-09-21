using System;
using System.Collections.Generic;

namespace SLNG.Core;

/// <summary>What a pending inventory clipboard entry will do when it is pasted.</summary>
public enum InventoryClipboardMode
{
    /// <summary>Nothing is on the clipboard.</summary>
    None,

    /// <summary>Paste makes a second copy and leaves the original where it is.</summary>
    Copy,

    /// <summary>Paste moves it. The original is gone from where it was.</summary>
    Cut,
}

/// <summary>Why a paste cannot happen, or that it can.</summary>
public enum InventoryPasteCheck
{
    Ok,

    /// <summary>Nothing has been cut or copied.</summary>
    Nothing,

    /// <summary>A folder cannot be pasted into itself.</summary>
    IntoItself,

    /// <summary>A folder cannot be pasted into something it contains.</summary>
    IntoOwnDescendant,

    /// <summary>Copying a whole folder is not supported yet; cutting one is.</summary>
    FolderCopyUnsupported,
}

/// <summary>
/// The inventory's own cut/copy/paste clipboard — one item or folder, and what pasting it means.
/// </summary>
/// <remarks>
/// Separate from the system clipboard on purpose: this holds an inventory id, not text, and
/// pasting it performs a grid operation rather than inserting characters. Ctrl+C on a folder in
/// the inventory is a different act from Ctrl+C in a text field, and the two must not share a
/// buffer.
///
/// <para>Engine- and protocol-free so the rules that make a paste illegal can be tested without a
/// grid — and they are the part worth testing. Pasting a folder into its own descendant detaches
/// that whole subtree from the inventory root, and there is no UI left to get it back with.</para>
/// </remarks>
public sealed class InventoryClipboard
{
    public InventoryClipboardMode Mode { get; private set; } = InventoryClipboardMode.None;

    /// <summary>The item or folder waiting to be pasted.</summary>
    public Guid Id { get; private set; }

    public bool IsFolder { get; private set; }

    /// <summary>Its name, for the status line — "„Hats“ eingefügt" beats "eingefügt".</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>The folder it was taken from, so a cut-and-paste back into the same place can be
    /// recognised as the no-op it is.</summary>
    public Guid SourceFolderId { get; private set; }

    public bool HasContent => Mode != InventoryClipboardMode.None && Id != Guid.Empty;

    public void Set(InventoryClipboardMode mode, Guid id, bool isFolder, string name, Guid sourceFolderId)
    {
        if (mode == InventoryClipboardMode.None || id == Guid.Empty) { Clear(); return; }

        Mode = mode;
        Id = id;
        IsFolder = isFolder;
        Name = name ?? string.Empty;
        SourceFolderId = sourceFolderId;
    }

    public void Clear()
    {
        Mode = InventoryClipboardMode.None;
        Id = Guid.Empty;
        IsFolder = false;
        Name = string.Empty;
        SourceFolderId = Guid.Empty;
    }

    /// <summary>
    /// Whether the clipboard's content may be pasted into a folder.
    /// </summary>
    /// <param name="targetFolderId">Where the user is pasting.</param>
    /// <param name="targetAncestors">Every folder from <paramref name="targetFolderId"/> up to the
    /// inventory root, in any order. Supplied by the caller because only the UI knows the tree;
    /// this class must not reach for one.</param>
    public InventoryPasteCheck CanPasteInto(Guid targetFolderId, IReadOnlyCollection<Guid>? targetAncestors)
    {
        if (!HasContent) return InventoryPasteCheck.Nothing;
        if (targetFolderId == Guid.Empty) return InventoryPasteCheck.Nothing;

        if (IsFolder)
        {
            if (targetFolderId == Id) return InventoryPasteCheck.IntoItself;

            if (targetAncestors != null)
            {
                foreach (var ancestor in targetAncestors)
                    if (ancestor == Id) return InventoryPasteCheck.IntoOwnDescendant;
            }

            // Cutting a folder is one reparent. Copying one means recreating it and every item
            // inside it, one request each, with per-item permissions deciding which survive — a
            // different feature, and one that fails halfway in a way a user cannot see. Refusing
            // it plainly beats half-doing it.
            if (Mode == InventoryClipboardMode.Copy) return InventoryPasteCheck.FolderCopyUnsupported;
        }

        return InventoryPasteCheck.Ok;
    }

    /// <summary>Whether pasting into this folder would achieve nothing — a cut put back exactly
    /// where it came from. A COPY into the same folder is not pointless: it is how a duplicate is
    /// made.</summary>
    public bool IsNoOpInto(Guid targetFolderId)
        => Mode == InventoryClipboardMode.Cut && targetFolderId != Guid.Empty && targetFolderId == SourceFolderId;
}
