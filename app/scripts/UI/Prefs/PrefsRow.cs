using System;
using System.Collections.Generic;
using Godot;

namespace SLNG.App.UI.Prefs;

/// <summary>
/// One setting in the Preferences window (FEAT-UI-79): its control, its label, an optional one-sentence hint and an
/// optional badge when the change does not apply at once. Built through <see cref="PrefsSection"/>'s
/// <c>AddCheck</c> / <c>AddSlider</c> / <c>AddOption</c> / <c>AddControl</c> / <c>AddBlock</c> / <c>AddNote</c>, never
/// directly.
///
/// <para>The row is built from L10n KEYS, not translated strings, and keeps them: the search (FEAT-UI-80) indexes
/// every language from the key. <see cref="Keywords"/> and <see cref="LiteralTexts"/> are the texts that are not keys
/// (an SL debug-setting name, a dropdown entry such as "2x").</para>
///
/// <para>Layout: a checkbox row is the checkbox, which carries its own label, across the full width; every other row
/// has the label in a fixed left column and the control to its right. A hint, when there is one, sits below.</para>
/// </summary>
public partial class PrefsRow : VBoxContainer
{
    private readonly List<string> _optionKeys = new();
    private readonly List<string> _literalTexts = new();
    private readonly List<string> _keywords = new();
    private readonly List<string> _searchKeys = new();

    private HBoxContainer? _line;
    private Container? _badgeHost;
    private Label? _label;
    private Label? _badge;
    private Godot.Control? _content;

    /// <summary>Stable id, <c>&lt;categoryId&gt;.&lt;sectionId&gt;.&lt;name&gt;</c>, e.g. <c>camera_movement.camera.fov</c>.</summary>
    public string Id { get; }

    /// <summary>The category this row sits in. Set when a section takes the row.</summary>
    public string CategoryId { get; private set; } = "";

    /// <summary>The section this row sits in. Set when a section takes the row.</summary>
    public string SectionId { get; private set; } = "";

    /// <summary>L10n key of the label (the checkbox's own text for a checkbox row); null for a note.</summary>
    public string? LabelKey { get; private set; }

    /// <summary>L10n key of the hint under the row; for a note, of the note's text.</summary>
    public string? HintKey { get; private set; }

    /// <summary>L10n key of the tooltip, when there is a longer explanation.</summary>
    public string? TooltipKey { get; private set; }

    /// <summary>L10n keys of a dropdown's entries. Empty when the entries are not translated.</summary>
    public IReadOnlyList<string> OptionKeys => _optionKeys;

    /// <summary>Texts of a dropdown's entries that are not L10n keys ("2x", a locale code).</summary>
    public IReadOnlyList<string> LiteralTexts => _literalTexts;

    /// <summary>Extra words the search matches: the SL debug-setting name, common abbreviations.</summary>
    public IReadOnlyList<string> Keywords => _keywords;

    /// <summary>L10n keys of further texts inside an embedded block that the search should find.</summary>
    public IReadOnlyList<string> SearchKeys => _searchKeys;

    /// <summary>The value text right of a slider row's slider (null for any other row). A page that moves the slider
    /// itself and wants the text to follow something other than the slider's own value sets it here.</summary>
    public Label? ValueLabel { get; private set; }

    /// <summary>When a change takes effect. Anything but <see cref="PrefsApply.Immediately"/> shows a badge.</summary>
    public PrefsApply Apply { get; private set; } = PrefsApply.Immediately;

    /// <summary>The row whose value hides or disables this one (shadow controls, the mirror probe), if any. Only
    /// recorded here; the page keeps the visibility / enabled logic.</summary>
    public PrefsRow? Controller { get; private set; }

    /// <summary>True when the row sits in its section's collapsed "Advanced" fold-out.</summary>
    public bool IsAdvanced { get; internal set; }

    /// <summary>The interactive control (checkbox, slider, dropdown, the control given to <c>AddControl</c>); null for
    /// a note or a block.</summary>
    public Control? Control { get; private set; }

    /// <summary>False while <see cref="SetEnabled"/> has switched the row off (a dependent whose switch is off).</summary>
    public bool IsEnabled { get; private set; } = true;

    /// <summary>The content of an embedded block when it filters itself (the Keyboard table); the search drives that
    /// filter instead of highlighting the block. Null for every other row.</summary>
    public IPrefsSearchTarget? SearchTarget { get; private set; }

    /// <summary>True while the search tints this row as a hit.</summary>
    public bool IsHighlighted { get; private set; }

    internal PrefsRow(string id)
    {
        Id = id;
        AddThemeConstantOverride("separation", PrefsStyle.HintSeparation);
    }

    /// <summary>Tints the row (a hit of the Preferences search) or takes the tint off. Only the background is drawn, behind
    /// the row's own controls.</summary>
    public void SetHighlighted(bool highlighted)
    {
        if (IsHighlighted == highlighted) return;
        IsHighlighted = highlighted;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (!IsHighlighted) return;
        DrawStyleBox(PrefsStyle.SearchHitStyle(!IsEnabled), new Rect2(
            -PrefsStyle.SearchHitPadX, -PrefsStyle.SearchHitPadY,
            Size.X + 2 * PrefsStyle.SearchHitPadX, Size.Y + 2 * PrefsStyle.SearchHitPadY));
    }

    /// <summary>The control as <typeparamref name="T"/> (an <c>HSlider</c>, a <c>CheckBox</c>, an <c>OptionButton</c>),
    /// or null when it is not one.</summary>
    public T? ControlAs<T>() where T : Godot.Control => Control as T;

    /// <summary>Records that another row's value hides or disables this one. Does nothing else.</summary>
    public PrefsRow DependsOn(PrefsRow controller)
    {
        Controller = controller;
        return this;
    }

    /// <summary>Adds words the search matches that are in no label: the SL debug-setting name this row corresponds to.</summary>
    public PrefsRow WithKeywords(params string[] keywords)
    {
        foreach (string keyword in keywords)
        {
            if (!string.IsNullOrWhiteSpace(keyword)) _keywords.Add(keyword);
        }
        return this;
    }

    /// <summary>Adds texts the search matches that are neither a key nor a keyword: the labels of controls inside an
    /// embedded block that were never translated (the bottom-bar buttons).</summary>
    public PrefsRow WithLiteralTexts(params string[] texts)
    {
        foreach (string text in texts)
        {
            if (!string.IsNullOrWhiteSpace(text)) _literalTexts.Add(text);
        }
        return this;
    }

    /// <summary>Says when the change takes effect and shows the matching badge after the label.</summary>
    public PrefsRow WithApply(PrefsApply apply)
    {
        Apply = apply;
        if (_badge != null)
        {
            _badge.GetParent()?.RemoveChild(_badge);
            _badge.QueueFree();
            _badge = null;
        }
        string? key = apply switch
        {
            PrefsApply.Restart => "ui.preferences.apply_restart",
            PrefsApply.NextLogin => "ui.preferences.apply_next_login",
            PrefsApply.ReopenWindows => "ui.preferences.apply_reopen",
            _ => null,
        };
        if (key != null && _badgeHost != null)
        {
            _badge = PrefsStyle.MakeBadge(L10n.Tr(key));
            _badgeHost.AddChild(_badge);
        }
        return this;
    }

    /// <summary>Gives the row a longer explanation as a tooltip, on the label and the control.</summary>
    public PrefsRow WithTooltip(string key)
    {
        TooltipKey = key;
        string text = L10n.Tr(key);
        if (_label != null)
        {
            _label.TooltipText = text;
            _label.MouseFilter = MouseFilterEnum.Pass; // a Label ignores the mouse by default, and so never shows a tooltip
        }
        if (Control != null) Control.TooltipText = text;
        else TooltipText = text;
        return this;
    }

    /// <summary>Enables or disables the row's control (for a block: its content, when that is a button, slider or text
    /// field itself) and dims the row. Meant for a row that depends on another (<see cref="DependsOn"/>); anything of an
    /// unknown kind is only dimmed.</summary>
    public void SetEnabled(bool enabled)
    {
        switch (Control ?? _content)
        {
            case BaseButton button: button.Disabled = !enabled; break;
            case Slider slider: slider.Editable = enabled; break;
            case LineEdit edit: edit.Editable = enabled; break;
        }
        Modulate = enabled ? Colors.White : new Color(1f, 1f, 1f, 0.5f);
        IsEnabled = enabled;
        if (IsHighlighted) QueueRedraw(); // the tint is drawn stronger on a dimmed row
    }

    internal void Attach(string categoryId, string sectionId)
    {
        CategoryId = categoryId;
        SectionId = sectionId;
    }

    internal static PrefsRow CreateCheck(string id, string labelKey, bool value, Action<bool> onToggled, string? hintKey)
    {
        var row = new PrefsRow(id) { LabelKey = labelKey };
        var check = new CheckBox
        {
            Text = L10n.Tr(labelKey),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            ButtonPressed = value,
            FocusMode = FocusModeEnum.None,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        check.Toggled += on => onToggled(on);

        row._line = new HBoxContainer();
        row._line.AddThemeConstantOverride("separation", PrefsStyle.ColumnGap);
        row._line.AddChild(check);
        row._badgeHost = row._line;
        row.AddChild(row._line);
        row.Control = check;
        row.AddHint(hintKey, PrefsStyle.CheckHintIndent);
        return row;
    }

    internal static PrefsRow CreateSlider(string id, string labelKey, double min, double max, double step, double value,
        Func<double, string> format, Action<double> onChanged, string? hintKey)
    {
        var row = new PrefsRow(id) { LabelKey = labelKey };

        var slider = new HSlider
        {
            MinValue = min,
            MaxValue = max,
            Step = step,
            Value = value,
            FocusMode = FocusModeEnum.None,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(PrefsStyle.SliderMinWidth, 0),
        };
        var valueLabel = PrefsStyle.MakeValueLabel(format(value));
        slider.ValueChanged += v =>
        {
            valueLabel.Text = format(v);
            onChanged(v);
        };

        var side = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        side.AddThemeConstantOverride("separation", PrefsStyle.ColumnGap);
        side.AddChild(slider);
        side.AddChild(valueLabel);

        row.BuildLabelLine(labelKey, side);
        row.Control = slider;
        row.ValueLabel = valueLabel;
        row.AddHint(hintKey, 0);
        return row;
    }

    internal static PrefsRow CreateOption(string id, string labelKey, IReadOnlyList<string> optionLabels, int selected,
        Action<int> onSelected, string? hintKey, IReadOnlyList<string>? optionKeys)
    {
        var row = new PrefsRow(id) { LabelKey = labelKey };
        if (optionKeys != null) row._optionKeys.AddRange(optionKeys);
        else row._literalTexts.AddRange(optionLabels);

        var option = new OptionButton
        {
            // A dropdown is as wide as its longest entry unless the text may be clipped, and a long entry would then push the
            // whole page wider than the window.
            ClipText = true,
            FocusMode = FocusModeEnum.None,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        foreach (string text in optionLabels) option.AddItem(text);
        if (selected >= 0 && selected < optionLabels.Count) option.Select(selected);
        option.ItemSelected += index => onSelected((int)index);

        row.BuildLabelLine(labelKey, option);
        row.Control = option;
        row.AddHint(hintKey, 0);
        return row;
    }

    internal static PrefsRow CreateControl(string id, string labelKey, Control control, string? hintKey)
    {
        var row = new PrefsRow(id) { LabelKey = labelKey };
        if (control.SizeFlagsVertical == SizeFlags.Fill) control.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        row.BuildLabelLine(labelKey, control);
        row.Control = control;
        row.AddHint(hintKey, 0);
        return row;
    }

    internal static PrefsRow CreateBlock(string id, Control content, string? labelKey, string[] searchKeys)
    {
        var row = new PrefsRow(id) { LabelKey = labelKey };
        row._searchKeys.AddRange(searchKeys);
        row._content = content;
        row.SearchTarget = content as IPrefsSearchTarget;
        if (labelKey != null)
        {
            row._label = PrefsStyle.MakeLabel(L10n.Tr(labelKey));
            row.AddChild(row._label);
        }
        row.AddChild(content);
        return row;
    }

    internal static PrefsRow CreateNote(string id, string textKey)
    {
        var row = new PrefsRow(id);
        row.AddHint(textKey, 0);
        return row;
    }

    /// <summary>A label in the fixed left column (with room after it for the badge) and <paramref name="side"/> right of it.</summary>
    private void BuildLabelLine(string labelKey, Godot.Control side)
    {
        var cell = new HBoxContainer { CustomMinimumSize = new Vector2(PrefsStyle.LabelColumnWidth, 0) };
        cell.AddThemeConstantOverride("separation", 6);
        _label = PrefsStyle.MakeLabel(L10n.Tr(labelKey));
        cell.AddChild(_label);
        _badgeHost = cell;

        _line = new HBoxContainer();
        _line.AddThemeConstantOverride("separation", PrefsStyle.ColumnGap);
        _line.AddChild(cell);
        _line.AddChild(side);
        AddChild(_line);
    }

    /// <summary>The one-sentence explanation below the row, indented by <paramref name="indent"/> px.</summary>
    private void AddHint(string? hintKey, int indent)
    {
        if (hintKey == null) return;
        HintKey = hintKey;
        var hint = PrefsStyle.MakeHint(L10n.Tr(hintKey));
        if (indent <= 0)
        {
            AddChild(hint);
            return;
        }
        var margin = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        margin.AddThemeConstantOverride("margin_left", indent);
        margin.AddChild(hint);
        AddChild(margin);
    }
}
