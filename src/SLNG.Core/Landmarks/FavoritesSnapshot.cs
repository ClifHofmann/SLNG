namespace SLNG.Core.Landmarks;

/// <summary>
/// FEAT-UI-72: what a read of the inventory Favorites folder found.
/// </summary>
/// <param name="Confirmed">True only when the Favorites folder exists and the grid really answered for it and every
/// subfolder looked at. An unconfirmed snapshot says nothing about what the folder holds -- a refused fetch reads as
/// an empty folder -- so a caller must never act on its <paramref name="Items"/> as if they were the whole truth
/// (the login cache is not touched by one).</param>
/// <param name="Items">The landmark items found (links excluded, Trash excluded, only those with an asset id).</param>
public sealed record FavoritesSnapshot(bool Confirmed, IReadOnlyList<LandmarkInventoryItem> Items)
{
    /// <summary>The answer when there is no inventory, no Favorites folder, or the fetch failed.</summary>
    public static FavoritesSnapshot Unconfirmed { get; } = new(false, Array.Empty<LandmarkInventoryItem>());
}
