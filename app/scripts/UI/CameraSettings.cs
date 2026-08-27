using System;
using Godot;

namespace SLNG.App.UI;

/// <summary>
/// Camera-Controls pad speeds (BUG-UI-02 follow-up), persisted to user://preferences.cfg under a
/// "camera" section -- same ConfigFile pattern and file as <see cref="UiSettings"/> /
/// ToolbarSettings. Each value is a multiplier over <see cref="CameraHUD"/>'s built-in per-frame
/// step, so 1.0 == the shipped default. <see cref="CameraHUD"/> reads these live each frame, so a
/// slider change takes effect immediately with no rebroadcast needed.
/// </summary>
public sealed class CameraSettings
{
    private const string ConfigPath = "user://preferences.cfg";
    private const string Section = "camera";

    public const float MinMultiplier = 0.25f;
    public const float MaxMultiplier = 3.0f;

    /// <summary>Orbit pad (rotate around the avatar / focus point).</summary>
    public float OrbitSpeed { get; private set; } = 1.0f;

    /// <summary>Pan pad (shift the camera sideways / up / down).</summary>
    public float PanSpeed { get; private set; } = 1.0f;

    /// <summary>Zoom in / out buttons.</summary>
    public float ZoomSpeed { get; private set; } = 1.0f;

    public void Load()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(ConfigPath) != Error.Ok) return;
        OrbitSpeed = Clamp((float)cfg.GetValue(Section, "orbit_speed", 1.0));
        PanSpeed = Clamp((float)cfg.GetValue(Section, "pan_speed", 1.0));
        ZoomSpeed = Clamp((float)cfg.GetValue(Section, "zoom_speed", 1.0));
    }

    public void SetOrbitSpeed(float value) => Persist("orbit_speed", value, v => OrbitSpeed = v);
    public void SetPanSpeed(float value) => Persist("pan_speed", value, v => PanSpeed = v);
    public void SetZoomSpeed(float value) => Persist("zoom_speed", value, v => ZoomSpeed = v);

    private static void Persist(string key, float value, Action<float> assign)
    {
        float clamped = Clamp(value);
        assign(clamped);

        var cfg = new ConfigFile();
        cfg.Load(ConfigPath); // preserve sections owned by other features (UiSettings, ToolbarSettings)
        cfg.SetValue(Section, key, clamped);
        cfg.Save(ConfigPath);
    }

    private static float Clamp(float value) => Mathf.Clamp(value, MinMultiplier, MaxMultiplier);
}
