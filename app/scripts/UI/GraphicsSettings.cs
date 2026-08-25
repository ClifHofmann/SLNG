using Godot;

namespace SLNG.App.UI;

/// <summary>
/// Graphics options, persisted to user://preferences.cfg under a "graphics" section -- same
/// ConfigFile file and pattern as UiSettings and ToolbarSettings, so the three coexist without
/// knowing about each other.
///
/// The selection is deliberately narrow. Every option here is one that FEAT-PERF-01 actually
/// measured as mattering on this client, rather than the full surface of things Godot happens to
/// expose. V-Sync leads because the investigation ended on it: with the freezes fixed, the median
/// frame sat at exactly 31.2 ms, which is two 60 Hz refresh intervals -- a frame that misses the
/// 16.7 ms deadline gets rounded up to the next refresh, so the client reads as a hard 30 fps and
/// hides how much headroom is really missing. Turning V-Sync off is the fastest way to see the
/// true frame rate underneath.
///
/// Applying and persisting are separate on purpose. <see cref="Apply"/> takes the scene objects it
/// needs as arguments instead of holding references, so this class stays a plain settings holder
/// that the UI page and Boot can both use without either owning the other.
/// </summary>
public sealed class GraphicsSettings
{
    private const string ConfigPath = "user://preferences.cfg";
    private const string Section = "graphics";

    /// <summary>Matches DisplayServer.VSyncMode: 0 disabled, 1 enabled, 2 adaptive, 3 mailbox.</summary>
    public int VSyncMode { get; private set; } = (int)DisplayServer.VSyncMode.Enabled;

    /// <summary>0 means unlimited. Only meaningful with V-Sync off, which the UI says out loud.</summary>
    public int MaxFps { get; private set; }

    public float DrawDistance { get; private set; } = RenderConfig.DrawDistance;

    /// <summary>Viewport.Msaa enum value: 0 off, 1 2x, 2 4x, 3 8x.</summary>
    public int Msaa { get; private set; } = (int)Viewport.Msaa.Msaa2X;

    /// <summary>SSAO, SSIL, glow and volumetric fog together. They are toggled as a group because
    /// that is how the existing F2 shortcut in Boot already treats them, and splitting them here
    /// would leave two controls disagreeing about the same four flags.</summary>
    public bool PostFx { get; private set; } = true;

    public bool Shadows { get; private set; } = true;
    public float ShadowBlur { get; private set; } = 1.8f;
    public int ShadowResolution { get; private set; } = 4096;
    public float ShadowDistance { get; private set; } = 150.0f;
    public float ShadowOpacity { get; private set; } = 0.90f;

    public void Load()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(ConfigPath) != Error.Ok) return;

        VSyncMode = (int)cfg.GetValue(Section, "vsync_mode", VSyncMode);
        MaxFps = (int)cfg.GetValue(Section, "max_fps", MaxFps);
        DrawDistance = (float)cfg.GetValue(Section, "draw_distance", DrawDistance);
        Msaa = (int)cfg.GetValue(Section, "msaa", Msaa);
        PostFx = (bool)cfg.GetValue(Section, "post_fx", PostFx);
        Shadows = (bool)cfg.GetValue(Section, "shadows", Shadows);
        ShadowBlur = (float)cfg.GetValue(Section, "shadow_blur", ShadowBlur);
        ShadowResolution = (int)cfg.GetValue(Section, "shadow_resolution", ShadowResolution);
        ShadowDistance = (float)cfg.GetValue(Section, "shadow_distance", ShadowDistance);
        ShadowOpacity = (float)cfg.GetValue(Section, "shadow_opacity", ShadowOpacity);
    }

    private void Save()
    {
        var cfg = new ConfigFile();
        cfg.Load(ConfigPath); // preserve sections owned by other features
        cfg.SetValue(Section, "vsync_mode", VSyncMode);
        cfg.SetValue(Section, "max_fps", MaxFps);
        cfg.SetValue(Section, "draw_distance", DrawDistance);
        cfg.SetValue(Section, "msaa", Msaa);
        cfg.SetValue(Section, "post_fx", PostFx);
        cfg.SetValue(Section, "shadows", Shadows);
        cfg.SetValue(Section, "shadow_blur", ShadowBlur);
        cfg.SetValue(Section, "shadow_resolution", ShadowResolution);
        cfg.SetValue(Section, "shadow_distance", ShadowDistance);
        cfg.SetValue(Section, "shadow_opacity", ShadowOpacity);
        cfg.Save(ConfigPath);
    }

    public void SetVSyncMode(int mode) { VSyncMode = mode; Save(); }
    public void SetMaxFps(int fps) { MaxFps = fps; Save(); }
    public void SetDrawDistance(float metres) { DrawDistance = metres; Save(); }
    public void SetMsaa(int msaa) { Msaa = msaa; Save(); }
    public void SetPostFx(bool on) { PostFx = on; Save(); }
    public void SetShadows(bool on) { Shadows = on; Save(); }
    public void SetShadowBlur(float blur) { ShadowBlur = blur; Save(); }
    public void SetShadowResolution(int res) { ShadowResolution = res; Save(); }
    public void SetShadowDistance(float distance) { ShadowDistance = distance; Save(); }
    public void SetShadowOpacity(float opacity) { ShadowOpacity = opacity; Save(); }

    /// <summary>
    /// Pushes the current values into the engine. Safe to call repeatedly and with nulls -- during
    /// startup the environment and sun do not exist yet, and the window-level settings should still
    /// take effect.
    /// </summary>
    public void Apply(Viewport? viewport, WorldEnvironment? worldEnvironment, DirectionalLight3D? sun)
    {
        DisplayServer.WindowSetVsyncMode((DisplayServer.VSyncMode)VSyncMode);

        // Engine.MaxFps is the frame limiter Godot applies itself. With V-Sync on it is redundant
        // at best and fights the refresh rate at worst, so it is only meaningful as a way to cap
        // an unsynced frame rate -- for instance to stop a menu spinning the GPU at 900 fps.
        Engine.MaxFps = MaxFps;

        RenderConfig.DrawDistance = DrawDistance;

        if (viewport != null) viewport.Msaa3D = (Viewport.Msaa)Msaa;

        RenderingServer.DirectionalShadowAtlasSetSize(ShadowResolution, true);

        if (worldEnvironment?.Environment is { } env)
        {
            env.SsaoEnabled = PostFx;
            env.SsilEnabled = PostFx;
            env.GlowEnabled = PostFx;
            env.VolumetricFogEnabled = PostFx;
        }

        if (sun != null)
        {
            sun.ShadowEnabled = Shadows;
            sun.ShadowBlur = ShadowBlur;
            sun.DirectionalShadowMaxDistance = ShadowDistance;
            sun.ShadowOpacity = ShadowOpacity;
        }
    }
}
