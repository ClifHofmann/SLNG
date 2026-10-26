namespace SLNG.Core.Login;

/// <summary>
/// FEAT-UI-72: one entry of the login screen's Favorites list -- a landmark from the inventory Favorites
/// folder that has been resolved to a place the grid's login service understands. The login screen
/// cannot read the inventory, so these are kept per profile on disk
/// (<see cref="LoginFavoriteBook"/>) and written while a session is running.
/// </summary>
/// <param name="AssetId">The landmark asset id: the key, immutable (region id + local position).</param>
/// <param name="ItemId">The inventory item id the landmark had in the Favorites folder when this was written.</param>
/// <param name="Name">The item's name, as the folder shows it (changes with a rename; the target does not).</param>
/// <param name="Target">Where the landmark points.</param>
public sealed record LoginFavorite(Guid AssetId, Guid ItemId, string Name, StartTarget Target);
