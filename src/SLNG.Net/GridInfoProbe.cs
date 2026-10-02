using SLNG.Core.ChatLogs;

namespace SLNG.Net;

/// <summary>
/// FEAT-UI-41: asks an OpenSim grid for its name (<c>get_grid_info</c>, the standard grid-info
/// document; <c>&lt;gridname&gt;OSGrid&lt;/gridname&gt;</c>). Firestorm names a chat-log folder after
/// that name, so finding it is what lets SLNG put a log where Firestorm will look. One plain GET
/// with a short timeout and no credentials, made only to the grid the user is logging into and
/// never to a Linden grid. A grid that does not answer simply has no name here -- the caller falls
/// back (see <see cref="GridLabels.Resolve"/>); it never blocks or fails a login.
/// </summary>
public static class GridInfoProbe
{
    private static readonly HttpClient Http = new();

    /// <summary>The grid's reported name, or null (not http(s), no answer, no name, a timeout).</summary>
    public static async Task<string?> TryGetGridNameAsync(string? loginUri, TimeSpan timeout, CancellationToken ct = default)
    {
        Uri? uri = GridLabels.GridInfoUri(loginUri);
        if (uri is null) return null;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var response = await Http.GetAsync(uri, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            // A grid-info document is a few hundred bytes; refuse anything that is not.
            string body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return body.Length > 64 * 1024 ? null : GridLabels.ParseGridInfoName(body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or InvalidOperationException)
        {
            return null;
        }
    }
}
