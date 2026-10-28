namespace SLNG.Core.Login;

/// <summary>
/// FEAT-UI-77: a favorite whose region did not answer when it was last looked up -- the region is down or gone. The login
/// screen lists it greyed out (it cannot be started at: nothing says which region the landmark points at), so the person
/// learns that before logging in instead of after a teleport that never lands.
/// </summary>
public sealed record LoginOfflineFavorite(Guid AssetId, Guid ItemId, string Name);
