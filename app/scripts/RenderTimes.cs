using System.Collections.Generic;
using System.Text;
using Godot;

namespace SLNG.App;

/// <summary>
/// BUG-PERF-05: Godot's own render cost, CPU and GPU, per viewport -- for the performance overlay
/// and the <c>[Perf]</c> line.
///
/// Everything measured before this was either our code (process/physics ms, <c>[PhaseCost]</c>) or
/// a count (draws, tris). Since BUG-PERF-01 our code has been a small slice of the frame, so the
/// rest of it was one unexplained number, and "is it the CPU submitting draws or the GPU shading
/// pixels" could only be guessed at by toggling settings and watching the frame rate. Reported
/// 2026-10-04 at 55 fps: shadows off, post-FX off and V-Sync off each left it short of 60, which
/// is exactly the result guessing cannot read. Godot measures the split per viewport; it only has
/// to be asked (<see cref="RenderingServer.ViewportSetMeasureRenderTime"/>).
///
/// Per viewport, because a frame here is not one render: the HUD viewport (AvatarRenderer, window
/// sized, its own World3D) and the planar mirror (<see cref="MirrorReflection"/>, the whole scene a
/// second time) each run their own pass, and a toggle aimed at the main view need not touch them.
///
/// Main thread only, like everything else the overlay reads -- except in the separate-render-thread
/// mode (FEAT-PERF-13), where the per-viewport getters are round trips into the render thread and
/// are read by <see cref="RenderTimeSampler"/> instead (see <see cref="Sample"/>).
/// </summary>
public static class RenderTimes
{
    internal sealed class Entry
    {
        public string Name = "";
        public Viewport? Viewport;
        public double CpuSum, GpuSum;
        public bool DrawnSinceSettle;

        /// <summary>Per FRAME of the last window, not per frame the viewport drew: a mirror that
        /// rendered in half the frames shows half its cost, so the entries add up to the frame.</summary>
        public double CpuMs, GpuMs;
        public bool Drawn;
    }

    private static readonly List<Entry> _entries = new();
    private static double _setupSum;
    private static int _frames;

    // FEAT-PERF-13, separate render thread only. The sampler thread adds into the entries' sums
    // under _gate; Settle reads and clears them under it. _requestsDone is the divisor there: it
    // counts the frames the sampler actually read, which is fewer than _frames when it was behind.
    private static readonly object _gate = new();
    private static RenderTimeSampler? _sampler;
    private static int _requestsDone;
    private static volatile bool _wantInfo;

    /// <summary>Godot's scene update before any viewport draws (dirty instances, transforms,
    /// AABBs), averaged over the last window. Shared by all viewports.</summary>
    public static double SetupCpuMs { get; private set; }

    /// <summary>Sum over every tracked viewport, averaged over the last window.</summary>
    public static double CpuMs { get; private set; }

    public static double GpuMs { get; private set; }

    /// <summary>BUG-PERF-05 round four: the main view's draw calls split by pass, as of the last
    /// window's end. Round three put ~13 ms of a 20 ms frame into draw submission on the CPU
    /// (viewport render plus the ~4.6 ms of the draw that no viewport measures), so the next
    /// question is which pass the ~11k draws belong to: the scene (depth prepass and colour pass
    /// both count here) or the shadow cascades -- the two have different levers.</summary>
    public static int MainVisibleDraws { get; private set; }
    public static int MainShadowDraws { get; private set; }
    public static int MainCanvasDraws { get; private set; }

    /// <summary>Starts measuring <paramref name="viewport"/> under <paramref name="name"/>. A second
    /// call with the same name replaces the viewport, so a recreated HUD viewport is not counted
    /// twice.</summary>
    public static void Track(string name, Viewport viewport)
    {
        RenderingServer.ViewportSetMeasureRenderTime(viewport.GetViewportRid(), true);
        foreach (var e in _entries)
        {
            if (e.Name != name) continue;
            e.Viewport = viewport;
            return;
        }
        _entries.Add(new Entry { Name = name, Viewport = viewport });
    }

    /// <summary>Once per frame. Reads what Godot measured for the previous frame's render.</summary>
    /// <remarks>FEAT-PERF-13: with rendering on its own thread
    /// <c>viewport_get_measured_render_time_cpu/gpu</c> are round trips that wait out the frame being
    /// drawn, so they are read on <see cref="RenderTimeSampler"/>'s thread and the main thread only
    /// posts the request. <c>get_frame_setup_time_cpu</c> is a plain member read in the engine, never
    /// a round trip, and stays here.</remarks>
    public static void Sample()
    {
        _frames++;
        _setupSum += RenderingServer.GetFrameSetupTimeCpu();
        if (RenderThread.IsSeparate)
        {
            SampleOffThread();
            return;
        }

        foreach (var e in _entries)
        {
            // A viewport that did not draw keeps reporting its LAST measurement, so a mirror that
            // has gone idle would otherwise be billed for every frame after it.
            if (!IsRendering(e.Viewport)) continue;
            var rid = e.Viewport!.GetViewportRid();
            e.CpuSum += RenderingServer.ViewportGetMeasuredRenderTimeCpu(rid);
            e.GpuSum += RenderingServer.ViewportGetMeasuredRenderTimeGpu(rid);
            e.DrawnSinceSettle = true;
        }
    }

    private static void SampleOffThread()
    {
        var entries = _entries.ToArray();
        var rids = new Rid[entries.Length];
        var active = new bool[entries.Length];
        Rid infoRid = default;
        for (int i = 0; i < entries.Length; i++)
        {
            var e = entries[i];
            if (e.Name == "main" && e.Viewport != null && GodotObject.IsInstanceValid(e.Viewport))
                infoRid = e.Viewport.GetViewportRid();
            if (!IsRendering(e.Viewport)) continue;
            rids[i] = e.Viewport!.GetViewportRid();
            active[i] = true;
            e.DrawnSinceSettle = true;
        }

        bool wantInfo = _wantInfo && infoRid.IsValid;
        if (wantInfo) _wantInfo = false;
        _sampler ??= new RenderTimeSampler();
        _sampler.Post(() => ReadOnSamplerThread(entries, rids, active, infoRid, wantInfo));
    }

    /// <summary>The round trips, on the sampler thread. Also the unit the selftest drives.</summary>
    internal static void ReadOnSamplerThread(Entry[] entries, Rid[] rids, bool[] active, Rid infoRid, bool wantInfo)
    {
        var cpu = new double[entries.Length];
        var gpu = new double[entries.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            if (!active[i]) continue;
            cpu[i] = RenderingServer.ViewportGetMeasuredRenderTimeCpu(rids[i]);
            gpu[i] = RenderingServer.ViewportGetMeasuredRenderTimeGpu(rids[i]);
        }

        int visible = 0, shadow = 0, canvas = 0;
        if (wantInfo)
        {
            visible = DrawCalls(infoRid, RenderingServer.ViewportRenderInfoType.Visible);
            shadow = DrawCalls(infoRid, RenderingServer.ViewportRenderInfoType.Shadow);
            canvas = DrawCalls(infoRid, RenderingServer.ViewportRenderInfoType.Canvas);
        }

        lock (_gate)
        {
            for (int i = 0; i < entries.Length; i++)
            {
                entries[i].CpuSum += cpu[i];
                entries[i].GpuSum += gpu[i];
            }
            _requestsDone++;
            if (wantInfo)
            {
                MainVisibleDraws = visible;
                MainShadowDraws = shadow;
                MainCanvasDraws = canvas;
            }
        }
    }

    /// <summary>Stops the sampler thread. Called before the engine tears the RenderingServer down.</summary>
    public static void Shutdown()
    {
        _sampler?.Dispose();
        _sampler = null;
    }

    /// <summary>Closes the window: turns the sums since the last call into per-frame averages.</summary>
    public static void Settle()
    {
        if (_frames == 0) return;
        bool separate = RenderThread.IsSeparate;
        int divisor = _frames;
        if (separate)
        {
            lock (_gate) divisor = _requestsDone;
            // Nothing read yet (the first window, or a sampler that is stuck behind the render
            // thread): keep the previous numbers rather than publish zeros.
            if (divisor == 0)
            {
                _setupSum = 0;
                _frames = 0;
                _wantInfo = true;
                return;
            }
        }

        double cpu = 0, gpu = 0;
        foreach (var e in _entries)
        {
            double cpuSum, gpuSum;
            lock (_gate)
            {
                cpuSum = e.CpuSum;
                gpuSum = e.GpuSum;
                e.CpuSum = e.GpuSum = 0;
            }
            e.CpuMs = cpuSum / divisor;
            e.GpuMs = gpuSum / divisor;
            e.Drawn = e.DrawnSinceSettle;
            e.DrawnSinceSettle = false;
            cpu += e.CpuMs;
            gpu += e.GpuMs;
        }
        CpuMs = cpu;
        GpuMs = gpu;
        SetupCpuMs = _setupSum / _frames;
        if (separate)
        {
            // ViewportGetRenderInfo is a round trip in this mode; ask the sampler thread for it with
            // the next sample. The counts are one window old, which is what a per-window figure is.
            lock (_gate) _requestsDone = 0;
            _wantInfo = true;
        }
        else
        {
            foreach (var e in _entries)
            {
                if (e.Name != "main" || e.Viewport == null || !GodotObject.IsInstanceValid(e.Viewport)) continue;
                var rid = e.Viewport.GetViewportRid();
                MainVisibleDraws = DrawCalls(rid, RenderingServer.ViewportRenderInfoType.Visible);
                MainShadowDraws = DrawCalls(rid, RenderingServer.ViewportRenderInfoType.Shadow);
                MainCanvasDraws = DrawCalls(rid, RenderingServer.ViewportRenderInfoType.Canvas);
            }
        }
        _setupSum = 0;
        _frames = 0;
    }

    /// <summary>The viewports that drew in the last window as name, then cpu/gpu ms: for the panel
    /// <c>main 6.9/10.1 · hud 0.5/1.1</c>, for the log line (<paramref name="compact"/>, no spaces,
    /// so it stays one grep-able column) <c>main:6.9/10.1,hud:0.5/1.1</c>.</summary>
    public static string Describe(bool compact)
    {
        string separator = compact ? "," : "  ·  ";
        char nameEnd = compact ? ':' : ' ';
        var sb = new StringBuilder();
        foreach (var e in _entries)
        {
            if (!e.Drawn) continue;
            if (sb.Length > 0) sb.Append(separator);
            sb.Append(e.Name).Append(nameEnd)
              .Append(e.CpuMs.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)).Append('/')
              .Append(e.GpuMs.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.Length > 0 ? sb.ToString() : "-";
    }

    private static int DrawCalls(Rid viewport, RenderingServer.ViewportRenderInfoType pass) =>
        RenderingServer.ViewportGetRenderInfo(viewport, pass, RenderingServer.ViewportRenderInfo.DrawCallsInFrame);

    private static bool IsRendering(Viewport? viewport)
    {
        if (viewport == null || !GodotObject.IsInstanceValid(viewport) || !viewport.IsInsideTree()) return false;
        return viewport is not SubViewport sub || sub.RenderTargetUpdateMode != SubViewport.UpdateMode.Disabled;
    }
}
