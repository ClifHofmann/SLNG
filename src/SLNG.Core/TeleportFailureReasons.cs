namespace SLNG.Core;

/// <summary>
/// The failure ids a simulator sends with a refused teleport (<c>no_host</c>, <c>noaccess_tport</c>, ...), and the localisation
/// key that says what they mean. The reference viewer keeps the same table in <c>teleport_strings.xml</c> (<c>errors</c> set) and shows
/// the sentence, never the id; an id shown raw ("no_host") tells a person nothing.
///
/// <para>Pure and engine-agnostic: it only names the key. The client turns the key into text in the current language, and anything
/// that is not a known id (a sentence the grid already wrote, a timeout text) is shown as it came.</para>
/// </summary>
public static class TeleportFailureReasons
{
    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "invalid_tport", "invalid_region_handoff", "blocked_tport", "nolandmark_tport", "timeout_tport", "NoHelpIslandTP",
        "noaccess_tport", "missing_attach_tport", "too_many_uploads_tport", "expired_tport", "expired_region_handoff",
        "preexisting_tport", "no_host", "no_inventory_host", "MustGetAgeRegion",
    };

    /// <summary>The localisation key for a failure id, or null when <paramref name="reason"/> is not one of the known ids.</summary>
    public static string? KeyFor(string? reason)
    {
        string? id = reason?.Trim();
        return id != null && Known.Contains(id) ? "ui.teleport.reason_" + id : null;
    }
}
