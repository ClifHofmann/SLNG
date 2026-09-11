using Godot;

namespace SLNG.App.UI;

/// <summary>
/// FEAT-AVATAR-03: the local hover-height offset, persisted to user://preferences.cfg under a
/// "avatar" section -- same ConfigFile file and pattern as <see cref="DofSettings"/> /
/// <see cref="CameraSettings"/>, so all of them coexist without knowing about each other.
///
/// This holds only the user's chosen value and its persistence; it does not talk to the network
/// or the renderer. Boot.cs owns pushing a changed value onto both the outbound
/// GridSession.SetHoverHeight call and the local self AvatarComponent.HoverOffsetZ so the render
/// updates immediately instead of waiting for the sim to echo it back.
/// </summary>
public sealed class AvatarHoverSettings
{
    private const string ConfigPath = "user://preferences.cfg";
    private const string Section = "avatar";

    public const float MinHoverHeight = -2.0f;
    public const float MaxHoverHeight = 2.0f;
    public const float DefaultHoverHeight = 0f;

    public float HoverHeight { get; private set; } = DefaultHoverHeight;

    public void Load()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(ConfigPath) != Error.Ok) return;
        HoverHeight = Clamp((float)cfg.GetValue(Section, "hover_height", DefaultHoverHeight));
    }

    // Same persist-on-release pattern as DofSettings: an HSlider fires ValueChanged on every step
    // of a drag, so the caller applies live with persist:false while dragging and writes once on
    // DragEnded -- otherwise every pixel of drag would be a synchronous ConfigFile.Save().
    public void SetHoverHeight(float value, bool persist = true)
    {
        float clamped = Clamp(value);
        HoverHeight = clamped;
        if (!persist) return;

        var cfg = new ConfigFile();
        cfg.Load(ConfigPath); // preserve sections owned by other features (DofSettings, CameraSettings, ...)
        cfg.SetValue(Section, "hover_height", clamped);
        cfg.Save(ConfigPath);
    }

    public void ResetToDefault() => SetHoverHeight(DefaultHoverHeight);

    private static float Clamp(float v) => Mathf.Clamp(v, MinHoverHeight, MaxHoverHeight);
}
