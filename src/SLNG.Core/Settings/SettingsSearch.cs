using System.Globalization;
using System.Text;

namespace SLNG.Core.Settings;

/// <summary>
/// The text rules of the Preferences search (FEAT-UI-80): how a typed query and the texts of a setting are made
/// comparable, and the one-call match. Pure and engine-agnostic, like <c>KeyBindingTable</c>, so it is unit-tested; the
/// window that shows the result lives in <c>app/</c>.
///
/// <para>Matching works on <see cref="Normalize"/>d text: lower case, accents folded (ä becomes a), ß becomes ss and
/// runs of white space become one space. A query is split into words; one word is a substring of any of an entry's texts
/// (as in the reference viewer), several words must each occur, in any of the texts. A search on every keystroke
/// parses the query once (<see cref="Parse"/>) and asks each entry (<see cref="SettingsSearchEntry"/>), whose texts
/// were normalized once when the entry was made.</para>
/// </summary>
public static class SettingsSearch
{
    /// <summary>Lower case, diacritics folded, ß as "ss", white space collapsed to single spaces and trimmed. Null and
    /// white space give the empty string. Both the query and every searched text go through this.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        string decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        bool pendingSpace = false;
        foreach (char c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }
            if (c is 'ß' or 'ẞ') builder.Append("ss");
            else builder.Append(c);
        }
        return builder.ToString();
    }

    /// <summary>The query as words to look for. An empty or blank query is <see cref="SettingsQuery.Empty"/>, which
    /// matches everything.</summary>
    public static SettingsQuery Parse(string? query)
    {
        string normalized = Normalize(query);
        return normalized.Length == 0 ? SettingsQuery.Empty : new SettingsQuery(normalized.Split(' '));
    }

    /// <summary>One-call form for a caller with a single entry: whether <paramref name="query"/> matches
    /// <paramref name="texts"/>. Normalizes everything again each time, so a loop over many entries should use
    /// <see cref="Parse"/> and <see cref="SettingsSearchEntry"/> instead.</summary>
    public static bool Matches(string? query, IEnumerable<string?> texts)
        => new SettingsSearchEntry(texts).Matches(Parse(query));
}
