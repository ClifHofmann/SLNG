using Godot;

namespace SLNG.App.UI;

/// <summary>
/// "Camera" tab content for PreferencesWindow: pad-speed sliders (BUG-UI-02) and view settings
/// -- FOV, rear-view distance, focus height (FEAT-UI-12). Bound to <see cref="CameraSettings"/>.
/// Kept as its own reusable Control (mirrors <see cref="DisplayPreferencesPage"/>) so
/// PreferencesWindow itself stays generic -- see PreferencesWindow.AddTab.
/// </summary>
public partial class CameraPreferencesPage : VBoxContainer
{
    private CameraSettings _settings = null!;

    // Kept for ResetToDefaults -- each row's value label is updated by its own ValueChanged closure.
    private HSlider _orbitSlider = null!;
    private HSlider _panSlider = null!;
    private HSlider _zoomSlider = null!;
    private HSlider _fovSlider = null!;
    private HSlider _distanceSlider = null!;
    private HSlider _heightSlider = null!;

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 10);
    }

    /// <summary>Builds the sliders. Call once, right after this page has been added via
    /// PreferencesWindow.AddTab (mirrors DisplayPreferencesPage.Initialize).</summary>
    public void Initialize(CameraSettings settings)
    {
        _settings = settings;

        AddHeading(L10n.Tr("ui.preferences.camera_speed_heading"));

        _orbitSlider = AddSliderRow(L10n.Tr("ui.preferences.camera_orbit_speed"),
            CameraSettings.MinMultiplier, CameraSettings.MaxMultiplier, 0.05f, _settings.OrbitSpeed,
            FormatPercent, v => _settings.SetOrbitSpeed(v));
        _panSlider = AddSliderRow(L10n.Tr("ui.preferences.camera_pan_speed"),
            CameraSettings.MinMultiplier, CameraSettings.MaxMultiplier, 0.05f, _settings.PanSpeed,
            FormatPercent, v => _settings.SetPanSpeed(v));
        _zoomSlider = AddSliderRow(L10n.Tr("ui.preferences.camera_zoom_speed"),
            CameraSettings.MinMultiplier, CameraSettings.MaxMultiplier, 0.05f, _settings.ZoomSpeed,
            FormatPercent, v => _settings.SetZoomSpeed(v));

        AddHint(L10n.Tr("ui.preferences.camera_speed_hint"));

        var separator = new HSeparator();
        separator.AddThemeConstantOverride("separation", 15);
        AddChild(separator);

        AddHeading(L10n.Tr("ui.preferences.camera_view_heading"));

        _fovSlider = AddSliderRow(L10n.Tr("ui.preferences.camera_fov"),
            CameraSettings.MinFov, CameraSettings.MaxFov, 1f, _settings.Fov,
            v => $"{v:0}°", v => _settings.SetFov(v));
        _distanceSlider = AddSliderRow(L10n.Tr("ui.preferences.camera_rear_distance"),
            CameraSettings.MinDistance, CameraSettings.MaxDistance, 0.5f, _settings.RearDistance,
            FormatMetres, v => _settings.SetRearDistance(v));
        _heightSlider = AddSliderRow(L10n.Tr("ui.preferences.camera_focus_height"),
            CameraSettings.MinFocusHeight, CameraSettings.MaxFocusHeight, 0.1f, _settings.FocusHeight,
            FormatMetres, v => _settings.SetFocusHeight(v));

        AddHint(L10n.Tr("ui.preferences.camera_view_hint"));

        var resetButton = new Button
        {
            Text = L10n.Tr("ui.preferences.camera_speed_reset"),
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
            FocusMode = Control.FocusModeEnum.None,
        };
        resetButton.Pressed += ResetToDefaults;
        AddChild(resetButton);
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

    private HSlider AddSliderRow(string label, float min, float max, float step, float initial,
                                System.Func<double, string> format, System.Action<float> onChange)
    {
        var caption = new Label { Text = label };
        caption.AddThemeColorOverride("font_color", new Color(0.75f, 0.75f, 0.75f));
        AddChild(caption);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        AddChild(row);

        var slider = new HSlider
        {
            MinValue = min,
            MaxValue = max,
            Step = step,
            Value = initial,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(200, 0),
        };
        row.AddChild(slider);

        var valueLabel = new Label
        {
            Text = format(initial),
            CustomMinimumSize = new Vector2(52, 0),
        };
        valueLabel.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.8f));
        row.AddChild(valueLabel);

        slider.ValueChanged += value =>
        {
            valueLabel.Text = format(value);
            onChange((float)value);
        };

        return slider;
    }

    private void ResetToDefaults()
    {
        // Assigning Value fires ValueChanged, which persists and updates the value label.
        _orbitSlider.Value = 1.0;
        _panSlider.Value = 1.0;
        _zoomSlider.Value = 1.0;
        _fovSlider.Value = CameraSettings.DefaultFov;
        _distanceSlider.Value = CameraSettings.DefaultDistance;
        _heightSlider.Value = CameraSettings.DefaultFocusHeight;
    }

    private static string FormatPercent(double multiplier) => $"{Mathf.RoundToInt(multiplier * 100)}%";
    private static string FormatMetres(double metres) => $"{metres:0.0} m";
}
