namespace SLNG.Core;

/// <summary>
/// Which friends the list shows, and in what order -- the rule that used to live inside the Friends panel
/// (FEAT-UI-65), where nothing could test it.
/// </summary>
public static class FriendListView
{
    /// <summary>The friends to show: those that match the filter text (a part of the shown name or of the login name,
    /// ignoring case; empty text matches everyone), and with <paramref name="onlyOnline"/> only those online. Sorted
    /// online first, then by shown name ignoring case -- the order every viewer uses.</summary>
    /// <param name="shownName">The name the list displays for a friend (the Display Name when there is one).</param>
    public static IReadOnlyList<FriendEntry> Select(
        IEnumerable<FriendEntry> friends, Func<FriendEntry, string> shownName, string? filterText, bool onlyOnline)
    {
        string text = (filterText ?? string.Empty).Trim();
        return friends
            .Where(f => text.Length == 0
                        || shownName(f).Contains(text, StringComparison.OrdinalIgnoreCase)
                        || f.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
            .Where(f => !onlyOnline || f.IsOnline)
            .OrderByDescending(f => f.IsOnline)
            .ThenBy(shownName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
