using SLNG.App.UI.Prefs;

namespace SLNG.App.UI;

/// <summary>
/// The "Names above avatars" section of the General category (FEAT-UI-79; FEAT-UI-30, FEAT-UI-31): the reference viewer's
/// nametag switches, ordered the way the lines appear in the tag, and the one that is not a display switch. Bound to
/// <see cref="UiSettings"/>. The long explanations stay tooltips.
/// </summary>
public sealed class NamesPreferencesPage
{
    /// <summary>Fills <paramref name="section"/> (General, "names"), which Boot has already put in its place.</summary>
    public void Build(PrefsSection section, UiSettings settings)
    {
        string prefix = $"{section.CategoryId}.{section.Id}";

        section.AddCheck($"{prefix}.show_display_names", "ui.preferences.show_display_names",
                settings.ShowDisplayNames, on => settings.SetShowDisplayNames(on))
            .WithTooltip("ui.preferences.show_display_names_tip")
            .WithKeywords("UseDisplayNames", "NameTagShowDisplayNames");

        section.AddCheck($"{prefix}.show_legacy_names", "ui.preferences.show_legacy_names",
                settings.ShowLegacyNames, on => settings.SetShowLegacyNames(on))
            .WithTooltip("ui.preferences.show_legacy_names_tip")
            .WithKeywords("NameTagShowUsernames");

        section.AddCheck($"{prefix}.show_group_titles", "ui.preferences.show_group_titles",
                settings.ShowGroupTitles, on => settings.SetShowGroupTitles(on))
            .WithTooltip("ui.preferences.show_group_titles_tip")
            .WithKeywords("NameTagShowGroupTitles");

        // FEAT-UI-30: NOT a display toggle -- see UiSettings.HideOwnGroupTitle. Placed with them because that is where a
        // user looks for it, but the tooltip has to say that this one actually reaches other people.
        section.AddCheck($"{prefix}.hide_own_group_title", "ui.preferences.hide_own_group_title",
                settings.HideOwnGroupTitle, on => settings.SetHideOwnGroupTitle(on))
            .WithTooltip("ui.preferences.hide_own_group_title_tip")
            .WithKeywords("RenderHideGroupTitle");
    }
}
