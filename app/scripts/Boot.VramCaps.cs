using System;
using System.Collections.Generic;
using System.Text;
using Godot;
using SLNG.App;
using SLNG.Core;

/// <summary>
/// FEAT-PERF-25: graphics that fit a small card, and a look at where its video memory goes.
///
/// <para>In-world 2026-10-10 at Sirens with the budget limited to 3.5 GB, ~2.8 GB of the process's video
/// memory was not the texture cache, and <see cref="VramBudgetPolicy"/> left the cache 552 MB. This
/// part (1) picks a <see cref="LowVramPolicy"/> tier from the OS budget and lets
/// <see cref="SLNG.App.UI.GraphicsSettings.Apply"/> cap the expensive render buffers -- unless the user
/// turned that off -- and logs what was capped and why; (2) resizes the reflection atlas, which Godot
/// only lets a scenario do one way (see <see cref="UpdateReflectionAtlasCap"/>); (3) writes a one-shot
/// <c>[VramBreakdown]</c> line: Godot's measured totals, the texture cache's own share, and estimates
/// for every render buffer outside it.</para>
/// </summary>
public partial class Boot
{
    private int _vramTier = -1;
    private long _vramTierBudget;
    private string _loggedVramCaps = "";

    // The reflection atlas: resized once per session (it cannot grow back, see UpdateReflectionAtlasCap).
    private bool _reflectionAtlasShrunk;
    private int _reflectionAtlasShrinkFrames;

    private double _vramBreakdownDueAt = -1;
    private string _vramBreakdownReason = "";
    private int _vramBreakdownsLeft = 4;

    private static double VramNowSeconds => Time.GetTicksMsec() / 1000.0;

    /// <summary>Called with every smoothed OS budget reading (UpdateVramBudget). A tier change
    /// re-applies the graphics settings; the caps themselves live in GraphicsSettings.</summary>
    private void UpdateLowVramTier(long osBudget)
    {
        int tier = LowVramPolicy.TierFor(osBudget, _vramTier);
        if (tier == _vramTier) return;
        _vramTier = tier;
        _vramTierBudget = osBudget;
        _graphicsSettings.VramTier = tier;
        // Not ApplyGraphicsSettings: that re-runs UpdateVramBudget, which is where this was called from.
        _graphicsSettings.Apply(GetViewport(), _worldEnvironment, _sun, _reflectionProbe);
        ReportVramCaps();
    }

    /// <summary>One line whenever what the caps do changes -- a new tier, or the user toggling the caps
    /// or an option they cap. godot.log and the perf sidecar, ungated: a state change, not a reading.</summary>
    private void ReportVramCaps()
    {
        if (_vramTier < 0) return;
        var settings = _graphicsSettings;
        long budgetMb = _vramTierBudget >> 20;
        string line;
        if (_vramTier == LowVramPolicy.TierNone)
        {
            line = $"[VramCaps] OS video memory budget {budgetMb} MB: no caps";
        }
        else
        {
            long threshold = _vramTier == LowVramPolicy.TierTiny ? LowVramPolicy.TinyBudgetBytes : LowVramPolicy.SmallBudgetBytes;
            string why = $"OS video memory budget {budgetMb} MB < {threshold >> 20} MB ({LowVramPolicy.TierName(_vramTier)} card)";
            if (!settings.LowVramCaps)
                line = $"[VramCaps] {why}, but the automatic caps are switched off -- every graphics option applies as set";
            else if (settings.CapChanges.Count == 0)
                line = $"[VramCaps] {why}: nothing to cap, the options are already within the limits";
            else
                line = $"[VramCaps] {why}: {string.Join(", ", settings.CapChanges)} " +
                       "(switch off: Preferences > Graphics > Hardware > \"limit expensive effects on small graphics cards\")";
        }
        if (line == _loggedVramCaps) return;
        _loggedVramCaps = line;
        PerfSidecar.Write(line);
        GD.Print(line);
        ScheduleVramBreakdown("after the caps changed", 20);
    }

    /// <summary>Per frame: the reflection atlas resize and the breakdown timer.</summary>
    private void UpdateVramCapsFrame()
    {
        UpdateReflectionAtlasCap();

        if (_vramBreakdownDueAt >= 0 && VramNowSeconds >= _vramBreakdownDueAt)
        {
            _vramBreakdownDueAt = -1;
            if (_vramBreakdownsLeft > 0)
            {
                _vramBreakdownsLeft--;
                LogVramBreakdown(_vramBreakdownReason);
            }
        }
    }

    /// <summary>Asks for a <c>[VramBreakdown]</c> line <paramref name="delaySeconds"/> from now, at most
    /// four per session. A later request replaces a pending one.</summary>
    private void ScheduleVramBreakdown(string reason, double delaySeconds)
    {
        if (_vramBreakdownsLeft <= 0 || _gpuCache == null) return;
        _vramBreakdownReason = reason;
        _vramBreakdownDueAt = VramNowSeconds + delaySeconds;
    }

    /// <summary>
    /// The reflection atlas cap. Godot 4.7 binds no call to resize a scenario's reflection atlas
    /// (<c>scenario_set_reflection_atlas_size</c> exists in RendererSceneCull but is not exposed), and
    /// project.godot's 1024 is read once, when the scenario is created. What it does do, in
    /// <c>LightStorage::reflection_probe_instance_begin_render</c> (light_storage.cpp, 4.7-stable): an
    /// UPDATE_ALWAYS probe rendering into an atlas that is not 256 resizes that atlas to 256, and the
    /// atlas never grows back for the scenario's lifetime ("stuck on a lower resolution", by design).
    /// The hero probe already triggers that whenever it is used. On a small card the follow probe is
    /// switched to Always for a few frames to trigger it on purpose -- ~270 MB at 1024 x 4 slots, ~30 MB
    /// at 256 (see VramEstimate.ReflectionAtlas) -- and then goes back to its once-per-move capture.
    /// Godot prints a warning about the atlas size when it happens; the line above it says why.
    /// </summary>
    private void UpdateReflectionAtlasCap()
    {
        if (_reflectionProbe == null) return;
        if (_reflectionAtlasShrinkFrames > 0)
        {
            if (--_reflectionAtlasShrinkFrames == 0)
            {
                _reflectionProbe.UpdateMode = ReflectionProbe.UpdateModeEnum.Once;
                _reflectionAtlasShrunk = true;
            }
            return;
        }
        if (_reflectionAtlasShrunk || !_graphicsSettings.ReflectionAtlasCapped) return;
        // A hidden probe has not allocated the atlas, and a visible one is what will.
        if (!_reflectionProbe.Visible || !_reflectionProbe.IsInsideTree()) return;

        string line = $"[VramCaps] reflection atlas {ProjectSettings.GetSetting("rendering/reflections/reflection_atlas/reflection_size", 256)} -> " +
                      $"{LowVramPolicy.CappedReflectionSize} for this session (Godot warns about the atlas size next; that is this, on purpose)";
        PerfSidecar.Write(line);
        GD.Print(line);
        _reflectionProbe.UpdateMode = ReflectionProbe.UpdateModeEnum.Always;
        _reflectionAtlasShrinkFrames = 3;
    }

    /// <summary>
    /// The one-shot breakdown: what Godot measures, what the texture cache holds, and estimates for the
    /// render buffers outside it. The RenderingServer reads here are synchronous (they wait for the
    /// render thread), which is why this runs a few times per session and never per frame.
    /// </summary>
    private void LogVramBreakdown(string reason)
    {
        if (_gpuCache == null) return;
        long video = unchecked((long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.VideoMemUsed));
        long textures = unchecked((long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TextureMemUsed));
        long buffers = unchecked((long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.BufferMemUsed));
        long osBudget = 0, osUsage = 0;
        bool dxgi = _dxgiProbe != null && _dxgiProbe.TryQuery(out osBudget, out osUsage);
        var cache = _gpuCache.SizeBreakdown();
        var eff = _graphicsSettings.Effective;
        bool ssilHalf = (bool)ProjectSettings.GetSetting("rendering/environment/ssil/half_size", true);

        var sb = new StringBuilder();
        sb.Append($"[VramBreakdown] ({reason}) ");
        if (dxgi) sb.Append($"OS budget {osBudget >> 20} MB (smoothed {_vramTierBudget >> 20}), process usage {osUsage >> 20} MB | ");
        else if (_cmdlineVramBudgetMb.HasValue) sb.Append($"budget {_cmdlineVramBudgetMb.Value} MB (--vram-budget, no DXGI) | ");
        sb.Append($"Godot: video {video >> 20} MB, textures {textures >> 20} MB, buffers {buffers >> 20} MB | ");
        sb.Append($"texture cache {_gpuCache.CurrentSizeBytes >> 20} of {_gpuCache.MaxSizeBytes >> 20} MB: " +
                  $"{cache.TextureBytes >> 20} MB in {cache.Textures} textures (avatars {cache.AvatarBytes >> 20}, protected {cache.ProtectedBytes >> 20}), " +
                  $"{cache.MeshBytes >> 20} MB in {cache.Meshes} meshes | ");
        long outsideTextures = textures - cache.TextureBytes;
        long outsideBuffers = buffers - cache.MeshBytes;
        sb.Append($"outside the cache: textures {outsideTextures >> 20} MB, buffers {outsideBuffers >> 20} MB");
        if (dxgi) sb.Append($", driver/allocator {(osUsage - video) >> 20} MB");
        sb.Append(" | est. render buffers:");

        long estimated = 0;
        var mainSize = SLNG.App.UI.UiScale.RenderSize(GetViewport()) * GetViewport().Scaling3DScale;
        int mw = (int)mainSize.X, mh = (int)mainSize.Y;
        long main = VramEstimate.RenderBuffers(mw, mh, eff.Msaa, eff.Ssao, eff.SsaoHalfSize, eff.Ssil, ssilHalf,
                                               _graphicsSettings.PostFxSsr, _graphicsSettings.PostFxGlow);
        estimated += main;
        sb.Append($" main {mw}x{mh} MSAA {LowVramPolicy.MsaaName(eff.Msaa)} {main >> 20} MB (MSAA {VramEstimate.MsaaBuffers(mw, mh, eff.Msaa) >> 20})");

        foreach (Node node in GetTree().Root.FindChildren("*", "SubViewport", true, false))
        {
            if (node is not SubViewport sv || sv.Disable3D || sv.GetCamera3D() == null) continue;
            // A viewport with its own world has no WorldEnvironment, so no post effects.
            bool effects = !sv.OwnWorld3D;
            long est = VramEstimate.RenderBuffers(sv.Size.X, sv.Size.Y, (int)sv.Msaa3D,
                effects && eff.Ssao, eff.SsaoHalfSize, effects && eff.Ssil, ssilHalf,
                effects && _graphicsSettings.PostFxSsr, effects && _graphicsSettings.PostFxGlow);
            estimated += est;
            sb.Append($", {sv.Name} {sv.Size.X}x{sv.Size.Y} {sv.RenderTargetUpdateMode.ToString().ToLowerInvariant()} {est >> 20} MB");
        }

        long shadow = _graphicsSettings.Shadows ? VramEstimate.ShadowAtlas(eff.ShadowAtlasSize) : 0;
        estimated += shadow;
        sb.Append($", sun shadow atlas {eff.ShadowAtlasSize} {shadow >> 20} MB");

        int reflSize = _reflectionAtlasShrunk ? LowVramPolicy.CappedReflectionSize
            : (int)ProjectSettings.GetSetting("rendering/reflections/reflection_atlas/reflection_size", 256);
        int reflCount = (int)ProjectSettings.GetSetting("rendering/reflections/reflection_atlas/reflection_count", 64);
        bool reflUsed = (_reflectionProbe?.Visible ?? false) || (_heroProbe?.Visible ?? false) || _reflectionAtlasShrunk;
        long refl = reflUsed ? VramEstimate.ReflectionAtlas(reflSize, reflCount) : 0;
        estimated += refl;
        sb.Append($", reflection atlas {reflSize} x {reflCount} {refl >> 20} MB{(reflUsed ? "" : " (not allocated)")}");

        sb.Append($" | textures outside the cache not explained by these: {(outsideTextures - estimated) >> 20} MB");
        sb.Append($" | caps: tier {LowVramPolicy.TierName(Math.Max(0, _vramTier))}, {(_graphicsSettings.LowVramCaps ? "on" : "off by the user")}");

        string line = sb.ToString();
        PerfSidecar.Write(line);
        GD.Print(line);
    }
}
