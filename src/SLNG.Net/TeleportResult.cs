namespace SLNG.Net;

/// <summary>
/// Outcome of a teleport attempt. Engine-agnostic: <see cref="Message"/> is whatever text the
/// grid/LibreMetaverse reported (last <c>TeleportProgress</c> message before completion), so no
/// LibreMetaverse type (<c>TeleportEventArgs</c>/<c>TeleportStatus</c>) crosses the
/// <c>SLNG.Net</c> boundary.
/// </summary>
public sealed record TeleportResult(bool Success, string Message);
