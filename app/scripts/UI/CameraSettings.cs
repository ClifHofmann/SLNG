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
    public const float DefaultFov = 75f;

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

    /// <summary>Vertical field of view in degrees (Godot Camera3D default is 75).</summary>
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
