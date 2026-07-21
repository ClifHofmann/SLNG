namespace SLNG.Net;

/// <summary>
/// Outcome of creating a landmark. Engine-agnostic: no LibreMetaverse type crosses the
/// <c>SLNG.Net</c> boundary.
/// </summary>
public sealed record LandmarkCreateResult(bool Success, Guid? ItemId, Guid? AssetId, string Message);
