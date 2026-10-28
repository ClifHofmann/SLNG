namespace SLNG.Core.Settings;

/// <summary>
/// One searchable thing (a setting, a section title, a key action) as the texts it can be found by, normalized once
/// when the entry is made so that a keystroke only runs substring tests. The texts of several languages go into the
/// same entry: a German user finds a setting by its English name too.
/// </summary>
public sealed class SettingsSearchEntry
{
    private readonly string[] _texts;

    /// <summary>Normalizes <paramref name="texts"/>; empty and null ones, and duplicates, are dropped.</summary>
    public SettingsSearchEntry(IEnumerable<string?> texts)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string? text in texts)
        {
            string normalized = SettingsSearch.Normalize(text);
            if (normalized.Length > 0) seen.Add(normalized);
        }
        _texts = new string[seen.Count];
        seen.CopyTo(_texts);
    }

    /// <summary>How many distinct texts the entry holds.</summary>
    public int TextCount => _texts.Length;

    /// <summary>True when every word of <paramref name="query"/> occurs in at least one text. An empty query matches
    /// everything, even an entry with no text.</summary>
    public bool Matches(SettingsQuery query)
    {
        foreach (string word in query.Words)
        {
            bool found = false;
            foreach (string text in _texts)
            {
                if (text.Contains(word, StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }
            }
            if (!found) return false;
        }
        return true;
    }
}
