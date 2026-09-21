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
    private CheckBox _alwaysRunCheck = null!;

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

        // Section: Movement / Locomotion
        var moveHeading = new Label { Text = L10n.Tr("ui.preferences.locomotion_heading") };
        moveHeading.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.8f));
        AddChild(moveHeading);

        _alwaysRunCheck = new CheckBox
        {
            Text = L10n.Tr("ui.preferences.always_run"),
            ButtonPressed = _settings.AlwaysRun,
        };
        _alwaysRunCheck.Toggled += on =>
        {
            _settings.SetAlwaysRun(on);
        };
        _settings.AlwaysRunChanged += on =>
        {
            if (_alwaysRunCheck != null && _alwaysRunCheck.ButtonPressed != on)
            {
                _alwaysRunCheck.SetPressedNoSignal(on);
            }
        };
        AddChild(_alwaysRunCheck);

        var moveHint = new Label
        {
            Text = L10n.Tr("ui.preferences.always_run_hint"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        moveHint.AddThemeFontSizeOverride("font_size", 11);
        moveHint.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
        AddChild(moveHint);

        var separator = new HSeparator();
        separator.AddThemeConstantOverride("separation", 16);
        AddChild(separator);

        // Section: Sitting / Furniture
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
        hint.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
        AddChild(hint);

        var separator2 = new HSeparator();
        separator2.AddThemeConstantOverride("separation", 16);
        AddChild(separator2);

        // Section: Avatar Behavior (FEAT-ANIM-10)
        var behavHeading = new Label { Text = L10n.Tr("ui.preferences.avatar_behavior_heading") };
        behavHeading.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.8f));
        AddChild(behavHeading);

        var typingCheck = new CheckBox
        {
            Text = L10n.Tr("ui.preferences.play_typing_animation"),
            ButtonPressed = _settings.PlayTypingAnimation,
        };
        typingCheck.Toggled += on => _settings.SetPlayTypingAnimation(on);
        _settings.PlayTypingAnimationChanged += on =>
        {
            if (typingCheck != null && typingCheck.ButtonPressed != on)
            {
                typingCheck.SetPressedNoSignal(on);
            }
        };
        AddChild(typingCheck);

        var typingHint = new Label
        {
            Text = L10n.Tr("ui.preferences.play_typing_animation_hint"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        typingHint.AddThemeFontSizeOverride("font_size", 11);
        typingHint.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
        AddChild(typingHint);

        var gazeCheck = new CheckBox
        {
            Text = L10n.Tr("ui.preferences.head_follows_camera"),
            ButtonPressed = _settings.HeadFollowsCamera,
        };
        gazeCheck.Toggled += on => _settings.SetHeadFollowsCamera(on);
        _settings.HeadFollowsCameraChanged += on =>
        {
            if (gazeCheck != null && gazeCheck.ButtonPressed != on)
            {
                gazeCheck.SetPressedNoSignal(on);
            }
        };
        AddChild(gazeCheck);

        var gazeHint = new Label
        {
            Text = L10n.Tr("ui.preferences.head_follows_camera_hint"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        gazeHint.AddThemeFontSizeOverride("font_size", 11);
        gazeHint.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
        AddChild(gazeHint);
    }
}
