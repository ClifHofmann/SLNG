namespace SLNG.Core.Landmarks;

/// <summary>
/// FEAT-UI-72: which inventory folder is the Favorites folder -- a pure decision so it is testable without a
/// LibreMetaverse store.
///
/// <para><b>Strict on purpose.</b> LibreMetaverse's <c>FindFolderForType</c> returns the inventory ROOT when no folder
/// of the type exists (verified on 3.1.6: it scans the root's children and then <c>return RootFolder.UUID</c>), so
/// asking it for "the Favorites folder" on a grid that has none would make the whole inventory look like favorites.
/// This answers only with a folder that really is one, and with <c>null</c> otherwise. The reference viewer never
/// creates the folder either.</para>
/// </summary>
public static class FavoritesFolderResolver
{
    /// <summary>
    /// The id of the first folder among the inventory root's direct children whose preferred type is
    /// <see cref="FolderTypeIds.Favorites"/>, or <c>null</c> when there is none (including when there is no root yet).
    /// A Favorites-typed folder deeper than the root's children is not one: system folders live directly under the root.
    /// </summary>
    public static Guid? Resolve(Guid rootId, IEnumerable<(Guid Id, int PreferredType)> rootChildren)
    {
        if (rootId == Guid.Empty) return null;
        foreach (var (id, type) in rootChildren)
        {
            if (type == FolderTypeIds.Favorites && id != Guid.Empty && id != rootId)
                return id;
        }
        return null;
    }
}
