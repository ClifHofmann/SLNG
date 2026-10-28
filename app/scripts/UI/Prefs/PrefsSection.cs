using System;
using System.Collections.Generic;
using Godot;

namespace SLNG.App.UI.Prefs;

/// <summary>
/// A titled group of <see cref="PrefsRow"/>s inside a <see cref="PrefsCategory"/> (FEAT-UI-79): a heading, the main rows,
/// and -- from the first row added with <c>advanced: true</c> -- an "Advanced" fold-out below them. The fold-out is
/// collapsed by default, and whether the person opened it is remembered per section (<see cref="PrefsState"/>).
///
/// <para>A section is made by <see cref="PrefsCategory.AddSection"/>. Rows are added in the order they should appear;
/// a main row added after an advanced one still lands above the fold-out. Row ids are
/// <c>&lt;categoryId&gt;.&lt;sectionId&gt;.&lt;name&gt;</c>.</para>
/// </summary>
public partial class PrefsSection : VBoxContainer
{
    private readonly List<PrefsRow> _rows = new();
    private FoldableContainer? _fold;
    private VBoxContainer? _advancedBody;

    /// <summary>Section id, unique within its category.</summary>
    public string Id { get; }

    /// <summary>Id of the category this section belongs to.</summary>
    public string CategoryId { get; }

    /// <summary>L10n key of the heading; null for a section without one (a header row, a single embedded page).</summary>
    public string? TitleKey { get; }

    /// <summary>Every row of the section, main and advanced, in the order they were added.</summary>
    public IReadOnlyList<PrefsRow> Rows => _rows;

    /// <summary>The Advanced fold-out; null until a row is added with <c>advanced: true</c>.</summary>
    public FoldableContainer? AdvancedFold => _fold;

    internal PrefsSection(string categoryId, string id, string? titleKey)
    {
        CategoryId = categoryId;
        Id = id;
        TitleKey = titleKey;
        AddThemeConstantOverride("separation", PrefsStyle.RowSeparation);
        if (titleKey != null) AddChild(PrefsStyle.MakeHeading(L10n.Tr(titleKey)));
    }

    /// <summary>A checkbox row: the box carries its own label, across the full width.</summary>
    public PrefsRow AddCheck(string id, string labelKey, bool value, Action<bool> onToggled, string? hintKey = null,
        bool advanced = false)
        => Add(PrefsRow.CreateCheck(id, labelKey, value, onToggled, hintKey), advanced);

    /// <summary>A slider row: label left, slider right with its value written out beside it.</summary>
    public PrefsRow AddSlider(string id, string labelKey, double min, double max, double step, double value,
        Func<double, string> format, Action<double> onChanged, string? hintKey = null, bool advanced = false)
        => Add(PrefsRow.CreateSlider(id, labelKey, min, max, step, value, format, onChanged, hintKey), advanced);

    /// <summary>A dropdown row. <paramref name="optionLabels"/> are the entries as shown;
    /// <paramref name="optionKeys"/> are their L10n keys when they have them (for the search), otherwise the labels are
    /// recorded as literal texts.</summary>
    public PrefsRow AddOption(string id, string labelKey, IReadOnlyList<string> optionLabels, int selected,
        Action<int> onSelected, string? hintKey = null, bool advanced = false, IReadOnlyList<string>? optionKeys = null)
        => Add(PrefsRow.CreateOption(id, labelKey, optionLabels, selected, onSelected, hintKey, optionKeys), advanced);

    /// <summary>A row with any control on the right of a label.</summary>
    public PrefsRow AddControl(string id, string labelKey, Control control, string? hintKey = null, bool advanced = false)
        => Add(PrefsRow.CreateControl(id, labelKey, control, hintKey), advanced);

    /// <summary>Full-width content: an embedded older page, a list, a row of buttons. <paramref name="searchKeys"/> are
    /// L10n keys of texts inside it that the search should find.</summary>
    public PrefsRow AddBlock(string id, Control content, string? labelKey = null, bool advanced = false,
        params string[] searchKeys)
        => Add(PrefsRow.CreateBlock(id, content, labelKey, searchKeys), advanced);

    /// <summary>A paragraph of explanation that belongs to no single row.</summary>
    public PrefsRow AddNote(string id, string textKey, bool advanced = false)
        => Add(PrefsRow.CreateNote(id, textKey), advanced);

    private PrefsRow Add(PrefsRow row, bool advanced)
    {
        string prefix = $"{CategoryId}.{Id}.";
        if (!row.Id.StartsWith(prefix, StringComparison.Ordinal))
        {
            GD.PushError($"[Preferences] row id '{row.Id}' does not start with '{prefix}'");
        }
        foreach (var existing in _rows)
        {
            if (existing.Id == row.Id) GD.PushError($"[Preferences] row id '{row.Id}' is used twice in section '{CategoryId}.{Id}'");
        }

        row.Attach(CategoryId, Id);
        row.IsAdvanced = advanced;
        _rows.Add(row);

        if (advanced)
        {
            EnsureAdvanced().AddChild(row);
        }
        else
        {
            AddChild(row);
            if (_fold != null) MoveChild(row, _fold.GetIndex()); // main rows stay above the fold-out
        }
        return row;
    }

    private VBoxContainer EnsureAdvanced()
    {
        if (_advancedBody != null) return _advancedBody;

        _advancedBody = new VBoxContainer();
        _advancedBody.AddThemeConstantOverride("separation", PrefsStyle.RowSeparation);

        _fold = new FoldableContainer
        {
            Title = L10n.Tr("ui.preferences.advanced"),
            Folded = !PrefsState.IsAdvancedOpen(CategoryId, Id),
            FocusMode = FocusModeEnum.None,
        };
        PrefsStyle.StyleAdvancedFold(_fold);
        _fold.AddChild(_advancedBody);
        // Connected after the initial state is set: reading the remembered state at build time must not write it back.
        _fold.FoldingChanged += folded => PrefsState.SetAdvancedOpen(CategoryId, Id, !folded);
        AddChild(_fold);
        return _advancedBody;
    }
}
