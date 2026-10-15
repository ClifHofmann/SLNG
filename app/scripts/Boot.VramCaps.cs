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

    /// <summary>A pending <c>[VramBreakdown]</c>: when it is due, why, and how many of this kind a session may
    /// still write. Each kind has its own count, so a session that teleports a lot does not use up the few
    /// the caps and the budget fall are allowed.</summary>
    private sealed class BreakdownSlot
    {
        public double DueAt = -1;
        public string Reason = "";
        public int Left;
    }

    private readonly BreakdownSlot _capsBreakdown = new() { Left = 4 };

    // BUG-PERF-18: one breakdown after each region change.
    private readonly BreakdownSlot _regionBreakdown = new() { Left = 8 };

    // BUG-PERF-19: one breakdown after a graphics change that rebuilds render buffers (a preset switch).
    private readonly BreakdownSlot _graphicsBreakdown = new() { Left = 8 };
    private string _graphicsBufferSignature = "";

    /// <summary>What the last breakdown (or the moment a graphics change was applied) saw, so the next line can say
    /// what changed: how much of what Godot freed the process actually gave back.</summary>
    private readonly struct VramMark
    {
        public readonly string Label;
        public readonly double At;
        public readonly long Godot;
        public readonly long Process;
        public readonly long Adapter;

        public VramMark(string label, double at, long godot, long process, long adapter)
        {
            Label = label; At = at; Godot = godot; Process = process; Adapter = adapter;
        }
    }

    private VramMark? _vramMark;

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
        // The caps may have changed the buffers; what the next graphics change is compared with must say so.
        _graphicsBufferSignature = _graphicsSettings.AppliedBufferSignature();
        _renderBufferSwitch.SetApplied(_graphicsBufferSignature);
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

        if (_renderBufferSwitch.OnFrame())
            FinishGraphicsApply();

        FireBreakdown(_capsBreakdown);
        FireBreakdown(_regionBreakdown);
        FireBreakdown(_graphicsBreakdown);
    }

    private void FireBreakdown(BreakdownSlot slot)
    {
        if (slot.DueAt < 0 || VramNowSeconds < slot.DueAt) return;
        slot.DueAt = -1;
        if (slot.Left <= 0) return;
        slot.Left--;
        LogVramBreakdown(slot.Reason);
    }

    /// <summary>BUG-PERF-18: a <c>[VramBreakdown]</c> line a while after the world moved to another region
    /// (a teleport, a login is covered by the 60 s one): by then the region we left has had time to be
    /// released, so the line shows what is still held -- the cache's pinned bytes, the scene nodes, and
    /// what the driver keeps that Godot no longer counts. A newer region change replaces a pending one.</summary>
    private void ScheduleRegionChangeVramBreakdown(ulong regionHandle)
    {
        if (_regionBreakdown.Left <= 0 || _gpuCache == null) return;
        _regionBreakdown.Reason = $"20 s after entering region {regionHandle}";
        _regionBreakdown.DueAt = VramNowSeconds + 20;
    }

    /// <summary>Asks for a <c>[VramBreakdown]</c> line <paramref name="delaySeconds"/> from now, at most
    /// four per session. A later request replaces a pending one.</summary>
    private void ScheduleVramBreakdown(string reason, double delaySeconds)
    {
        if (_capsBreakdown.Left <= 0 || _gpuCache == null) return;
        _capsBreakdown.Reason = reason;
        _capsBreakdown.DueAt = VramNowSeconds + delaySeconds;
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
        if (dxgi)
        {
            sb.Append($"OS budget {osBudget >> 20} MB (smoothed {_vramTierBudget >> 20}), process usage {osUsage >> 20} MB");
            if (_lastAdapterUsage > 0 && _dxgiProbe != null)
                sb.Append($", whole card {_lastAdapterUsage >> 20} of {_dxgiProbe.DedicatedBytes >> 20} MB (others {Math.Max(0L, _lastAdapterUsage - osUsage) >> 20} MB)");
            sb.Append(" | ");
        }
        else if (_cmdlineVramBudgetMb.HasValue) sb.Append($"budget {_cmdlineVramBudgetMb.Value} MB (--vram-budget, no DXGI) | ");
        sb.Append($"Godot: video {video >> 20} MB, textures {textures >> 20} MB, buffers {buffers >> 20} MB | ");
        if (_vramMark is { } mark && dxgi)
        {
            // BUG-PERF-19: what Godot freed (or took) against what the process actually gave back (or took).
            long dGodot = video - mark.Godot, dProcess = osUsage - mark.Process;
            sb.Append($"since {mark.Label} {VramNowSeconds - mark.At:0} s ago: Godot {dGodot >> 20:+0;-0;0} MB, process {dProcess >> 20:+0;-0;0} MB");
            if (dGodot < 0) sb.Append($" (kept by the process: {Math.Max(0L, dProcess - dGodot) >> 20} MB of the {-dGodot >> 20} MB Godot freed)");
            sb.Append(" | ");
        }
        var refs = _gpuCache.ReferenceBreakdown();
        var compressed = _gpuCache.CompressedBreakdown();   // FEAT-PERF-17
        sb.Append($"texture cache {_gpuCache.CurrentSizeBytes >> 20} of {_gpuCache.MaxSizeBytes >> 20} MB " +
                  $"(pinned {refs.PinnedBytes >> 20} MB in {refs.Pinned}, unreferenced {refs.UnreferencedBytes >> 20} MB in {refs.Unreferenced} of which {refs.Fresh} fresh; " +
                  $"{(long)Performance.GetMonitor(Performance.Monitor.ObjectNodeCount)} scene nodes): " +
                  $"{cache.TextureBytes >> 20} MB in {cache.Textures} textures (avatars {cache.AvatarBytes >> 20}, protected {cache.ProtectedBytes >> 20}, " +
                  $"block-compressed {compressed.Bytes >> 20} MB in {compressed.Textures}), " +
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
        _vramMark = new VramMark("the previous breakdown", VramNowSeconds, video, dxgi ? osUsage : 0, _lastAdapterUsage);
    }

    private VramMark TakeVramMark(string label)
    {
        long process = 0;
        if (_dxgiProbe != null && _dxgiProbe.TryQuery(out _, out long usage)) process = usage;
        return new VramMark(label, VramNowSeconds, GodotVideoMemBytes(), process, _lastAdapterUsage);
    }

    /// <summary>BUG-PERF-19: wraps one <c>GraphicsSettings.Apply</c>. When it changes what the render buffers are
    /// made of (a preset switch), notes what Godot and the process held just before and asks for a
    /// <c>[VramBreakdown]</c> 10 s later, which prints how much of what Godot freed the process gave back.</summary>
    private void NoteGraphicsApply(Action apply)
    {
        var before = _bufferSwitchMark ?? TakeVramMark("the graphics change");
        _bufferSwitchMark = null;
        string signatureBefore = _graphicsBufferSignature;
        apply();
        string signature = _graphicsSettings.AppliedBufferSignature();
        if (signature == signatureBefore) return;
        _graphicsBufferSignature = signature;
        if (signatureBefore.Length == 0 || _gpuCache == null || _graphicsBreakdown.Left <= 0) return; // boot-time apply, not a switch
        _vramMark = before;
        _graphicsBreakdown.Reason = $"10 s after the render buffers changed ({signatureBefore} -> {signature})";
        _graphicsBreakdown.DueAt = VramNowSeconds + 10;
    }

    private VramMark? _bufferSwitchMark;

    /// <summary>The first half of a render-buffer switch: remember what the process holds, put the buffer-free state
    /// in the engine, and let <see cref="UpdateVramCapsFrame"/> finish the apply once the old buffers are gone.</summary>
    private void BeginRenderBufferSwitch()
    {
        _bufferSwitchMark = TakeVramMark("the graphics change");
        string line = $"[VramBuffers] releasing the render buffers ({_graphicsBufferSignature}) for {RenderBufferSwitch.ReleaseFrames} frames before " +
                      $"switching to ({_graphicsSettings.PredictedBufferSignature()}): switched directly, the process keeps what the old set held " +
                      "(measured +256..512 MB per distinct switch at 1440p), so the old set is handed back first (--no-buffer-release switches directly)";
        PerfSidecar.Write(line);
        GD.Print(line);
        _graphicsSettings.ApplyBufferFreeState(GetViewport(), _worldEnvironment);
    }
}
