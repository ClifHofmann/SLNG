using System.Collections.Generic;

namespace SLNG.Net;

/// <summary>
/// What is in the Trash, for the one question that cannot be taken back: "empty it?". FEAT-INV-10.
/// </summary>
/// <remarks>
/// Counts everything at any depth, items and folders alike, as the reference viewer does for its
/// own prompt (<c>collectDescendents</c> in <c>LLInventoryModel::emptyFolderType</c>,
/// llinventorymodel.cpp:4251-4273).
/// </remarks>
/// <param name="Items">Items at any depth below the Trash.</param>
/// <param name="Folders">Folders at any depth below the Trash.</param>
/// <param name="Complete">Every folder below the Trash has had its contents fetched, so the
/// counts are exact. When false they are a lower bound.</param>
/// <param name="WornNames">Things below the Trash that are being worn right now. Not empty means
/// the Trash must not be emptied: the reference viewer disables "Empty Trash" in exactly this case
/// (<c>LLVOAvatarSelf::hasAttachmentsInTrash</c>, llinventorybridge.cpp:4578).</param>
public sealed record TrashSummary(int Items, int Folders, bool Complete, IReadOnlyList<string> WornNames)
{
    /// <summary>Nothing known to be inside. Only a promise of emptiness when <see cref="Complete"/>
    /// is true too.</summary>
    public bool IsEmpty => Items == 0 && Folders == 0;
}
