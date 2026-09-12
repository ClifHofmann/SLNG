using System;
using Godot;

namespace SLNG.App.UI;

/// <summary>
/// Camera preferences, persisted to user://preferences.cfg under a "camera" section -- same
/// ConfigFile pattern and file as <see cref="UiSettings"/> / ToolbarSettings.
///
/// Two groups:
/// <list type="bullet">
///   <item>Pad speeds (BUG-UI-02) -- multipliers over <see cref="CameraHUD"/>'s built-in per-frame
///     step, 1.0 == shipped default. <see cref="CameraHUD"/> reads these live each frame.</item>
///   <item>View settings (FEAT-UI-12) -- FOV, rear-view distance and focus height.
///     <see cref="AvatarController"/> reads FOV / focus height live and the rear distance on the
///     next camera reset.</item>
/// </list>
/// </summary>
public sealed class CameraSettings
{
    private const string ConfigPath = "user://preferences.cfg";
    private const string Section = "camera";

    // 0.1 = 10% of the built-in step. The orbit pad in particular is fast at 100%, so the low
    // end has to reach genuinely slow (orbit at 10% is ~0.004 rad/frame, ~14 deg/s at 60 fps).
    public const float MinMultiplier = 0.1f;
    public const float MaxMultiplier = 3.0f;

    public const float MinFov = 50f;
    public const float MaxFov = 100f;

    /// <summary>Second Life's own default vertical field of view: <c>DEFAULT_FIELD_OF_VIEW =
    /// 60.f * DEG_TO_RAD</c> (indra/llmath/llcamera.h), which is also the shipped default of the
    /// viewer's <c>CameraAngle</c> setting (<c>1.047197551</c> rad = 60.000°, app_settings/
    /// settings.xml). SLNG shipped Godot's 75° instead, which is not a cosmetic difference: at the
    /// same camera distance a 60° view is <c>tan(37.5°)/tan(30°)</c> = <b>1.33x</b> more magnified,
    /// so the avatar simply renders a third smaller than in the reference viewer — and pulling the
    /// camera closer to compensate exaggerates perspective, which both shrinks the head relative to
    /// anything nearer the camera and visibly distorts a face in close-up. BUG-AVATAR-07.</summary>
    public const float DefaultFov = 60f;

    /// <summary>The pre-BUG-AVATAR-07 default (Godot's own). A stored value of exactly this is
    /// treated as "never chosen" by <see cref="Load"/> and migrated once to
    /// <see cref="DefaultFov"/>; see FovVersion.</summary>
    private const float LegacyDefaultFov = 75f;

    /// <summary>Bumped when <see cref="DefaultFov"/> changes, so an existing preferences.cfg does
    /// not pin every user to the old default forever. Stored alongside the value itself.</summary>
    private const int FovVersion = 1;

    public const float MinDistance = 1.0f;
    public const float MaxDistance = 20.0f;
    public const float DefaultDistance = 4.0f;

    public const float MinFocusHeight = 0.5f;
    public const float MaxFocusHeight = 3.0f;
    public const float DefaultFocusHeight = 1.8f;

    /// <summary>Orbit pad (rotate around the avatar / focus point).</summary>
    public float OrbitSpeed { get; private set; } = 1.0f;

    /// <summary>Pan pad (shift the camera sideways / up / down).</summary>
    public float PanSpeed { get; private set; } = 1.0f;

    /// <summary>Zoom in / out buttons.</summary>
    public float ZoomSpeed { get; private set; } = 1.0f;

    /// <summary>Vertical field of view in degrees — see <see cref="DefaultFov"/>.</summary>
    public float Fov { get; private set; } = DefaultFov;

    /// <summary>Resting third-person camera distance behind the avatar, applied on camera reset.</summary>
    public float RearDistance { get; private set; } = DefaultDistance;

    /// <summary>Height above the avatar origin the camera looks at / orbits around (was a hardcoded 1.8).</summary>
    public float FocusHeight { get; private set; } = DefaultFocusHeight;

    public void Load()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(ConfigPath) != Error.Ok) return;
        OrbitSpeed = ClampMul((float)cfg.GetValue(Section, "orbit_speed", 1.0));
        PanSpeed = ClampMul((float)cfg.GetValue(Section, "pan_speed", 1.0));
        ZoomSpeed = ClampMul((float)cfg.GetValue(Section, "zoom_speed", 1.0));
        Fov = Mathf.Clamp((float)cfg.GetValue(Section, "fov", DefaultFov), MinFov, MaxFov);
        // One-time migration off Godot's 75° default. Only touches a value that is EXACTLY the old
        // default and was written before this version existed — a FOV the user actually picked is
        // left alone, and once fov_version is stored this never runs again.
        if ((int)cfg.GetValue(Section, "fov_version", 0) < FovVersion)
        {
            if (Mathf.IsEqualApprox(Fov, LegacyDefaultFov)) SetFov(DefaultFov);
            Persist("fov_version", FovVersion, _ => { });
        }
        RearDistance = Mathf.Clamp((float)cfg.GetValue(Section, "rear_distance", DefaultDistance), MinDistance, MaxDistance);
        FocusHeight = Mathf.Clamp((float)cfg.GetValue(Section, "focus_height", DefaultFocusHeight), MinFocusHeight, MaxFocusHeight);
    }

    public void SetOrbitSpeed(float value) => Persist("orbit_speed", ClampMul(value), v => OrbitSpeed = v);
    public void SetPanSpeed(float value) => Persist("pan_speed", ClampMul(value), v => PanSpeed = v);
    public void SetZoomSpeed(float value) => Persist("zoom_speed", ClampMul(value), v => ZoomSpeed = v);
    public void SetFov(float value) => Persist("fov", Mathf.Clamp(value, MinFov, MaxFov), v => Fov = v);
    public void SetRearDistance(float value) => Persist("rear_distance", Mathf.Clamp(value, MinDistance, MaxDistance), v => RearDistance = v);
    public void SetFocusHeight(float value) => Persist("focus_height", Mathf.Clamp(value, MinFocusHeight, MaxFocusHeight), v => FocusHeight = v);

    private static void Persist(string key, float clamped, Action<float> assign)
    {
        assign(clamped);

        var cfg = new ConfigFile();
        cfg.Load(ConfigPath); // preserve sections owned by other features (UiSettings, ToolbarSettings)
        cfg.SetValue(Section, key, clamped);
        cfg.Save(ConfigPath);
    }

    private static float ClampMul(float value) => Mathf.Clamp(value, MinMultiplier, MaxMultiplier);
}
