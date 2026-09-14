using Godot;
using System;

namespace SLNG.App.UI;

/// <summary>
/// "Animation" tab content for PreferencesWindow (FEAT-ANIM-03). Its own reusable Control, same
/// shape as <see cref="DisplayPreferencesPage"/> / <see cref="CameraPreferencesPage"/>, so
/// PreferencesWindow itself stays generic — see PreferencesWindow.AddTab.
/// </summary>
public partial class AnimationPreferencesPage : VBoxContainer
{
    private AnimationSettings _settings = null!;

    /// <summary>Raised when the user changes the seat-pose preference, so Boot can push it onto the
    /// renderer without this page knowing the renderer exists.</summary>
    public event Action<bool>? SeatPoseOverridesAoChanged;

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 10);
    }

    /// <summary>Call once, right after this page has been added via PreferencesWindow.AddTab.</summary>
    public void Initialize(AnimationSettings settings)
    {
        _settings = settings;

        var heading = new Label { Text = L10n.Tr("ui.preferences.animation_seating_heading") };
        heading.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.8f));
        AddChild(heading);

        var seatPose = new CheckBox
        {
            Text = L10n.Tr("ui.preferences.seat_pose_overrides_ao"),
            ButtonPressed = _settings.SeatPoseOverridesAo,
        };
        seatPose.Toggled += on =>
        {
            _settings.SetSeatPoseOverridesAo(on);
            SeatPoseOverridesAoChanged?.Invoke(on);
        };
        AddChild(seatPose);

        // Spelled out because the behaviour is a departure from what other viewers do, and someone
        // who has patched their AO HUD for this needs to know which of the two is now in charge.
        var hint = new Label
        {
            Text = L10n.Tr("ui.preferences.seat_pose_overrides_ao_hint"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        hint.AddThemeFontSizeOverride("font_size", 11);
        hint.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.6f));
        AddChild(hint);
    }
}
