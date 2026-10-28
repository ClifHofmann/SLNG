using System.Collections.Generic;
using Godot;

namespace SLNG.App.UI.Prefs;

/// <summary>
/// One entry of the Preferences window's category list and the column of sections it shows (FEAT-UI-79). Made by
/// <see cref="PreferencesWindow.AddCategory"/>; fill it with <see cref="AddSection"/>.
/// </summary>
public partial class PrefsCategory : VBoxContainer
{
    private readonly List<PrefsSection> _sections = new();

    /// <summary>Category id, e.g. <c>camera_movement</c>. Also the first part of every row id inside it.</summary>
    public string Id { get; }

    /// <summary>L10n key of the name shown in the category list.</summary>
    public string TitleKey { get; }

    /// <summary>The sections, top to bottom.</summary>
    public IReadOnlyList<PrefsSection> Sections => _sections;

    internal PrefsCategory(string id, string titleKey)
    {
        Id = id;
        TitleKey = titleKey;
        AddThemeConstantOverride("separation", PrefsStyle.SectionSeparation);
    }

    /// <summary>Every row of every section, top to bottom (a section's advanced rows in the order they were added).</summary>
    public IReadOnlyList<PrefsRow> Rows
    {
        get
        {
            var rows = new List<PrefsRow>();
            foreach (var section in _sections) rows.AddRange(section.Rows);
            return rows;
        }
    }

    /// <summary>Appends a section below the others. <paramref name="titleKey"/> is the L10n key of its heading, or
    /// null for a section without one.</summary>
    public PrefsSection AddSection(string id, string? titleKey)
    {
        var section = new PrefsSection(Id, id, titleKey);
        foreach (var existing in _sections)
        {
            if (existing.Id == id) GD.PushError($"[Preferences] section '{Id}.{id}' exists twice");
        }
        _sections.Add(section);
        AddChild(section);
        return section;
    }
}
