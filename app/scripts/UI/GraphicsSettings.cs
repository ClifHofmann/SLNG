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

    /// <summary>FEAT-PERF-04: MB the <c>GpuCache</c> may hold in textures + meshes before it applies
    /// back-pressure (a global LOD bias + a shrink pass). This is NOT a total-VRAM cap — shadow
    /// maps, render targets and MSAA buffers live outside the cache; the cache is roughly 40 % of
    /// Godot's reported total. Default 1536 keeps today's value; the point of the setting is that
    /// the number now actually binds.</summary>
    public int TextureMemoryMb { get; private set; } = 1536;

    public bool PostFxSsao { get; private set; } = true;
    public bool PostFxSsil { get; private set; } = true;
    public bool PostFxGlow { get; private set; } = true;

    /// <summary>FEAT-RENDER-20: the real, camera-following <c>ReflectionProbe</c> that replaced
    /// BUG-RENDER-20's hand-rolled sky-tint approximation. Toggleable per AGENTS.md's "make visual
    /// features toggleable so they can be profiled and compared" -- off simply hides the node
    /// (<see cref="ReflectionProbe.Visible"/>), which stops it capturing or contributing at all,
    /// letting a shiny face fall back to Godot's plain sky-only IBL for an A/B comparison.</summary>
    public bool PostFxReflectionProbe { get; private set; } = true;

    /// <summary>FEAT-RENDER-21: screen-space reflections. Separate from
    /// <see cref="PostFxReflectionProbe"/> on purpose -- they answer different halves of the same
    /// question and fail in different ways. The probe covers everything, including what is off
    /// screen, but puts nearby objects in the wrong direction (one capture point, no parallax
    /// correction); SSR has correct parallax but only for what the frame already contains. The
    /// reference viewer runs both and mixes SSR over the probe sample
    /// (reflectionProbeF.glsl:883). Being able to switch them independently is what makes it
    /// possible to tell which one an artefact came from.</summary>
    public bool PostFxSsr { get; private set; } = true;

    // No PostFxVolumetricFog. Godot's volumetric fog is a second, flat-density fog model with
    // nothing region-aware behind it, and FEAT-RENDER-01 Phase 5 replaced distance haze entirely
    // with the per-fragment Windlight/EEP seam (slng_atmospherics.gdshaderinc). Leaving both
    // active is the "double-applying" the phase exists to rule out, so EnvironmentDriver.ApplyFog
    // forces it off every frame -- which made this setting write a value that was overwritten
    // ~16 ms later. Two visible symptoms, both from that: the Design tab's "Volumetric Fog"
    // checkbox toggled nothing, and F2's `anyOn` was permanently true because this defaulted to
    // true, so the first F2 press always turned post-FX OFF instead of on. Retired rather than
    // wired up: the setting is unreachable as long as EnvironmentDriver owns the fog, and it
    // should.

    public bool Shadows { get; private set; } = true;
    public float ShadowBlur { get; private set; } = 1.8f;
    public int ShadowResolution { get; private set; } = 4096;
    public float ShadowDistance { get; private set; } = 90.0f; // FEAT-PERF-06: was 150; see Boot's DirectionalShadowMaxDistance note
    public float ShadowOpacity { get; private set; } = 0.90f;
    public int ShadowSplits { get; private set; } = 2;
    public bool SmallObjectShadows { get; private set; } = false;
    public void Load()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(ConfigPath) != Error.Ok) return;

        VSyncMode = (int)cfg.GetValue(Section, "vsync_mode", VSyncMode);
        MaxFps = (int)cfg.GetValue(Section, "max_fps", MaxFps);
        DrawDistance = (float)cfg.GetValue(Section, "draw_distance", DrawDistance);
        Msaa = (int)cfg.GetValue(Section, "msaa", Msaa);
        TextureMemoryMb = (int)cfg.GetValue(Section, "texture_memory_mb", TextureMemoryMb);
        PostFxSsao = (bool)cfg.GetValue(Section, "post_fx_ssao", PostFxSsao);
        PostFxSsil = (bool)cfg.GetValue(Section, "post_fx_ssil", PostFxSsil);
        PostFxGlow = (bool)cfg.GetValue(Section, "post_fx_glow", PostFxGlow);
        PostFxReflectionProbe = (bool)cfg.GetValue(Section, "post_fx_reflection_probe", PostFxReflectionProbe);
        PostFxSsr = (bool)cfg.GetValue(Section, "post_fx_ssr", PostFxSsr);
        Shadows = (bool)cfg.GetValue(Section, "shadows", Shadows);
        ShadowBlur = (float)cfg.GetValue(Section, "shadow_blur", ShadowBlur);
        ShadowResolution = (int)cfg.GetValue(Section, "shadow_resolution", ShadowResolution);
        ShadowDistance = (float)cfg.GetValue(Section, "shadow_distance", ShadowDistance);
        ShadowOpacity = (float)cfg.GetValue(Section, "shadow_opacity", ShadowOpacity);
        ShadowSplits = (int)cfg.GetValue(Section, "shadow_splits", ShadowSplits);
        SmallObjectShadows = (bool)cfg.GetValue(Section, "small_object_shadows", SmallObjectShadows);
    }

    private void Save()
    {
        var cfg = new ConfigFile();
        cfg.Load(ConfigPath); // preserve sections owned by other features
        cfg.SetValue(Section, "vsync_mode", VSyncMode);
        cfg.SetValue(Section, "max_fps", MaxFps);
        cfg.SetValue(Section, "draw_distance", DrawDistance);
        cfg.SetValue(Section, "msaa", Msaa);
        cfg.SetValue(Section, "texture_memory_mb", TextureMemoryMb);
        cfg.SetValue(Section, "post_fx_ssao", PostFxSsao);
        cfg.SetValue(Section, "post_fx_ssil", PostFxSsil);
        cfg.SetValue(Section, "post_fx_glow", PostFxGlow);
        cfg.SetValue(Section, "post_fx_reflection_probe", PostFxReflectionProbe);
        cfg.SetValue(Section, "post_fx_ssr", PostFxSsr);
        cfg.SetValue(Section, "shadows", Shadows);
        cfg.SetValue(Section, "shadow_blur", ShadowBlur);
        cfg.SetValue(Section, "shadow_resolution", ShadowResolution);
        cfg.SetValue(Section, "shadow_distance", ShadowDistance);
        cfg.SetValue(Section, "shadow_opacity", ShadowOpacity);
        cfg.SetValue(Section, "shadow_splits", ShadowSplits);
        cfg.SetValue(Section, "small_object_shadows", SmallObjectShadows);
        cfg.Save(ConfigPath);
    }

    public void SetVSyncMode(int mode) { VSyncMode = mode; Save(); }
    public void SetMaxFps(int fps) { MaxFps = fps; Save(); }
    public void SetDrawDistance(float metres) { DrawDistance = metres; Save(); }
    public void SetMsaa(int msaa) { Msaa = msaa; Save(); }
    public void SetTextureMemoryMb(int mb) { TextureMemoryMb = mb; Save(); }
    public void SetPostFxSsao(bool on) { PostFxSsao = on; Save(); }
    public void SetPostFxSsil(bool on) { PostFxSsil = on; Save(); }
    public void SetPostFxGlow(bool on) { PostFxGlow = on; Save(); }
    public void SetPostFxReflectionProbe(bool on) { PostFxReflectionProbe = on; Save(); }
    public void SetPostFxSsr(bool on) { PostFxSsr = on; Save(); }
    public void SetShadows(bool on) { Shadows = on; Save(); }
    public void SetShadowBlur(float blur) { ShadowBlur = blur; Save(); }
    public void SetShadowResolution(int res) { ShadowResolution = res; Save(); }
    public void SetShadowDistance(float distance) { ShadowDistance = distance; Save(); }
    public void SetShadowOpacity(float opacity) { ShadowOpacity = opacity; Save(); }
    public void SetShadowSplits(int splits) { ShadowSplits = splits; Save(); }
    public void SetSmallObjectShadows(bool on) { SmallObjectShadows = on; Save(); }

    /// <summary>
    /// Pushes the current values into the engine. Safe to call repeatedly and with nulls -- during
    /// startup the environment and sun do not exist yet, and the window-level settings should still
    /// take effect.
    /// </summary>
    public void Apply(Viewport? viewport, WorldEnvironment? worldEnvironment, DirectionalLight3D? sun,
                       ReflectionProbe? reflectionProbe = null)
    {
        DisplayServer.WindowSetVsyncMode((DisplayServer.VSyncMode)VSyncMode);

        // Engine.MaxFps is the frame limiter Godot applies itself. With V-Sync on it is redundant
        // at best and fights the refresh rate at worst, so it is only meaningful as a way to cap
        // an unsynced frame rate -- for instance to stop a menu spinning the GPU at 900 fps.
        Engine.MaxFps = MaxFps;

        RenderConfig.DrawDistance = DrawDistance;
        RenderConfig.SmallObjectShadows = SmallObjectShadows;

        if (viewport != null) viewport.Msaa3D = (Viewport.Msaa)Msaa;

        RenderingServer.DirectionalShadowAtlasSetSize(ShadowResolution, true);

        if (worldEnvironment?.Environment is { } env)
        {
            env.SsaoEnabled = PostFxSsao;
            env.SsilEnabled = PostFxSsil;
            env.SsrEnabled = PostFxSsr;
            env.GlowEnabled = PostFxGlow;
        }

        // Visible, not QueueFree/re-create: toggling back on must resume from wherever Boot's own
        // cadence last left it rather than starting the whole node over. A hidden ReflectionProbe
        // contributes nothing to the pipeline (same as if it were never placed), which is exactly
        // the A/B this toggle exists for.
        if (reflectionProbe != null) reflectionProbe.Visible = PostFxReflectionProbe;

        if (sun != null)
        {
            sun.ShadowEnabled = Shadows;
            sun.ShadowBlur = ShadowBlur;
            sun.DirectionalShadowMaxDistance = ShadowDistance;
            sun.ShadowOpacity = ShadowOpacity;
            sun.DirectionalShadowMode = ShadowSplits switch {
                0 => DirectionalLight3D.ShadowMode.Orthogonal,
                1 => DirectionalLight3D.ShadowMode.Parallel2Splits,
                _ => DirectionalLight3D.ShadowMode.Parallel4Splits
            };
        }
    }
}
