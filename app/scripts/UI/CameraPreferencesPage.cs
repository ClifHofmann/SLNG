using Godot;
using SLNG.App.UI.Prefs;
using SLNG.Core.Camera;

namespace SLNG.App.UI;

/// <summary>
/// The "Camera" section of the Camera &amp; movement category (FEAT-UI-79): view settings -- FOV, rear-view distance,
/// focus height (FEAT-UI-12), Firestorm's "Disable camera constraints" -- and, under Advanced, the pad-speed sliders
/// (BUG-UI-02). Bound to <see cref="CameraSettings"/>. A builder rather than a Control: it fills the section with
/// <see cref="PrefsRow"/>s and keeps the sliders it needs for the reset button and the field-of-view range.
/// </summary>
public sealed class CameraPreferencesPage
{
    private CameraSettings _settings = null!;

    // Kept for ResetToDefaults -- each row's value label is updated by the row's own ValueChanged handler.
    private HSlider _orbitSlider = null!;
    private HSlider _panSlider = null!;
    private HSlider _zoomSlider = null!;
    private HSlider _fovSlider = null!;
    private HSlider _distanceSlider = null!;
    private HSlider _heightSlider = null!;

    // True while the field-of-view slider's range is being moved: changing a bound clamps the value and raises
    // ValueChanged, which must not be written back to the settings as if the person had moved the slider.
    private bool _syncingFovRange;

    /// <summary>Fills <paramref name="section"/> (Camera &amp; movement, "camera"), which Boot has already put in its
    /// place. Call once, after the section is in the window's tree.</summary>
    public void Build(PrefsSection section, CameraSettings settings)
    {
        _settings = settings;
        string prefix = $"{section.CategoryId}.{section.Id}";

        // FEAT-UI-73: the viewer's range for this window shape (5 degrees up to 175 / aspect), re-read whenever the page is
        // shown (llfloaterpreference.cpp:502-503, :618-619). The stored value may lie outside it after a resize and is
        // left alone: the camera applies the clamp itself, every frame.
        _fovSlider = section.AddSlider($"{prefix}.fov", "ui.preferences.camera_fov",
                CameraFovLimits.AbsoluteMinDegrees, CameraFovLimits.AbsoluteMaxDegrees, 1.0, _settings.Fov,
                v => $"{v:0}°", v => { if (!_syncingFovRange) _settings.SetFov((float)v); },
                hintKey: "ui.preferences.camera_fov_hint")
            .WithKeywords("CameraAngle")
            .ControlAs<HSlider>()!;
        RefreshFovRange();
        section.VisibilityChanged += RefreshFovRange;

        // The rear distance is read when the camera is reset, not every frame -- that is what the hint says.
        _distanceSlider = section.AddSlider($"{prefix}.rear_distance", "ui.preferences.camera_rear_distance",
                CameraSettings.MinDistance, CameraSettings.MaxDistance, 0.5, _settings.RearDistance,
                FormatMetres, v => _settings.SetRearDistance((float)v),
                hintKey: "ui.preferences.camera_rear_distance_hint")
            .ControlAs<HSlider>()!;
        _heightSlider = section.AddSlider($"{prefix}.focus_height", "ui.preferences.camera_focus_height",
                CameraSettings.MinFocusHeight, CameraSettings.MaxFocusHeight, 0.1, _settings.FocusHeight,
                FormatMetres, v => _settings.SetFocusHeight((float)v))
            .ControlAs<HSlider>()!;

        // Firestorm's "Disable camera constraints": takes effect at once, like every switch on a Preferences page.
        section.AddCheck($"{prefix}.disable_constraints", "ui.preferences.camera_disable_constraints",
                _settings.DisableConstraints, on => _settings.SetDisableConstraints(on),
                hintKey: "ui.preferences.camera_disable_constraints_hint")
            .WithKeywords("DisableCameraConstraints");

        var resetButton = new Button
        {
            Text = L10n.Tr("ui.preferences.camera_speed_reset"),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
            FocusMode = Control.FocusModeEnum.None,
        };
        resetButton.Pressed += ResetToDefaults;
        section.AddBlock($"{prefix}.reset", resetButton, null, false, "ui.preferences.camera_speed_reset");

        // Advanced: the speed of the on-screen Camera Controls pads.
        section.AddNote($"{prefix}.speed_note", "ui.preferences.camera_speed_hint", advanced: true);
        _orbitSlider = AddSpeedSlider(section, prefix, "orbit_speed", "ui.preferences.camera_orbit_speed",
            _settings.OrbitSpeed, v => _settings.SetOrbitSpeed(v));
        _panSlider = AddSpeedSlider(section, prefix, "pan_speed", "ui.preferences.camera_pan_speed",
            _settings.PanSpeed, v => _settings.SetPanSpeed(v));
        _zoomSlider = AddSpeedSlider(section, prefix, "zoom_speed", "ui.preferences.camera_zoom_speed",
            _settings.ZoomSpeed, v => _settings.SetZoomSpeed(v));
    }

    private static HSlider AddSpeedSlider(PrefsSection section, string prefix, string name, string labelKey, float value, System.Action<float> set)
        => section.AddSlider($"{prefix}.{name}", labelKey,
                CameraSettings.MinMultiplier, CameraSettings.MaxMultiplier, 0.05, value,
                FormatPercent, v => set((float)v), advanced: true)
            .ControlAs<HSlider>()!;

    /// <summary>Gives the field-of-view slider the range the window's shape allows now, showing the stored value
    /// (clamped into it, for display only).</summary>
    private void RefreshFovRange()
    {
        if (_fovSlider == null) return;
        float aspect = CameraSettings.ViewAspect(_fovSlider.GetViewport());
        _syncingFovRange = true;
        try
        {
            _fovSlider.MinValue = CameraFovLimits.MinDegrees(aspect);
            _fovSlider.MaxValue = CameraFovLimits.MaxDegrees(aspect);
            _fovSlider.Value = CameraFovLimits.Clamp(_settings.Fov, aspect);
        }
        finally
        {
            _syncingFovRange = false;
        }
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
