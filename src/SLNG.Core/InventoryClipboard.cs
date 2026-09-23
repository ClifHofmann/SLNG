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

    /// <summary>Paste-as-link only: the thing on the clipboard is itself a link, and SL has no
    /// link to a link.</summary>
    AlreadyALink,

    /// <summary>Paste-as-link only: this kind of thing cannot be linked at all.</summary>
    CannotBeLinked,

    /// <summary>Paste-as-link only: a <c>#Library</c> item is not in your inventory, so there is
    /// nothing of yours for a link to point at.</summary>
    LibraryCannotBeLinked,
}

/// <summary>Why something may not be CUT, or that it may. BUG-INV-10.</summary>
/// <remarks>
/// Separate from <see cref="InventoryPasteCheck"/> because it is answered at a different moment:
/// a paste is judged against a destination, a cut against the thing itself, before anything goes
/// on the clipboard at all. The reference viewer draws the same line —
/// <c>LLInvFVBridge::cutToClipboard</c> (llinventorybridge.cpp:349-372) refuses before the
/// clipboard is touched, so a refused cut cannot leave a stale entry armed behind it.
/// </remarks>
public enum InventoryCutCheck
{
    Ok,

    /// <summary>It lives under <c>#Library</c>, which is not your inventory and cannot be moved
    /// out of.</summary>
    InLibrary,

    /// <summary>A folder the grid maintains itself (Objects, Clothing, #Outfits ...).</summary>
    SystemFolder,
}

/// <summary>
/// The facts about a clipboard entry that decide whether it may be pasted <b>as a link</b>.
/// </summary>
/// <remarks>
/// Collected when the thing is put on the clipboard rather than when it is pasted: by paste time
/// the row it came from may have been scrolled away, refreshed or freed. Kept as plain values so
/// this stays testable without a grid — <see cref="InventoryClipboard"/> must not reach for an
/// inventory store.
/// </remarks>
public readonly record struct InventoryClipboardFacts(
    // The entry is itself a link.
    bool IsLink,
    // Its SL asset type (see AssetTypeIds); -1 when unknown.
    int AssetType,
    // Its asset id -- empty on a notecard or material the server has not written yet.
    Guid AssetId,
    // It lives under #Library, which is not the agent's inventory.
    bool IsInLibrary,
    // It is a folder the grid maintains itself (Objects, Clothing, #Outfits ...).
    bool IsSystemFolder);

/// <summary>Which inventory types may have a link made to them.</summary>
/// <remarks>
/// The list is the <c>can_link</c> column of the viewer's own asset dictionary
/// (<c>llassettype.cpp:71-105</c>), which is what <c>LLAssetType::lookupCanLink</c> reads and
/// <c>LLInvFVBridge::isClipboardPasteableAsLink</c> (llinventorybridge.cpp:681-717) tests before
/// it offers "Paste As Link". Everything not in the dictionary is unlinkable too, so this is an
/// allow-list rather than a block-list: a type we have never heard of is refused, not attempted.
/// </remarks>
public static class InventoryLinkRules
{
    // texture, sound, calling card, landmark, script, clothing, object, notecard, folder,
    // lsl text, lsl bytecode, tga texture, body part, wav, tga image, jpeg, animation, gesture,
    // settings, material, gltf, gltf binary.
    private static readonly int[] Linkable =
    {
        0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 11, 12, 13, 17, 18, 19, 20, 21, 56, 57, 58, 59,
    };

    public static bool CanLink(int assetType) => Array.IndexOf(Linkable, assetType) >= 0;

    /// <summary>Whether a link to this type needs the asset to exist first. AIS answers a link to
    /// an asset-less notecard or material with <c>Cannot link to items with a NULL asset_id</c>,
    /// which the viewer pre-empts with its own CantLinkNotecard / CantLinkMaterial notices
    /// (llinventorybridge.cpp:4420-4435).</summary>
    public static bool NeedsAssetToLink(int assetType)
        => assetType is AssetTypeIds.Notecard or AssetTypeIds.Material;
}

/// <summary>
/// The inventory's own cut/copy/paste clipboard — one item or folder, and what pasting it means.
/// </summary>
/// <remarks>
/// Separate from the system clipboard on purpose: this holds an inventory id, not text, and
/// pasting it performs a grid operation rather than inserting characters. Ctrl+C on a folder in
/// the inventory is a different act from Ctrl+C in a text field, and the two must not share a
/// buffer. (The reference viewer uses one <c>LLClipboard</c> for both, with a "is this text or
/// objects" flag; keeping them apart is the one deliberate difference from it here.)
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

    /// <summary>What was true about it when it was taken. Only paste-as-link reads this.</summary>
    public InventoryClipboardFacts Facts { get; private set; }

    public bool HasContent => Mode != InventoryClipboardMode.None && Id != Guid.Empty;

    public void Set(InventoryClipboardMode mode, Guid id, bool isFolder, string name, Guid sourceFolderId,
                    InventoryClipboardFacts facts = default)
    {
        if (mode == InventoryClipboardMode.None || id == Guid.Empty) { Clear(); return; }

        Mode = mode;
        Id = id;
        IsFolder = isFolder;
        Name = name ?? string.Empty;
        SourceFolderId = sourceFolderId;
        Facts = facts;
    }

    /// <summary>Whether this may be cut at all. BUG-INV-10.</summary>
    /// <remarks>
    /// Reported in-world 2026-09-23: Ctrl+X on a <c>#Library</c> folder, pasted into inventory,
    /// produced an <b>empty folder</b>. A cut is a MOVE, and the Library is not yours to move
    /// anything out of — every grid refuses it, and what arrives is whatever the half-done request
    /// left behind.
    ///
    /// <para>The reference viewer refuses the cut itself rather than the paste.
    /// <c>cutToClipboard</c> requires <c>isItemRemovable()</c>, which for both items and folders
    /// ends at the same root test —
    /// <c>if (!model-&gt;isObjectDescendentOf(id, gInventory.getRootFolderID())) return false;</c>
    /// (llinventoryfunctions.cpp:745 for an item, :828 for a folder), under the literal comment
    /// <i>"Can't delete an item that's in the library."</i>. The Library hangs off
    /// <c>getLibraryRootFolderID()</c>, a different root, so nothing in it passes.</para>
    ///
    /// <para><b>Copy stays allowed.</b> <c>copyToClipboard</c> asks only
    /// <c>isItemCopyable()</c> (llinventorybridge.cpp:406-412) — copying out of the Library is
    /// what the Library is FOR, and FEAT-INV-09's folder copy already handles it.</para>
    ///
    /// <para>The library test comes first so that a Library folder that is also a system folder
    /// ("Clothing" exists under both roots) is explained by the reason the user can act on.</para>
    /// </remarks>
    public static InventoryCutCheck CanCut(InventoryClipboardFacts facts, bool isFolder)
    {
        if (facts.IsInLibrary) return InventoryCutCheck.InLibrary;
        if (isFolder && facts.IsSystemFolder) return InventoryCutCheck.SystemFolder;
        return InventoryCutCheck.Ok;
    }

    public void Clear()
    {
        Mode = InventoryClipboardMode.None;
        Id = Guid.Empty;
        IsFolder = false;
        Name = string.Empty;
        SourceFolderId = Guid.Empty;
        Facts = default;
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
        }

        return InventoryPasteCheck.Ok;
    }

    /// <summary>
    /// Whether the clipboard's content may be pasted into a folder <b>as a link</b> — a second
    /// name for the one thing, not a second thing. FEAT-INV-09.
    /// </summary>
    /// <remarks>
    /// The rules are <c>LLInvFVBridge::isClipboardPasteableAsLink</c>
    /// (llinventorybridge.cpp:681-717) plus the two asset-id cases
    /// <c>LLFolderBridge::pasteLinkFromClipboard</c> checks before it sends anything
    /// (llinventorybridge.cpp:4420-4435).
    ///
    /// <para>Takes no ancestor list on purpose: a folder LINK inside the folder it points at is
    /// not the trap a folder MOVE there is. Nothing is detached — the original keeps its place,
    /// and the link is a leaf. The reference viewer does not test for it either.</para>
    /// </remarks>
    public InventoryPasteCheck CanPasteLinkInto(Guid targetFolderId)
    {
        if (!HasContent) return InventoryPasteCheck.Nothing;
        if (targetFolderId == Guid.Empty) return InventoryPasteCheck.Nothing;

        // A Library item belongs to the library owner, not to you. AIS refuses a link to one, and
        // the honest fix is the ordinary copy — which is what wearing a Library item already does.
        if (Facts.IsInLibrary) return InventoryPasteCheck.LibraryCannotBeLinked;

        if (IsFolder)
        {
            // A protected folder is the grid's own; linking one hands a second route into a folder
            // the server routes arriving content by.
            return Facts.IsSystemFolder ? InventoryPasteCheck.CannotBeLinked : InventoryPasteCheck.Ok;
        }

        if (Facts.IsLink) return InventoryPasteCheck.AlreadyALink;
        if (!InventoryLinkRules.CanLink(Facts.AssetType)) return InventoryPasteCheck.CannotBeLinked;
        if (InventoryLinkRules.NeedsAssetToLink(Facts.AssetType) && Facts.AssetId == Guid.Empty)
            return InventoryPasteCheck.CannotBeLinked;

        return InventoryPasteCheck.Ok;
    }

    /// <summary>Whether pasting into this folder would achieve nothing — a cut put back exactly
    /// where it came from. A COPY into the same folder is not pointless: it is how a duplicate is
    /// made.</summary>
    public bool IsNoOpInto(Guid targetFolderId)
        => Mode == InventoryClipboardMode.Cut && targetFolderId != Guid.Empty && targetFolderId == SourceFolderId;
}
