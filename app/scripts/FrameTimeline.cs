using System.Diagnostics;
using System.Threading;
using Godot;

namespace SLNG.App;

/// <summary>
/// BUG-PERF-05: the main thread's frame cut into the stretches Godot runs it in, so every
/// millisecond of it has a name.
///
/// <para>Round one (<see cref="RenderTimes"/>) showed a CPU-bound frame: 20.4 ms against ~9.5 ms of
/// GPU. Round two bracketed every <c>_Process</c> callback with two nodes that run first and last
/// in the process order (<see cref="Node.ProcessPriority"/> int.MinValue / int.MaxValue): scripts
/// ~4.5 ms, which the <c>[PhaseCost]</c> marks account for almost entirely, and render CPU ~9 ms --
/// leaving ~6 ms that was neither. This round splits that rest along the order of
/// <c>Main::iteration</c> / <c>SceneTree::process</c>, using the engine's own signals as cuts:</para>
/// <code>
/// [prev frame_post_draw] -- other ------ physics step, input and OS events, frame pacing
/// [process_frame]        -- pre-flush -- deferred calls and transform notifications queued since
///                                         the last frame
/// [first _Process]       -- scripts ---- every node's _Process and internal process, ours and Godot's
/// [last _Process]        -- post-flush - deferred calls, CanvasItem redraws, skeleton and skin
///                                         updates (NOTIFICATION_UPDATE_SKELETON is deferred), transform
///                                         notifications, delete queue, timers/tweens, RenderingServer sync
/// [frame_pre_draw]       -- draw ------- the whole RenderingServer draw: scene update, reflection-probe
///                                         faces, every viewport, canvas, present
/// [frame_post_draw]
/// </code>
/// <para>Main thread only; the signals fire on it. Rendering on the main thread makes
/// <c>frame_post_draw</c> arrive inline, right after the draw.</para>
///
/// <para><b>FEAT-PERF-13, separate render thread.</b> <c>frame_pre_draw</c> is still emitted on the
/// main thread, by <c>RenderingServer::draw</c> after its <c>sync()</c>, but the draw itself is only
/// queued, and <c>frame_post_draw</c> is delivered later through the message queue, at whichever flush
/// the next frame reaches first. So "pre_draw to post_draw" is no longer a cost the main thread pays,
/// and the strict ordering check below would throw most frames away. In that mode a frame is closed
/// by its own <c>frame_pre_draw</c> instead, and the stretches mean:</para>
/// <list type="bullet">
/// <item><c>postFlushMs</c> includes the main thread's <b>wait for the render thread</b>
/// (<c>RenderingServer::sync()</c> is the last thing before pre_draw). It exceeds the main-thread
/// value by exactly that wait, so it grows when the render thread is the slower side.</item>
/// <item><c>drawMs</c> is 0: queueing the draw is not measurable here. The render thread's own cost is
/// <c>renderCpuMs</c> / <c>renderGpuMs</c> / <c>setupMs</c> in <see cref="RenderTimes"/>.</item>
/// <item><c>frameMs</c> is still the frame period (pre_draw to the next pre_draw), so fps and the
/// stretches stay comparable across the two modes.</item>
/// </list>
///
/// <para><b>FEAT-PERF-24 Phase 0 (P0-a): post-flush split in two.</b> The last script arms a
/// zero-length <see cref="SceneTreeTimer"/>. Godot fires it in the same frame's
/// <c>process_timers</c>, which comes after the message-queue flush (deferred calls, and with them
/// every skeleton's <c>NOTIFICATION_UPDATE_SKELETON</c>) and the transform notifications, and before
/// the delete queue (<c>scene_tree.cpp</c> <c>SceneTree::process</c>, 4.7-stable). So
/// <c>flushMs</c> = scripts end to timer is the main thread's own flush work, and <c>tailMs</c> =
/// timer to pre_draw is the delete queue plus, in separate mode, <c>RenderingServer::sync()</c>.
/// With <c>--diag</c> in separate mode, two callables also go through
/// <c>RenderingServer.CallOnRenderThread</c> (one at scripts end, one at the timer). The render
/// thread runs each once it has done everything queued before it, and both have run by pre_draw,
/// because the sync before it waits for exactly that. <c>rtBusyMs</c> is how long after scripts end
/// the render side was still busy, and <c>rtLagMs</c> is how far behind it still was when the
/// flush ended. When <c>tailMs</c> is about <c>rtLagMs</c>, the tail is waiting.</para>
/// </summary>
public partial class FrameTimeline : Node
{
    private static long _processFrame, _scriptsStart, _scriptsEnd, _preDraw, _lastPostDraw;
    private static double _preFlushSum, _scriptsSum, _postFlushSum, _drawSum, _frameSum;
    private static int _frames;
    private static bool _hooked;

    // FEAT-PERF-24 P0-a. _flushMark is 0 until this frame's timer fired; the render markers are
    // written on the render thread, hence Volatile.
    private static long _flushMark, _rtAfterScripts, _rtAfterFlush;
    private static bool _rtQueued;
    private static double _flushSum, _tailSum, _rtBusySum, _rtLagSum;
    private static int _splitFrames, _rtFrames, _frees;
    private static Callable _rtAfterScriptsMarker, _rtAfterFlushMarker, _onFlushed;
    private static bool _rtMarkersOn;
    private static SceneTree? _tree;

    private bool _isEnd;

    /// <summary>Averages per frame over the last <see cref="Settle"/> window. They add up to
    /// <see cref="FrameMs"/>.</summary>
    public static double OtherMs { get; private set; }
    public static double PreFlushMs { get; private set; }
    public static double ScriptsMs { get; private set; }
    public static double PostFlushMs { get; private set; }
    public static double DrawMs { get; private set; }
    public static double FrameMs { get; private set; }

    /// <summary>FEAT-PERF-24 P0-a: <see cref="PostFlushMs"/> split at the timer marker. They add up
    /// to it (over the frames both were measured in).</summary>
    public static double FlushMs { get; private set; }
    public static double TailMs { get; private set; }

    /// <summary>FEAT-PERF-24 P0-a, separate mode with <c>--diag</c> only, else -1: scripts end to the
    /// render thread reaching the marker queued then, and the same for the marker queued at the
    /// flush end.</summary>
    public static double RtBusyMs { get; private set; } = -1;
    public static double RtLagMs { get; private set; } = -1;

    /// <summary>Prims removed (<c>QueueFree</c>d) per frame: the delete-queue half of the tail.</summary>
    public static double FreesPerFrame { get; private set; }

    /// <summary>Counts one prim node handed to the delete queue (ObjectRenderer.RemoveVisual).</summary>
    public static void CountFree() => _frees++;

    /// <summary>Adds the two process markers under <paramref name="parent"/> (where they sit does
    /// not matter, only their priority) and hooks the frame signals once.</summary>
    public static void Install(Node parent)
    {
        parent.AddChild(new FrameTimeline { Name = "FrameTimelineStart", ProcessPriority = int.MinValue });
        parent.AddChild(new FrameTimeline { Name = "FrameTimelineEnd", ProcessPriority = int.MaxValue, _isEnd = true });
        if (_hooked) return;
        _hooked = true;
        _tree = parent.GetTree();
        _onFlushed = Callable.From(OnFlushed);
        _tree.ProcessFrame += () => _processFrame = Stopwatch.GetTimestamp();
        RenderingServer.FramePreDraw += OnPreDraw;
        // Separate render thread: post_draw arrives out of step with the frame (see the class
        // summary), and pre_draw closes the frame instead.
        if (!RenderThread.IsSeparate) RenderingServer.FramePostDraw += OnPostDraw;
        if (RenderThread.IsSeparate && Diagnostics.Enabled)
        {
            // Made once, here on the main thread; the render thread only invokes them. The body is
            // a timestamp write and nothing else (the same rule as RenderThread.Probe).
            _rtAfterScriptsMarker = Callable.From(() => Volatile.Write(ref _rtAfterScripts, Stopwatch.GetTimestamp()));
            _rtAfterFlushMarker = Callable.From(() => Volatile.Write(ref _rtAfterFlush, Stopwatch.GetTimestamp()));
            _rtMarkersOn = true;
        }
    }

    /// <summary>The timer's handler: the main thread's deferred flush and transform notifications
    /// for this frame are done.</summary>
    private static void OnFlushed()
    {
        _flushMark = Stopwatch.GetTimestamp();
        if (_rtQueued) RenderingServer.CallOnRenderThread(_rtAfterFlushMarker);
    }

    /// <summary>Adds this frame's split once its pre_draw (separate) or post_draw (main) closed it.
    /// A frame whose timer did not fire before pre_draw is left out of the split only.</summary>
    private static void AccumulateSplit(long preDraw)
    {
        long flushMark = _flushMark;
        if (flushMark == 0 || flushMark < _scriptsEnd || flushMark > preDraw) return;
        _flushSum += Ms(flushMark - _scriptsEnd);
        _tailSum += Ms(preDraw - flushMark);
        _splitFrames++;

        if (!_rtQueued) return;
        // RenderingServer::sync() ran just before pre_draw, so both markers have run by now.
        long afterScripts = Volatile.Read(ref _rtAfterScripts), afterFlush = Volatile.Read(ref _rtAfterFlush);
        if (afterScripts < _scriptsEnd || afterFlush < flushMark) return;
        _rtBusySum += Ms(afterScripts - _scriptsEnd);
        _rtLagSum += Ms(afterFlush - flushMark);
        _rtFrames++;
    }

    private static void OnPreDraw()
    {
        long now = Stopwatch.GetTimestamp();
        _preDraw = now;
        if (!RenderThread.IsSeparate) return;

        long previous = _lastPostDraw; // in this mode: the previous frame's pre_draw
        _lastPostDraw = now;
        if (previous == 0 || !(previous < _processFrame && _processFrame <= _scriptsStart
                               && _scriptsStart <= _scriptsEnd && _scriptsEnd <= now))
        {
            return;
        }

        _preFlushSum += Ms(_scriptsStart - _processFrame);
        _scriptsSum += Ms(_scriptsEnd - _scriptsStart);
        _postFlushSum += Ms(now - _scriptsEnd);
        _frameSum += Ms(now - previous);
        _frames++;
        AccumulateSplit(now);
    }

    public override void _Process(double delta)
    {
        if (!_isEnd)
        {
            _scriptsStart = Stopwatch.GetTimestamp();
            return;
        }

        _flushMark = 0;
        _rtQueued = _rtMarkersOn;
        // Armed before the timestamp, so arming it is scripts time, not flush time.
        _tree?.CreateTimer(0.0, processAlways: true).Connect(SceneTreeTimer.SignalName.Timeout, _onFlushed);
        _scriptsEnd = Stopwatch.GetTimestamp();
        if (_rtQueued) RenderingServer.CallOnRenderThread(_rtAfterScriptsMarker);
    }

    private static void OnPostDraw()
    {
        long now = Stopwatch.GetTimestamp();
        long previous = _lastPostDraw;
        _lastPostDraw = now;

        // Only a frame whose every cut happened, in order, since the previous one is counted --
        // the first frame, and any frame drawn without a process step, would otherwise mix stretches
        // from two different frames.
        if (previous == 0 || !(previous < _processFrame && _processFrame <= _scriptsStart
                               && _scriptsStart <= _scriptsEnd && _scriptsEnd <= _preDraw && _preDraw <= now))
        {
            return;
        }

        _preFlushSum += Ms(_scriptsStart - _processFrame);
        _scriptsSum += Ms(_scriptsEnd - _scriptsStart);
        _postFlushSum += Ms(_preDraw - _scriptsEnd);
        _drawSum += Ms(now - _preDraw);
        _frameSum += Ms(now - previous);
        _frames++;
        AccumulateSplit(_preDraw);
    }

    public static void Settle()
    {
        if (_frames == 0) return;
        PreFlushMs = _preFlushSum / _frames;
        ScriptsMs = _scriptsSum / _frames;
        PostFlushMs = _postFlushSum / _frames;
        DrawMs = _drawSum / _frames;
        FrameMs = _frameSum / _frames;
        OtherMs = System.Math.Max(0, FrameMs - PreFlushMs - ScriptsMs - PostFlushMs - DrawMs);
        FreesPerFrame = (double)_frees / _frames;
        _preFlushSum = _scriptsSum = _postFlushSum = _drawSum = _frameSum = 0;
        _frames = _frees = 0;

        if (_splitFrames > 0)
        {
            FlushMs = _flushSum / _splitFrames;
            TailMs = _tailSum / _splitFrames;
        }
        RtBusyMs = _rtFrames > 0 ? _rtBusySum / _rtFrames : -1;
        RtLagMs = _rtFrames > 0 ? _rtLagSum / _rtFrames : -1;
        _flushSum = _tailSum = _rtBusySum = _rtLagSum = 0;
        _splitFrames = _rtFrames = 0;
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
