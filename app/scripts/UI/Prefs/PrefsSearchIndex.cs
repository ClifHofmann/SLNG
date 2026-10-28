using System.Collections.Generic;
using SLNG.Core.Settings;

namespace SLNG.App.UI.Prefs;

/// <summary>
/// What the Preferences search looks in (FEAT-UI-80): for every row the texts it can be found by, for every section and
/// category its title. Built from the L10n KEYS the rows kept, resolved in each of the given languages, so a German
/// user also finds a setting by its English name; plus the row's literal texts and keywords (the SL debug-setting name).
///
/// <para>Texts are normalized once here (<see cref="SettingsSearchEntry"/>); a keystroke only runs substring tests. The
/// index is a snapshot of the texts of the languages it was built with: rebuild it when the language changes. A row,
/// section or category that was added after the build is not in it and never matches.</para>
/// </summary>
public sealed class PrefsSearchIndex
{
    private readonly Dictionary<PrefsRow, SettingsSearchEntry> _rows = new();
    private readonly Dictionary<PrefsSection, SettingsSearchEntry> _sections = new();
    private readonly Dictionary<PrefsCategory, SettingsSearchEntry> _categories = new();

    /// <param name="categories">Every category of the window.</param>
    /// <param name="locales">The languages to resolve keys in: the current one, the one the window's texts were built in,
    /// and en-US. Duplicates are fine.</param>
    public PrefsSearchIndex(IEnumerable<PrefsCategory> categories, IReadOnlyList<string> locales)
    {
        foreach (var category in categories)
        {
            _categories[category] = new SettingsSearchEntry(Resolve(locales, category.TitleKey));
            foreach (var section in category.Sections)
            {
                _sections[section] = new SettingsSearchEntry(section.TitleKey == null ? new List<string>() : Resolve(locales, section.TitleKey));
                foreach (var row in section.Rows) _rows[row] = new SettingsSearchEntry(RowTexts(row, locales));
            }
        }
    }

    /// <summary>True when the query matches one of the row's texts. A row that is hidden by its page (<c>Visible</c> off) or
    /// that the index does not know never matches: a setting that is not on screen cannot be found.</summary>
    public bool Matches(PrefsRow row, SettingsQuery query)
        => row.Visible && _rows.TryGetValue(row, out var entry) && entry.Matches(query);

    /// <summary>True when the query matches the section's title.</summary>
    public bool Matches(PrefsSection section, SettingsQuery query)
        => _sections.TryGetValue(section, out var entry) && entry.Matches(query);

    /// <summary>True when the query matches the category's title.</summary>
    public bool Matches(PrefsCategory category, SettingsQuery query)
        => _categories.TryGetValue(category, out var entry) && entry.Matches(query);

    private static List<string> RowTexts(PrefsRow row, IReadOnlyList<string> locales)
    {
        var keys = new List<string>();
        if (row.LabelKey != null) keys.Add(row.LabelKey);
        if (row.HintKey != null) keys.Add(row.HintKey);
        if (row.TooltipKey != null) keys.Add(row.TooltipKey);
        keys.AddRange(row.OptionKeys);
        keys.AddRange(row.SearchKeys);

        var texts = Resolve(locales, keys.ToArray());
        texts.AddRange(row.LiteralTexts);
        texts.AddRange(row.Keywords);
        return texts;
    }

    private static List<string> Resolve(IReadOnlyList<string> locales, params string[] keys)
    {
        var texts = new List<string>();
        foreach (string locale in locales)
        {
            foreach (string key in keys)
            {
                string? text = L10n.TrIn(locale, key);
                if (!string.IsNullOrEmpty(text)) texts.Add(text);
            }
        }
        return texts;
    }
}
