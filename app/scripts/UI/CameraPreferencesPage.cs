using Godot;

namespace SLNG.App.UI;

/// <summary>
/// "Camera" tab content for PreferencesWindow (BUG-UI-02 follow-up): sliders for the
/// Camera-Controls pad speeds, bound to <see cref="CameraSettings"/>. Kept as its own reusable
/// Control (mirrors <see cref="DisplayPreferencesPage"/>) so PreferencesWindow itself stays
/// generic -- see PreferencesWindow.AddTab.
/// </summary>
public partial class CameraPreferencesPage : VBoxContainer
{
    private CameraSettings _settings = null!;

    // Kept only for ResetToDefaults -- each row's % label is updated by its own ValueChanged closure.
    private HSlider _orbitSlider = null!;
    private HSlider _panSlider = null!;
    private HSlider _zoomSlider = null!;

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 10);
    }

    /// <summary>Builds the speed sliders. Call once, right after this page has been added via
    /// PreferencesWindow.AddTab (mirrors DisplayPreferencesPage.Initialize).</summary>
    public void Initialize(CameraSettings settings)
    {
        _settings = settings;

        var heading = new Label { Text = L10n.Tr("ui.preferences.camera_speed_heading") };
        heading.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.8f));
        AddChild(heading);

        _orbitSlider = AddSpeedRow(
            L10n.Tr("ui.preferences.camera_orbit_speed"), _settings.OrbitSpeed,
            v => _settings.SetOrbitSpeed(v));
        _panSlider = AddSpeedRow(
            L10n.Tr("ui.preferences.camera_pan_speed"), _settings.PanSpeed,
            v => _settings.SetPanSpeed(v));
        _zoomSlider = AddSpeedRow(
            L10n.Tr("ui.preferences.camera_zoom_speed"), _settings.ZoomSpeed,
            v => _settings.SetZoomSpeed(v));

        var hint = new Label
        {
            Text = L10n.Tr("ui.preferences.camera_speed_hint"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        hint.AddThemeColorOverride("font_color", new Color(0.5f, 0.5f, 0.5f));
        hint.AddThemeFontSizeOverride("font_size", 12);
        AddChild(hint);

        var resetButton = new Button
        {
            Text = L10n.Tr("ui.preferences.camera_speed_reset"),
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
            FocusMode = Control.FocusModeEnum.None,
        };
        resetButton.Pressed += ResetToDefaults;
        AddChild(resetButton);
    }

    private HSlider AddSpeedRow(string label, float initial, System.Action<float> onChange)
    {
        var caption = new Label { Text = label };
        caption.AddThemeColorOverride("font_color", new Color(0.75f, 0.75f, 0.75f));
        AddChild(caption);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        AddChild(row);

        var slider = new HSlider
        {
            MinValue = CameraSettings.MinMultiplier,
            MaxValue = CameraSettings.MaxMultiplier,
            Step = 0.05,
            Value = initial,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(200, 0),
        };
        row.AddChild(slider);

        var valueLabel = new Label
        {
            Text = FormatPercent(initial),
            CustomMinimumSize = new Vector2(48, 0),
        };
        valueLabel.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.8f));
        row.AddChild(valueLabel);

        slider.ValueChanged += value =>
        {
            valueLabel.Text = FormatPercent((float)value);
            onChange((float)value);
        };

        return slider;
    }

    private void ResetToDefaults()
    {
        // Assigning Value fires ValueChanged, which persists and updates the % label.
        _orbitSlider.Value = 1.0;
        _panSlider.Value = 1.0;
        _zoomSlider.Value = 1.0;
    }

    private static string FormatPercent(float multiplier) => $"{Mathf.RoundToInt(multiplier * 100)}%";
}
