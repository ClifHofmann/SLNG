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
/// Main thread only, like everything else the overlay reads.
/// </summary>
public static class RenderTimes
{
    private sealed class Entry
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

    /// <summary>Godot's scene update before any viewport draws (dirty instances, transforms,
    /// AABBs), averaged over the last window. Shared by all viewports.</summary>
    public static double SetupCpuMs { get; private set; }

    /// <summary>Sum over every tracked viewport, averaged over the last window.</summary>
    public static double CpuMs { get; private set; }

    public static double GpuMs { get; private set; }

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
    public static void Sample()
    {
        _frames++;
        _setupSum += RenderingServer.GetFrameSetupTimeCpu();
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

    /// <summary>Closes the window: turns the sums since the last call into per-frame averages.</summary>
    public static void Settle()
    {
        if (_frames == 0) return;
        double cpu = 0, gpu = 0;
        foreach (var e in _entries)
        {
            e.CpuMs = e.CpuSum / _frames;
            e.GpuMs = e.GpuSum / _frames;
            e.Drawn = e.DrawnSinceSettle;
            e.CpuSum = e.GpuSum = 0;
            e.DrawnSinceSettle = false;
            cpu += e.CpuMs;
            gpu += e.GpuMs;
        }
        CpuMs = cpu;
        GpuMs = gpu;
        SetupCpuMs = _setupSum / _frames;
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

    private static bool IsRendering(Viewport? viewport)
    {
        if (viewport == null || !GodotObject.IsInstanceValid(viewport) || !viewport.IsInsideTree()) return false;
        return viewport is not SubViewport sub || sub.RenderTargetUpdateMode != SubViewport.UpdateMode.Disabled;
    }
}
