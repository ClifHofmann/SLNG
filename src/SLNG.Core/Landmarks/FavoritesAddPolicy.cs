namespace SLNG.Core.Landmarks;

/// <summary>What <see cref="FavoritesAddPolicy.Decide"/> says about putting a landmark into the Favorites folder.</summary>
public enum FavoriteAddVerdict
{
    /// <summary>Go ahead: copy it into the folder.</summary>
    Add,
    /// <summary>The grid has no Favorites folder (or none could be found yet).</summary>
    NoFolder,
    /// <summary>The inventory has no such landmark.</summary>
    UnknownItem,
    /// <summary>The item is already in the Favorites folder.</summary>
    AlreadyInFolder,
    /// <summary>Another landmark with the same asset (the same place) is already a favorite.</summary>
    AssetAlreadyFavorite,
    /// <summary>The landmark cannot be copied; a favorite is a copy, and moving the only one out of Landmarks is never done.</summary>
    NotCopyable,
}

/// <summary>
/// FEAT-UI-72 phase 2: whether a landmark may become a favorite -- a pure decision, made before anything is written to the
/// inventory. A favorite is a <b>copy</b> in the Favorites folder (what the reference viewer does,
/// <c>llfavoritesbar.cpp:692-698</c>); it refuses an item that is already in the folder (<c>:567-572</c>), and here also a
/// second landmark for a place that already is a favorite, as the old local list did. A landmark the person may not copy
/// is refused instead of being moved: moving it would take their only copy out of Landmarks, and asset protection
/// (AGENTS.md, non-negotiable 1) forbids working around a missing Copy permission.
/// </summary>
public static class FavoritesAddPolicy
{
    public static FavoriteAddVerdict Decide(
        bool folderExists, bool itemKnown, Guid itemId, Guid assetId, bool canCopy,
        IEnumerable<LandmarkInventoryItem> favorites)
    {
        if (!folderExists) return FavoriteAddVerdict.NoFolder;
        if (!itemKnown) return FavoriteAddVerdict.UnknownItem;

        foreach (var favorite in favorites)
        {
            if (favorite.Id == itemId) return FavoriteAddVerdict.AlreadyInFolder;
        }
        if (!canCopy) return FavoriteAddVerdict.NotCopyable;
        if (assetId != Guid.Empty)
        {
            foreach (var favorite in favorites)
            {
                if (favorite.AssetId == assetId) return FavoriteAddVerdict.AssetAlreadyFavorite;
            }
        }
        return FavoriteAddVerdict.Add;
    }
}
