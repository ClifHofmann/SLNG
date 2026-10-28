using System;
using Godot;
using SLNG.App.UI.Prefs;

namespace SLNG.App.UI;

/// <summary>
/// The "Movement" and "Avatar" sections of the Camera &amp; movement category (FEAT-UI-79; FEAT-ANIM-03, FEAT-ANIM-10):
/// always run, head follows camera, the typing animation and the seat-pose-over-AO switch. Bound to
/// <see cref="AnimationSettings"/>. A builder rather than a Control: it fills two sections with
/// <see cref="PrefsRow"/>s.
/// </summary>
public sealed class AnimationPreferencesPage
{
    private AnimationSettings _settings = null!;

    /// <summary>Raised when the user changes the seat-pose preference, so Boot can push it onto the
    /// renderer without this page knowing the renderer exists.</summary>
    public event Action<bool>? SeatPoseOverridesAoChanged;

    /// <summary>Fills <paramref name="movement"/> and <paramref name="avatar"/> (Camera &amp; movement), which Boot has
    /// already put in their places. Call once, after the sections are in the window's tree.</summary>
    public void Build(PrefsSection movement, PrefsSection avatar, AnimationSettings settings)
    {
        _settings = settings;

        // Movement / locomotion
        var alwaysRun = movement.AddCheck($"{movement.CategoryId}.{movement.Id}.always_run", "ui.preferences.always_run",
            _settings.AlwaysRun, on => _settings.SetAlwaysRun(on), hintKey: "ui.preferences.always_run_hint");
        KeepInSync(alwaysRun, add => _settings.AlwaysRunChanged += add);

        // Avatar behaviour (FEAT-ANIM-10) and sitting / furniture (FEAT-ANIM-03)
        var headFollows = avatar.AddCheck($"{avatar.CategoryId}.{avatar.Id}.head_follows_camera", "ui.preferences.head_follows_camera",
            _settings.HeadFollowsCamera, on => _settings.SetHeadFollowsCamera(on),
            hintKey: "ui.preferences.head_follows_camera_hint");
        KeepInSync(headFollows, add => _settings.HeadFollowsCameraChanged += add);

        var typing = avatar.AddCheck($"{avatar.CategoryId}.{avatar.Id}.typing_animation", "ui.preferences.play_typing_animation",
                _settings.PlayTypingAnimation, on => _settings.SetPlayTypingAnimation(on),
                hintKey: "ui.preferences.play_typing_animation_hint")
            .WithKeywords("PlayTypingAnim");
        KeepInSync(typing, add => _settings.PlayTypingAnimationChanged += add);

        // The hint is spelled out because the behaviour is a departure from what other viewers do, and someone
        // who has patched their AO HUD for this needs to know which of the two is now in charge.
        avatar.AddCheck($"{avatar.CategoryId}.{avatar.Id}.seat_pose_overrides_ao", "ui.preferences.seat_pose_overrides_ao",
            _settings.SeatPoseOverridesAo,
            on =>
            {
                _settings.SetSeatPoseOverridesAo(on);
                SeatPoseOverridesAoChanged?.Invoke(on);
            },
            hintKey: "ui.preferences.seat_pose_overrides_ao_hint");
    }

    /// <summary>The setting can change from outside this page too (a menu entry, a key): follow it without raising
    /// the checkbox's own Toggled, which would write the value back.</summary>
    private static void KeepInSync(PrefsRow row, Action<Action<bool>> subscribe)
    {
        var check = row.ControlAs<CheckBox>()!;
        subscribe(on =>
        {
            if (check.ButtonPressed != on) check.SetPressedNoSignal(on);
        });
    }
}
