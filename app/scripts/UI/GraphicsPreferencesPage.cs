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

    /// <summary>Offered frame caps. 0 is "unlimited" and comes first because it is the default and
    /// the only one that matters while V-Sync is on.</summary>
    private static readonly int[] FpsChoices = { 0, 30, 60, 90, 120, 144, 240 };

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
            index => { _settings.SetVSyncMode(index); _apply(); }));

        AddHint(L10n.Tr("ui.preferences.vsync_hint"));

        // --- Frame cap ----------------------------------------------------------------------
        var fpsLabels = new string[FpsChoices.Length];
        fpsLabels[0] = L10n.Tr("ui.preferences.fps_unlimited");
        for (int i = 1; i < FpsChoices.Length; i++) fpsLabels[i] = FpsChoices[i].ToString();

        int fpsIndex = Array.IndexOf(FpsChoices, _settings.MaxFps);
        AddRow(L10n.Tr("ui.preferences.fps_limit"), BuildOption(
            fpsLabels,
            fpsIndex < 0 ? 0 : fpsIndex,
            index => { _settings.SetMaxFps(FpsChoices[index]); _apply(); }));

        AddChild(new HSeparator());

        // --- Draw distance ------------------------------------------------------------------
        // The single most effective control here on a busy region: cost scales with the area
        // drawn, so halving it quarters the object count rather than halving it.
        AddHeading(L10n.Tr("ui.preferences.draw_distance_heading"));

        var drawRow = new HBoxContainer();
        drawRow.AddThemeConstantOverride("separation", 12);
        AddChild(drawRow);

        var drawSlider = new HSlider
        {
            MinValue = 32,
            MaxValue = 512,
            Step = 16,
            Value = _settings.DrawDistance,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(200, 0),
        };
        drawRow.AddChild(drawSlider);

        _drawDistanceValue = new Label
        {
            Text = $"{_settings.DrawDistance:0} m",
            CustomMinimumSize = new Vector2(56, 0),
        };
        _drawDistanceValue.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.8f));
        drawRow.AddChild(_drawDistanceValue);

        drawSlider.ValueChanged += value =>
        {
            _drawDistanceValue.Text = $"{value:0} m";
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
            index => { _settings.SetMsaa(index); _apply(); }));

        AddCheck(L10n.Tr("ui.preferences.shadows"), _settings.Shadows,
                 on => { _settings.SetShadows(on); _apply(); });

        AddCheck(L10n.Tr("ui.preferences.post_fx"), _settings.PostFx,
                 on => { _settings.SetPostFx(on); _apply(); });

        AddHint(L10n.Tr("ui.preferences.post_fx_hint"));
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

        var name = new Label { Text = label, CustomMinimumSize = new Vector2(130, 0) };
        name.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f));
        row.AddChild(name);

        control.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(control);

        AddChild(row);
    }

    private void AddCheck(string label, bool value, Action<bool> onToggled)
    {
        var check = new CheckBox { Text = label, ButtonPressed = value, FocusMode = FocusModeEnum.None };
        check.Toggled += pressed => onToggled(pressed);
        AddChild(check);
    }

    private static OptionButton BuildOption(string[] labels, int selected, Action<int> onSelected)
    {
        var option = new OptionButton();
        foreach (string label in labels) option.AddItem(label);
        option.Select(Mathf.Clamp(selected, 0, labels.Length - 1));
        option.ItemSelected += index => onSelected((int)index);
        return option;
    }
}
