using Godot;

namespace SLNG.App.UI;

/// <summary>
/// FEAT-ANIM-03: local animation preferences, persisted to user://preferences.cfg under an
/// "animation" section — same ConfigFile and pattern as <see cref="AvatarHoverSettings"/> /
/// <see cref="CameraSettings"/>, so all of them coexist without knowing about each other.
///
/// Holds the user's choice and its persistence only; the renderer reads it.
/// </summary>
public sealed class AnimationSettings
{
    private const string ConfigPath = "user://preferences.cfg";
    private const string Section = "animation";

    /// <summary>
    /// Whether a furniture pose outranks a worn AO HUD while seated.
    ///
    /// <para>Default <b>on</b>, and that is a deliberate departure from the reference viewer rather
    /// than an imitation of it: there, the two are blended purely by priority and the AO commonly
    /// wins, which is why AO HUDs ship a "disable while seated" patch script. Defaulting to off
    /// would mean shipping the problem and a switch to fix it. The switch exists for the case this
    /// rule guesses wrong — a worn animator that should keep playing while you sit.</para>
    /// </summary>
    public bool SeatPoseOverridesAo { get; private set; } = true;

    public void Load()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(ConfigPath) != Error.Ok) return;
        SeatPoseOverridesAo = (bool)cfg.GetValue(Section, "seat_pose_overrides_ao", true);
    }

    public void SetSeatPoseOverridesAo(bool value)
    {
        SeatPoseOverridesAo = value;

        var cfg = new ConfigFile();
        cfg.Load(ConfigPath); // preserve sections owned by other features
        cfg.SetValue(Section, "seat_pose_overrides_ao", value);
        cfg.Save(ConfigPath);
    }
}
