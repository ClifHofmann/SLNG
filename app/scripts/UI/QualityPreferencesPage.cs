using System;
using Godot;

namespace SLNG.App.UI;

/// <summary>
/// "Quality / Hardware" tab content for PreferencesWindow.
/// </summary>
public partial class QualityPreferencesPage : VBoxContainer
{
    private GraphicsSettings _settings = null!;
    private Action _apply = null!;
    private Label _drawDistanceValue = null!;

    // Kept so Refresh can push changed values back into the controls.
    private OptionButton _vsyncOption = null!;
    private OptionButton _fpsOption = null!;
    private HSlider _drawSlider = null!;
    private OptionButton _msaaOption = null!;
    private OptionButton _shadowResOption = null!;

    private bool _refreshing;

    private static readonly int[] FpsChoices = { 0, 30, 60, 90, 120, 144, 240 };
    private static readonly int[] ShadowResChoices = { 1024, 2048, 4096 };

    public override void _Ready() => AddThemeConstantOverride("separation", 8);

    public void Initialize(GraphicsSettings settings, Action apply)
    {
        _settings = settings;
        _apply = apply;

        AddHeading(L10n.Tr("ui.preferences.graphics_heading"));

        // --- V-Sync -------------------------------------------------------------------------
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

        int resIndex = Array.IndexOf(ShadowResChoices, _settings.ShadowResolution);
        AddRow(L10n.Tr("ui.preferences.shadow_resolution"), BuildOption(
            new[]
            {
                L10n.Tr("ui.preferences.shadow_res_1024"),
                L10n.Tr("ui.preferences.shadow_res_2048"),
                L10n.Tr("ui.preferences.shadow_res_4096"),
            },
            resIndex < 0 ? 2 : resIndex,
            index => { if (!_refreshing) { _settings.SetShadowResolution(ShadowResChoices[index]); _apply(); } },
            out _shadowResOption));
    }

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

            int resIdx = Array.IndexOf(ShadowResChoices, _settings.ShadowResolution);
            _shadowResOption.Select(resIdx < 0 ? 2 : resIdx);
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

    private void AddRow(string label, Control control)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);

        var name = new Label
        {
            Text = label,
            CustomMinimumSize = new Vector2(120, 0),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.Fill,
        };
        name.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f));
        row.AddChild(name);

        control.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        if (control is OptionButton option)
        {
            option.ClipText = true;
            option.CustomMinimumSize = new Vector2(120, 0);
        }
        row.AddChild(control);

        AddChild(row);
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
}
