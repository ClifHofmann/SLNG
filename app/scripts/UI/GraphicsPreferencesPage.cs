using System;
using Godot;

namespace SLNG.App.UI;

/// <summary>
/// "Graphics" tab content for PreferencesWindow. Mirrors DisplayPreferencesPage: a plain
/// VBoxContainer built in Initialize, so PreferencesWindow itself stays generic (see its AddTab).
///
/// Every control applies immediately rather than on an OK button. These are all settings whose
/// effect you judge by looking at the world behind the dialog -- an Apply step would mean closing
/// the window to see what you changed, then reopening it to change it again.
/// </summary>
public partial class GraphicsPreferencesPage : VBoxContainer
{
    private GraphicsSettings _settings = null!;
    private Action _apply = null!;
    private Label _drawDistanceValue = null!;

    // Kept so Refresh can push changed values back into the controls.
    private OptionButton _vsyncOption = null!;
    private OptionButton _fpsOption = null!;
    private HSlider _drawSlider = null!;
    private OptionButton _msaaOption = null!;
    private CheckBox _shadowsCheck = null!;
    private VBoxContainer _shadowControls = null!;
    private HSlider _shadowSoftnessSlider = null!;
    private Label _shadowSoftnessValue = null!;
    private OptionButton _shadowResOption = null!;
    private HSlider _shadowDistanceSlider = null!;
    private Label _shadowDistanceValue = null!;
    private HSlider _shadowOpacitySlider = null!;
    private Label _shadowOpacityValue = null!;
    private CheckBox _postFxCheck = null!;

    /// <summary>Set while Refresh writes into the controls. HSlider.Value and CheckBox.ButtonPressed
    /// emit their change signals on assignment, so without this a refresh would re-enter the very
    /// handlers it is trying to synchronise -- saving and re-applying a value that never changed.
    /// (OptionButton.Select is exempt, but the flag covers it anyway rather than relying on that.)</summary>
    private bool _refreshing;

    /// <summary>Offered frame caps. 0 is "unlimited" and comes first because it is the default and
    /// the only one that matters while V-Sync is on.</summary>
    private static readonly int[] FpsChoices = { 0, 30, 60, 90, 120, 144, 240 };
    private static readonly int[] ShadowResChoices = { 1024, 2048, 4096 };

    public override void _Ready() => AddThemeConstantOverride("separation", 8);

    /// <summary>Call once, right after this page has been added via PreferencesWindow.AddTab.
    /// <paramref name="apply"/> re-applies every setting to the live scene; the page itself has no
    /// access to the viewport, environment or sun, and should not grow one.</summary>
    public void Initialize(GraphicsSettings settings, Action apply)
    {
        _settings = settings;
        _apply = apply;

        AddHeading(L10n.Tr("ui.preferences.graphics_heading"));

        // --- V-Sync -------------------------------------------------------------------------
        // First because it is the one that misleads: it caps the frame rate to the display's
        // refresh, so a client that cannot hold 60 fps reads as a rock-steady 30 rather than as
        // "just short of 60". Turning it off is how you find out what the renderer can really do.
        AddRow(L10n.Tr("ui.preferences.vsync"), BuildOption(
            new[]
            {
                L10n.Tr("ui.preferences.vsync_off"),
                L10n.Tr("ui.preferences.vsync_on"),
                L10n.Tr("ui.preferences.vsync_adaptive"),
                L10n.Tr("ui.preferences.vsync_mailbox"),
            },
            _settings.VSyncMode,
            index => { if (!_refreshing) { _settings.SetVSyncMode(index); _apply(); } },
            out _vsyncOption));

        AddHint(L10n.Tr("ui.preferences.vsync_hint"));

        // --- Frame cap ----------------------------------------------------------------------
        var fpsLabels = new string[FpsChoices.Length];
        fpsLabels[0] = L10n.Tr("ui.preferences.fps_unlimited");
        for (int i = 1; i < FpsChoices.Length; i++) fpsLabels[i] = FpsChoices[i].ToString();

        int fpsIndex = Array.IndexOf(FpsChoices, _settings.MaxFps);
        AddRow(L10n.Tr("ui.preferences.fps_limit"), BuildOption(
            fpsLabels,
            fpsIndex < 0 ? 0 : fpsIndex,
            index => { if (!_refreshing) { _settings.SetMaxFps(FpsChoices[index]); _apply(); } },
            out _fpsOption));

        AddChild(new HSeparator());

        // --- Draw distance ------------------------------------------------------------------
        // The single most effective control here on a busy region: cost scales with the area
        // drawn, so halving it quarters the object count rather than halving it.
        AddHeading(L10n.Tr("ui.preferences.draw_distance_heading"));

        var drawRow = new HBoxContainer();
        drawRow.AddThemeConstantOverride("separation", 12);
        AddChild(drawRow);

        _drawSlider = new HSlider
        {
            MinValue = 32,
            MaxValue = 512,
            Step = 16,
            Value = _settings.DrawDistance,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(200, 0),
        };
        drawRow.AddChild(_drawSlider);

        _drawDistanceValue = new Label
        {
            Text = $"{_settings.DrawDistance:0} m",
            CustomMinimumSize = new Vector2(56, 0),
        };
        _drawDistanceValue.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.8f));
        drawRow.AddChild(_drawDistanceValue);

        _drawSlider.ValueChanged += value =>
        {
            _drawDistanceValue.Text = $"{value:0} m";
            if (_refreshing) return;
            _settings.SetDrawDistance((float)value);
            _apply();
        };

        AddChild(new HSeparator());

        // --- Quality ------------------------------------------------------------------------
        AddHeading(L10n.Tr("ui.preferences.quality_heading"));

        AddRow(L10n.Tr("ui.preferences.msaa"), BuildOption(
            new[]
            {
                L10n.Tr("ui.preferences.off"),
                "2x", "4x", "8x",
            },
            _settings.Msaa,
            index => { if (!_refreshing) { _settings.SetMsaa(index); _apply(); } },
            out _msaaOption));

        _shadowsCheck = AddCheck(L10n.Tr("ui.preferences.shadows"), _settings.Shadows,
                                 on =>
                                 {
                                     _shadowControls.Visible = on;
                                     if (!_refreshing) { _settings.SetShadows(on); _apply(); }
                                 });

        _shadowControls = new VBoxContainer();
        _shadowControls.AddThemeConstantOverride("separation", 6);
        _shadowControls.Visible = _settings.Shadows;
        AddChild(_shadowControls);

        AddSliderRow(_shadowControls, L10n.Tr("ui.preferences.shadow_softness"), 0.1f, 3.0f, 0.1f, _settings.ShadowBlur,
                     "{0:0.0}", val => _settings.SetShadowBlur(val), out _shadowSoftnessSlider, out _shadowSoftnessValue);

        int resIndex = Array.IndexOf(ShadowResChoices, _settings.ShadowResolution);
        var shadowResRow = new HBoxContainer();
        shadowResRow.AddThemeConstantOverride("separation", 12);
        var resLabel = new Label
        {
            Text = L10n.Tr("ui.preferences.shadow_resolution"),
            CustomMinimumSize = new Vector2(130, 0),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.Fill,
        };
        resLabel.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f));
        shadowResRow.AddChild(resLabel);

        var resOption = BuildOption(
            new[]
            {
                L10n.Tr("ui.preferences.shadow_res_1024"),
                L10n.Tr("ui.preferences.shadow_res_2048"),
                L10n.Tr("ui.preferences.shadow_res_4096"),
            },
            resIndex < 0 ? 2 : resIndex,
            index => { if (!_refreshing) { _settings.SetShadowResolution(ShadowResChoices[index]); _apply(); } },
            out _shadowResOption);
        resOption.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        shadowResRow.AddChild(resOption);
        _shadowControls.AddChild(shadowResRow);

        AddSliderRow(_shadowControls, L10n.Tr("ui.preferences.shadow_distance"), 32f, 300f, 16f, _settings.ShadowDistance,
                     "{0:0} m", val => _settings.SetShadowDistance(val), out _shadowDistanceSlider, out _shadowDistanceValue);

        AddSliderRow(_shadowControls, L10n.Tr("ui.preferences.shadow_opacity"), 0.1f, 1.0f, 0.05f, _settings.ShadowOpacity,
                     "{0:P0}", val => _settings.SetShadowOpacity(val), out _shadowOpacitySlider, out _shadowOpacityValue);

        _postFxCheck = AddCheck(L10n.Tr("ui.preferences.post_fx"), _settings.PostFx,
                                on => { if (!_refreshing) { _settings.SetPostFx(on); _apply(); } });

        AddHint(L10n.Tr("ui.preferences.post_fx_hint"));
    }

    /// <summary>
    /// Re-reads every value from the settings object into the controls.
    ///
    /// Needed because the settings are not only changed from this page: F2 toggles post-processing
    /// and F3/F4 nudge the draw distance. Built once at startup and never refreshed, the controls
    /// would drift out of step with the state they claim to show -- the same class of fault as the
    /// page being built before the saved values were loaded, which had shadows genuinely off after
    /// login while the checkbox stayed ticked.
    /// </summary>
    public void Refresh()
    {
        _refreshing = true;
        try
        {
            _vsyncOption.Select(Mathf.Clamp(_settings.VSyncMode, 0, _vsyncOption.ItemCount - 1));

            int fpsIndex = Array.IndexOf(FpsChoices, _settings.MaxFps);
            _fpsOption.Select(fpsIndex < 0 ? 0 : fpsIndex);

            _drawSlider.Value = _settings.DrawDistance;
            _drawDistanceValue.Text = $"{_settings.DrawDistance:0} m";

            _msaaOption.Select(Mathf.Clamp(_settings.Msaa, 0, _msaaOption.ItemCount - 1));
            _shadowsCheck.ButtonPressed = _settings.Shadows;
            _shadowControls.Visible = _settings.Shadows;

            _shadowSoftnessSlider.Value = _settings.ShadowBlur;
            _shadowSoftnessValue.Text = $"{_settings.ShadowBlur:0.0}";

            int resIdx = Array.IndexOf(ShadowResChoices, _settings.ShadowResolution);
            _shadowResOption.Select(resIdx < 0 ? 2 : resIdx);

            _shadowDistanceSlider.Value = _settings.ShadowDistance;
            _shadowDistanceValue.Text = $"{_settings.ShadowDistance:0} m";

            _shadowOpacitySlider.Value = _settings.ShadowOpacity;
            _shadowOpacityValue.Text = $"{_settings.ShadowOpacity:P0}";

            _postFxCheck.ButtonPressed = _settings.PostFx;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void AddHeading(string text)
    {
        var heading = new Label { Text = text };
        heading.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.8f));
        AddChild(heading);
    }

    private void AddHint(string text)
    {
        var hint = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        hint.AddThemeColorOverride("font_color", new Color(0.5f, 0.5f, 0.5f));
        hint.AddThemeFontSizeOverride("font_size", 12);
        AddChild(hint);
    }

    /// <summary>Label on the left, control on the right, so the tab reads as a settings list rather
    /// than a stack of unlabelled dropdowns.</summary>
    private void AddRow(string label, Control control)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);

        var name = new Label
        {
            Text = label,
            CustomMinimumSize = new Vector2(120, 0),
            // The label must be allowed to shrink below its text width, or the row's minimum width
            // is the sum of two pieces of text that neither wrap nor clip.
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.Fill,
        };
        name.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f));
        row.AddChild(name);

        control.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        if (control is OptionButton option)
        {
            // Otherwise the dropdown's minimum width is its longest entry's full text.
            option.ClipText = true;
            option.CustomMinimumSize = new Vector2(120, 0);
        }
        row.AddChild(control);

        AddChild(row);
    }

    private CheckBox AddCheck(string label, bool value, Action<bool> onToggled)
    {
        // A CheckBox does not wrap or clip its text, so its label is a hard floor on the page's
        // width. Long explanations belong in a hint underneath, not in the caption.
        var check = new CheckBox { Text = label, ButtonPressed = value, FocusMode = FocusModeEnum.None };
        check.SizeFlagsHorizontal = SizeFlags.Fill;
        check.Toggled += pressed => onToggled(pressed);
        AddChild(check);
        return check;
    }

    private static OptionButton BuildOption(string[] labels, int selected, Action<int> onSelected,
                                            out OptionButton created)
    {
        var option = new OptionButton();
        created = option;
        foreach (string label in labels) option.AddItem(label);
        option.Select(Mathf.Clamp(selected, 0, labels.Length - 1));
        option.ItemSelected += index => onSelected((int)index);
        return option;
    }

    private void AddSliderRow(VBoxContainer parent, string label, float min, float max, float step, float initialValue,
                              string format, Action<float> onValueChanged, out HSlider createdSlider, out Label createdValue)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);

        var name = new Label
        {
            Text = label,
            CustomMinimumSize = new Vector2(130, 0),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.Fill,
        };
        name.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f));
        row.AddChild(name);

        var slider = new HSlider
        {
            MinValue = min,
            MaxValue = max,
            Step = step,
            Value = initialValue,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(160, 0),
        };
        row.AddChild(slider);

        var valueLabel = new Label
        {
            Text = string.Format(format, initialValue),
            CustomMinimumSize = new Vector2(50, 0),
        };
        valueLabel.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.8f));
        row.AddChild(valueLabel);

        slider.ValueChanged += value =>
        {
            valueLabel.Text = string.Format(format, value);
            if (_refreshing) return;
            onValueChanged((float)value);
            _apply();
        };

        createdSlider = slider;
        createdValue = valueLabel;
        parent.AddChild(row);
    }
}
