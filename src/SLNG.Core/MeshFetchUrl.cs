using System;

namespace SLNG.Core;

/// <summary>
/// FEAT-NET-07: the URL and retry rule for fetching a mesh asset over the region's mesh capability with our own
/// HTTP client. LibreMetaverse's own mesh request queued everything behind a limiter that let about ten meshes
/// through a second (measured on a cold visit, 8,876 requests, p95 latency 400 s); the reference viewer's
/// HTTP layer is limited by concurrent connections instead (llappcorehttp.cpp:70, llmeshrepository.cpp:352).
/// </summary>
public static class MeshFetchUrl
{
    /// <summary><c>{cap}?mesh_id={id}</c>, the shape LibreMetaverse requests (so every grid that served it keeps
    /// serving it); a capability URL that already has a query gets <c>&amp;</c> instead.</summary>
    public static Uri Build(Uri capability, Guid meshId)
    {
        string text = capability.ToString();
        string separator = text.Contains('?') ? "&" : "?";
        return new Uri($"{text}{separator}mesh_id={meshId}");
    }

    /// <summary>A status worth asking again: the service is busy or timed out. 403/404/410 are decisions
    /// (see <c>AssetRefusals</c>) and any other 4xx is a request that will fail the same way again.</summary>
    public static bool IsTransient(int status) => status is 408 or 429 or >= 500 and <= 599;
}
