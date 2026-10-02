using Godot;

namespace SLNG.App.UI;

/// <summary>
/// FEAT-LAND-02: one read-only setting of the Land-Info Options / Media / Sound tabs. It shows what the
/// sim said and cannot be toggled; a tooltip says changing it is not available yet (FEAT-LAND-09).
///
/// <para>It has three looks: ticked, unticked, and <b>unknown</b> (<c>null</c>) -- for a value this
/// viewer cannot read yet. Unknown is drawn as text with no box at all, so it can never be mistaken for
/// "unticked". An inapplicable setting (the viewer greys it: loop on a web page, restrict-voice while
/// voice is off) is drawn dimmed.</para>
///
/// <para><b>Idiom.</b> <c>CheckBox.Disabled = true</c>, like the disabled buttons of the General tab. A
/// disabled control still shows its tooltip in Godot 4 (the tooltip belongs to <c>Control</c>, not to
/// <c>BaseButton</c>'s input handling); the row itself is a <c>Pass</c> container so the hover reaches it
/// even where the box is hidden.</para>
/// </summary>
internal sealed partial class LandReadOnlyCheck : HBoxContainer
{
    private const float DimmedAlpha = 0.5f;

    private readonly CheckBox _box;
    private readonly Label _unknown;
    private string _label = string.Empty;

    internal LandReadOnlyCheck(string label = "")
    {
        MouseFilter = MouseFilterEnum.Pass;
        TooltipText = L10n.Tr("ui.land.read_only_hint");

        _box = new CheckBox
        {
            Disabled = true,
            FocusMode = FocusModeEnum.None,
            MouseFilter = MouseFilterEnum.Pass,
            ClipText = false,
        };
        // A disabled box would otherwise draw its text in the dimmed "disabled" colour, which is hard to
        // read for a value the user is meant to read.
        _box.AddThemeColorOverride("font_disabled_color", UiTheme.SecondaryText.Lightened(0.35f));
        AddChild(_box);

        _unknown = new Label { Visible = false, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _unknown.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
        AddChild(_unknown);

        SetLabel(label);
    }

    /// <summary>The label text (already translated).</summary>
    internal void SetLabel(string text)
    {
        _label = text;
        _box.Text = text;
        _unknown.Text = L10n.TrFormat("ui.land.flag_unknown", text);
    }

    /// <summary>Shows a value: ticked, unticked, or null for "unknown". <paramref name="applicable"/> false
    /// draws the setting dimmed.</summary>
    internal void Show(bool? value, bool applicable = true)
    {
        _box.Visible = value.HasValue;
        _unknown.Visible = !value.HasValue;
        // No signal: the box is disabled and nothing listens, but a value pushed by the code must never
        // look like a user toggle.
        _box.SetPressedNoSignal(value == true);
        Modulate = applicable ? Colors.White : new Color(1, 1, 1, DimmedAlpha);
        TooltipText = value.HasValue ? L10n.Tr("ui.land.read_only_hint") : L10n.Tr("ui.land.unknown_hint");
    }

    /// <summary>Convenience for the pure <see cref="LandInfoFormat.CheckState"/> a rule returned.</summary>
    internal void Show(LandInfoFormat.CheckState state)
    {
        SetLabel(L10n.Tr(state.LabelKey));
        Show(state.Ticked, state.Applicable);
    }

    // --- for the selftest ----------------------------------------------------------------------------

    /// <summary>True / false, or null while it is drawn as "unknown".</summary>
    internal bool? Value => _box.Visible ? _box.ButtonPressed : null;

    internal string Label => _label;

    /// <summary>True when the box is not drawn at all because the value is unknown.</summary>
    internal bool IsUnknown => !_box.Visible && _unknown.Visible;

    /// <summary>The text of the "unknown" form.</summary>
    internal string UnknownText => _unknown.Text;

    /// <summary>True when a click could change the box (it never can).</summary>
    internal bool Toggleable => !_box.Disabled;

    internal bool Dimmed => Modulate.A < 1f;
}
