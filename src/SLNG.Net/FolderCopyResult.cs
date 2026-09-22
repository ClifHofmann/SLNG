using System;

namespace SLNG.Net;

/// <summary>
/// What a recursive folder copy actually managed to do. FEAT-INV-09.
/// </summary>
/// <remarks>
/// A folder copy is not one request — it is a new folder per subfolder and a batch of copies per
/// folder, and some items are knowingly left behind. A caller that only learned "it finished"
/// would report a half-copied folder as a copy, so what was skipped is counted and the UI says so.
///
/// <para>These are counts of what was <b>asked for</b>, except <see cref="SkippedNoCopy"/>. Like
/// the reference viewer, the copy does not wait for the grid to confirm each request — see the
/// note in <c>GridSession.Inventory</c> — so a refusal reaches the log, not this record.</para>
/// </remarks>
/// <param name="NewFolderId">The copy's own folder id, or empty if not even that could be made.</param>
/// <param name="Folders">Folders created, including the top one.</param>
/// <param name="Items">Items the grid was asked to copy.</param>
/// <param name="Links">Links recreated as links, rather than as copies of what they point at.</param>
/// <param name="SkippedNoCopy">Items left behind because the owner may not copy them — the one
/// count here that is certain, because we decide it ourselves.</param>
/// <param name="DepthTruncated">The tree was deeper than <see cref="GridSession.MaxFolderCopyDepth"/>
/// and the deepest folders were not followed.</param>
public sealed record FolderCopyResult(
    Guid NewFolderId,
    int Folders,
    int Items,
    int Links,
    int SkippedNoCopy,
    bool DepthTruncated)
{
    public static readonly FolderCopyResult Nothing =
        new(Guid.Empty, 0, 0, 0, 0, false);

    /// <summary>Whether anything at all was created.</summary>
    public bool Success => NewFolderId != Guid.Empty;

    /// <summary>Whether the copy is complete — nothing skipped, nothing cut off.</summary>
    public bool Complete => Success && SkippedNoCopy == 0 && !DepthTruncated;
}
