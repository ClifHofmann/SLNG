namespace SLNG.Core;

/// <summary>
/// Which of a resident's two names the UI shows. The legacy name ("First Last", the login name) is the
/// stable identity; the Display Name is chosen freely and may be absent. Chat, the Friends list and the
/// Recent list show the Display Name when there is one -- the same rule the nametag uses -- and fall back
/// to the legacy name otherwise. Presentation only: logs, file names and protocol messages keep the
/// legacy name.
/// </summary>
public static class PersonNameDisplay
{
    /// <summary>The name to show. <paramref name="displayName"/> counts only when it is non-blank and
    /// really differs from <paramref name="legacyName"/> (a resident who never set one has the legacy name
    /// echoed back as the Display Name). With <paramref name="useDisplayNames"/> off the legacy name
    /// always wins; with no legacy name known, a Display Name is better than nothing.</summary>
    public static string Choose(string? legacyName, string? displayName, bool useDisplayNames)
    {
        string legacy = legacyName?.Trim() ?? "";
        string display = displayName?.Trim() ?? "";

        if (!useDisplayNames || display.Length == 0) return legacy;
        if (legacy.Length == 0) return display;
        return string.Equals(display, legacy, StringComparison.OrdinalIgnoreCase) ? legacy : display;
    }
}
