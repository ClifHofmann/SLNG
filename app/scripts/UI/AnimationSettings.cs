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
    /// <para>Default <b>off</b>. It shipped on, and three rounds of live testing each turned up a
    /// new case the rule read wrongly — a pose stand sourcing a hand animation, then a mesh body's
    /// 2-joint deformer passing as a pose — each time leaving the avatar worse off than with no
    /// rule at all. There is no signal in the animation stream that reliably separates "an AO
    /// fighting the furniture" from "a HUD deliberately posing me", which is precisely why the
    /// reference viewer does not try and why AO HUDs ship a "disable while seated" script instead.
    /// Off by default means SLNG behaves like every other viewer until someone asks for more.</para>
    ///
    /// <para>The rule itself is kept, and is worth turning on for ordinary furniture: it is what
    /// stops an AO stealing a couch's pose. It is opt-in because it cannot be trusted blind.</para>
    /// </summary>
    public bool SeatPoseOverridesAo { get; private set; }

    /// <summary>
    /// Whether moving forward/backward always runs instead of walking (Ctrl+R / Firestorm movement pref).
    /// </summary>
    public bool AlwaysRun { get; private set; }

    public event System.Action<bool>? SeatPoseOverridesAoChanged;
    public event System.Action<bool>? AlwaysRunChanged;

    public void Load()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(ConfigPath) != Error.Ok) return;
        SeatPoseOverridesAo = (bool)cfg.GetValue(Section, "seat_pose_overrides_ao", false);
        AlwaysRun = (bool)cfg.GetValue(Section, "always_run", false);
    }

    public void SetSeatPoseOverridesAo(bool value)
    {
        SeatPoseOverridesAo = value;

        var cfg = new ConfigFile();
        cfg.Load(ConfigPath); // preserve sections owned by other features
        cfg.SetValue(Section, "seat_pose_overrides_ao", value);
        cfg.Save(ConfigPath);
        SeatPoseOverridesAoChanged?.Invoke(value);
    }

    public void SetAlwaysRun(bool value)
    {
        if (AlwaysRun == value) return;
        AlwaysRun = value;

        var cfg = new ConfigFile();
        cfg.Load(ConfigPath);
        cfg.SetValue(Section, "always_run", value);
        cfg.Save(ConfigPath);
        AlwaysRunChanged?.Invoke(value);
    }
}
