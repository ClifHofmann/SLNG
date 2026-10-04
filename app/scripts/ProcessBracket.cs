using System.Diagnostics;
using Godot;

namespace SLNG.App;

/// <summary>
/// BUG-PERF-05, second round: how long ALL <c>_Process</c> callbacks of a frame take together,
/// measured by two nodes that run first and last in the frame's process order.
///
/// The first <c>[Perf]</c> lines with <see cref="RenderTimes"/> (2026-10-04, 49 fps, V-Sync off)
/// split a 20.4 ms frame into GPU ~9.5 ms (so not the limit), Godot render CPU + setup ~9.2 ms, and
/// our marked phases ~2.6 ms -- leaving ~8 ms of main-thread time that nothing attributed. The
/// overlay's "process" figure could not answer it: Godot's <c>TIME_PROCESS</c> is the whole process
/// step INCLUDING rendering, and a recent worst case rather than an average (it read 39.8 ms
/// against a 20.2 ms mean). This brackets exactly the script part: every node's _Process and
/// internal process (AnimationMixer, particles, Godot's own nodes) runs between the two, sorted by
/// <see cref="Node.ProcessPriority"/>. What is left after scripts and rendering is Godot's own
/// per-frame work outside the process list -- deferred calls, CanvasItem redraws, transform
/// notifications, input and physics.
///
/// Main thread only.
/// </summary>
public partial class ProcessBracket : Node
{
    private static long _startTicks;
    private static double _scriptsSum, _frameSum;
    private static int _frames;

    private bool _isEnd;

    /// <summary>All _Process work of a frame, averaged over the last <see cref="Settle"/> window.</summary>
    public static double ScriptsMs { get; private set; }

    /// <summary>Wall time per frame over the same window, so the split adds up to it.</summary>
    public static double FrameMs { get; private set; }

    /// <summary>Adds the start and end markers under <paramref name="parent"/>. Where they sit in
    /// the tree does not matter; only their process priority does.</summary>
    public static void Install(Node parent)
    {
        parent.AddChild(new ProcessBracket { Name = "ProcessBracketStart", ProcessPriority = int.MinValue });
        parent.AddChild(new ProcessBracket { Name = "ProcessBracketEnd", ProcessPriority = int.MaxValue, _isEnd = true });
    }

    public override void _Process(double delta)
    {
        if (!_isEnd)
        {
            _startTicks = Stopwatch.GetTimestamp();
            _frameSum += delta * 1000.0;
            return;
        }
        if (_startTicks == 0) return;
        _scriptsSum += (Stopwatch.GetTimestamp() - _startTicks) * 1000.0 / Stopwatch.Frequency;
        _frames++;
    }

    public static void Settle()
    {
        if (_frames == 0) return;
        ScriptsMs = _scriptsSum / _frames;
        FrameMs = _frameSum / _frames;
        _scriptsSum = _frameSum = 0;
        _frames = 0;
    }
}
