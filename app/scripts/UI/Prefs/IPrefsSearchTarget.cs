using SLNG.Core.Settings;

namespace SLNG.App.UI.Prefs;

/// <summary>
/// Implemented by the content of an embedded block (<see cref="PrefsSection.AddBlock"/>) that holds many things of its
/// own and a filter of its own -- the Keyboard table. The Preferences search asks it whether the query matches anything
/// inside, and drives its filter instead of highlighting the whole block.
/// </summary>
public interface IPrefsSearchTarget
{
    /// <summary>True when <paramref name="query"/> (never empty) matches something inside the block.</summary>
    bool MatchesSearch(SettingsQuery query);

    /// <summary>The window's search matches: show the block narrowed to <paramref name="text"/>, the text as typed. The
    /// block remembers that the search set its filter.</summary>
    void ShowSearch(string text);

    /// <summary>The window's search ended or stopped matching: take the filter back off, but only if the search set it.</summary>
    void EndSearch();
}
