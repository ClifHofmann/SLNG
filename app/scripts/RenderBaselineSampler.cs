using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace SLNG.App;

/// <summary>
/// Samples Godot's own render performance monitors over a fixed window and prints one
/// summary line. Exists for FEAT-RENDER-01: its Phase 1 acceptance criterion is "frame time
/// on a busy scene is no worse than the StandardMaterial3D baseline", which only means
/// anything if the before-figure is captured with the SAME method as the after-figure.
///
/// Reports the MEDIAN and 95th-percentile frame time rather than an average FPS: a mean FPS
/// hides exactly the thing that matters here, an occasional long frame. Draw calls, rendered
/// primitives and video memory come along because a shader migration can regress those
/// without touching frame time at all (e.g. by fragmenting material batches), and they are
/// far less noisy than timing, so a change in them is easier to trust than a small timing
/// delta.
///
/// Deliberately manual and stationary: the caller stands still, triggers it, and holds the
/// camera fixed for the sampling window. Comparing two runs is only valid for the same
/// place and the same view, so an automatic/continuous readout would invite exactly the
/// apples-to-oranges comparison this is meant to prevent.
/// </summary>
public partial class RenderBaselineSampler : Node
{
    private const double SampleSeconds = 10.0;

    private bool _sampling;
    private double _elapsed;
    private readonly List<double> _frameTimesMs = new();
    private string _label = "";
    private DisplayServer.VSyncMode _restoreVSync;

    /// <summary>Starts a sampling window. A second call while one is running is ignored, so a
    /// double-click on the menu entry can't corrupt the sample.</summary>
    public void StartSample(string label)
    {
        if (_sampling) return;

        _sampling = true;
        _elapsed = 0;
        _label = label;
        _frameTimesMs.Clear();

        // V-Sync MUST be off while sampling, or the numbers are worthless as a regression
        // detector: with it on, frame times snap to whole display-refresh intervals, so a
        // change can be large in either direction and still report the identical figure. The
        // very first baseline taken on this project did exactly that -- median 31.25 ms on a
        // ~64 Hz display, i.e. precisely two refresh periods, with 320 frames in 10 s. Restored
        // to whatever it was as soon as the window closes, so this doesn't quietly change how
        // the client runs the rest of the time.
        _restoreVSync = DisplayServer.WindowGetVsyncMode();
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);

        GD.Print($"[RenderBaseline] sampling '{label}' for {SampleSeconds:0}s " +
                  $"(vsync off for the window, was {_restoreVSync}) -- hold the camera still");
    }

    public override void _Process(double delta)
    {
        if (!_sampling) return;

        _elapsed += delta;
        _frameTimesMs.Add(delta * 1000.0);

        if (_elapsed < SampleSeconds) return;
        _sampling = false;
        DisplayServer.WindowSetVsyncMode(_restoreVSync);

        if (_frameTimesMs.Count == 0)
        {
            GD.Print("[RenderBaseline] no frames sampled");
            return;
        }

        var sorted = _frameTimesMs.OrderBy(t => t).ToList();
        double median = sorted[sorted.Count / 2];
        double p95 = sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.95))];
        double worst = sorted[^1];

        // Rendering monitors are per-frame counters; read them at the end of the window rather
        // than accumulating, since the view is stationary and they barely move.
        double drawCalls = Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);
        double primitives = Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame);
        double objects = Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame);
        double videoMemMb = Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / (1024.0 * 1024.0);

        GD.Print($"[RenderBaseline] {_label} | frames={sorted.Count} " +
                  $"medianMs={median:F2} p95Ms={p95:F2} worstMs={worst:F2} " +
                  $"drawCalls={drawCalls:F0} primitives={primitives:F0} objects={objects:F0} " +
                  $"videoMemMB={videoMemMb:F1}");
    }
}
