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
///
/// <para><b>FEAT-PERF-24: the render thread's own frame.</b> Once the tail is an honest wait, the
/// question is what the render thread does with its frame, and the viewport timers cover only part of
/// it. Two more markers bracket the queued draw: one queued in <c>frame_pre_draw</c>, which
/// <c>RenderingServerDefault::draw</c> emits right before it queues <c>_draw</c>
/// (<c>rendering_server_default.cpp:443-449</c>, 4.7-stable), and one queued from the main thread's
/// <c>ScriptServer::frame()</c> hook, the first managed code after <c>draw()</c> returns
/// (<c>main.cpp:5085-5105</c>; C# runs posts to <see cref="Dispatcher.SynchronizationContext"/>
/// there). Almost nothing else is queued between them (a later pre_draw handler's setter, or what a
/// continuation posted earlier queues; a frame whose end marker went out more than 1 ms late is left
/// out), so their gap is <c>_draw</c>: <c>drawCallMs</c>.
/// The rest of the render thread's cycle, up to the marker at the flush end, executes the commands
/// the main thread queued meanwhile: <c>commandMs</c> = <c>cmdScripts</c> (physics, input and our
/// scripts) + <c>cmdFlush</c> (deferred calls, skeleton writes). The render thread can only have been
/// idle before the main thread queued the next marker; that time, and the tail after the flush-end
/// marker, are <c>rtIdleMaxMs</c>, an upper bound. drawCall + command + idleMax is the render
/// thread's cycle, about <c>frameMs</c>. <see cref="RenderSections"/> splits <c>drawCallMs</c>
/// further.</para>
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

    // FEAT-PERF-24: the draw bracket. The two stamps are written on the render thread; the two
    // "queued" times are the main thread's.
    private static long _rtDrawStart, _rtDrawEnd, _drawStartQueued, _drawEndQueued;
    private static Callable _rtDrawStartMarker, _rtDrawEndMarker;
    private static SendOrPostCallback? _queueDrawEnd;
    private static double _drawCallSum, _cmdScriptsSum, _cmdFlushSum, _rtIdleSum;
    private static int _drawFrames;

    /// <summary>A draw whose end marker was queued later than this after its pre_draw is left out: a
    /// continuation posted earlier runs first in frame(), and what it queued would sit inside the
    /// bracket.</summary>
    private const double LateEndMarkerMs = 1.0;

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

    /// <summary>FEAT-PERF-24, separate mode with <c>--diag</c> only, else -1: per frame, the render
    /// thread inside <c>_draw</c>; executing the commands queued from the previous pre_draw to scripts
    /// end, and from there to the flush end; and the rest of its cycle, in which it may have been idle
    /// (an upper bound). See the class summary.</summary>
    public static double DrawCallMs { get; private set; } = -1;
    public static double CmdScriptsMs { get; private set; } = -1;
    public static double CmdFlushMs { get; private set; } = -1;
    public static double RtIdleMaxMs { get; private set; } = -1;
    public static double CommandMs => CmdScriptsMs < 0 ? -1 : CmdScriptsMs + CmdFlushMs;

    /// <summary>Prims removed (<c>QueueFree</c>d) per frame: the delete-queue half of the tail.</summary>
    public static double FreesPerFrame { get; private set; }

    /// <summary>Counts one prim node handed to the delete queue (ObjectRenderer.RemoveVisual).</summary>
    public static void CountFree() => _frees++;

    /// <summary>Which of our systems wrote a Node3D transform or <c>Visible</c> (each queues a transform
    /// notification for Godot's flush). Diagnostic only; main thread.</summary>
    public enum XformSource { ObjectUpdate, Extrapolate, Spin, TargetOmega, AvatarRoot, AvatarAttach, Visibility, Other }

    private static readonly int[] _xform = new int[8];
    private static readonly double[] _xformPerFrame = new double[8];

    /// <summary>Counts one Node3D transform/visibility write from <paramref name="s"/> (main thread only).</summary>
    public static void CountXform(XformSource s) => _xform[(int)s]++;

    /// <summary>Counts <paramref name="n"/> such writes.</summary>
    public static void CountXform(XformSource s, int n) => _xform[(int)s] += n;

    /// <summary>The window's writes per frame, "xform=obj:..,extrap:.." (--diag only, else empty).</summary>
    public static string DescribeXform()
    {
        if (!Diagnostics.Enabled) return "";
        static string F(double v) => v.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
        var x = _xformPerFrame;
        return $"xform=obj:{F(x[0])},extrap:{F(x[1])},spin:{F(x[2])},omega:{F(x[3])},avRoot:{F(x[4])},avAtt:{F(x[5])},vis:{F(x[6])},other:{F(x[7])} ";
    }

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
            _rtDrawStartMarker = Callable.From(() => Volatile.Write(ref _rtDrawStart, Stopwatch.GetTimestamp()));
            _rtDrawEndMarker = Callable.From(OnRenderThreadDrawEnd);
            _queueDrawEnd = _ =>
            {
                _drawEndQueued = Stopwatch.GetTimestamp();
                RenderingServer.CallOnRenderThread(_rtDrawEndMarker);
            };
            RenderSections.Install();
            _rtMarkersOn = true;
        }
    }

    /// <summary>Render thread, right after <c>_draw</c>: the stamp, then <see cref="RenderSections"/>
    /// reads Godot's captures (the one exception to "a timestamp write and nothing else", and it calls
    /// only RenderingDevice getters that must run on this thread).</summary>
    private static void OnRenderThreadDrawEnd()
    {
        long now = Stopwatch.GetTimestamp();
        Volatile.Write(ref _rtDrawEnd, now);
        // Both "queued" times were written before their markers were queued, and the main thread
        // cannot reach the next pre_draw before this has run (its sync waits for it).
        bool onTime = Ms(Volatile.Read(ref _drawEndQueued) - Volatile.Read(ref _drawStartQueued)) <= LateEndMarkerMs;
        RenderSections.OnDrawEnd(Volatile.Read(ref _rtDrawStart), now, onTime);
    }

    /// <summary>Main thread, in pre_draw: brackets the draw about to be queued. The start marker lands
    /// in the queue right before <c>_draw</c>; the end marker is queued from the frame() hook after
    /// <c>draw()</c> returns.</summary>
    private static void BracketDraw(long preDraw)
    {
        _drawStartQueued = preDraw;
        RenderingServer.CallOnRenderThread(_rtDrawStartMarker);
        Dispatcher.SynchronizationContext.Post(_queueDrawEnd!, null);
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
        AccumulateDrawSplit(preDraw, afterScripts, afterFlush, flushMark);
    }

    /// <summary>The previous frame's draw and the render thread's cycle after it, up to this frame's
    /// pre_draw (separate mode, <c>--diag</c>). Read before this frame's start marker is queued.</summary>
    private static void AccumulateDrawSplit(long preDraw, long afterScripts, long afterFlush, long flushMark)
    {
        long start = Volatile.Read(ref _rtDrawStart), end = Volatile.Read(ref _rtDrawEnd);
        long startQueued = _drawStartQueued, endQueued = _drawEndQueued;
        // Both stamps must belong to the draw queued at the previous pre_draw, and the end marker must
        // have gone out on time (LateEndMarkerMs).
        if (startQueued == 0 || start < startQueued || endQueued < startQueued || end < endQueued
            || end < start || afterScripts < end || Ms(endQueued - startQueued) > LateEndMarkerMs)
        {
            return;
        }
        _drawCallSum += Ms(end - start);
        // Each command stretch counts only from the moment its marker was queued, or from where the
        // previous one ended if that was later: from then on the render thread had work waiting.
        _cmdScriptsSum += Ms(afterScripts - System.Math.Max(end, _scriptsEnd));
        _cmdFlushSum += Ms(afterFlush - System.Math.Max(afterScripts, flushMark));
        _rtIdleSum += Ms(System.Math.Max(0, _scriptsEnd - end)) + Ms(System.Math.Max(0, flushMark - afterScripts))
                    + Ms(System.Math.Max(0, preDraw - afterFlush));
        _drawFrames++;
    }

    private static void OnPreDraw()
    {
        long now = Stopwatch.GetTimestamp();
        _preDraw = now;
        if (!RenderThread.IsSeparate) return;

        long previous = _lastPostDraw; // in this mode: the previous frame's pre_draw
        _lastPostDraw = now;
        if (previous != 0 && previous < _processFrame && _processFrame <= _scriptsStart
            && _scriptsStart <= _scriptsEnd && _scriptsEnd <= now)
        {
            _preFlushSum += Ms(_scriptsStart - _processFrame);
            _scriptsSum += Ms(_scriptsEnd - _scriptsStart);
            _postFlushSum += Ms(now - _scriptsEnd);
            _frameSum += Ms(now - previous);
            _frames++;
            AccumulateSplit(now);
        }
        if (_rtMarkersOn) BracketDraw(now);
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
        for (int i = 0; i < _xform.Length; i++) { _xformPerFrame[i] = (double)_xform[i] / _frames; _xform[i] = 0; }
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

        DrawCallMs = _drawFrames > 0 ? _drawCallSum / _drawFrames : -1;
        CmdScriptsMs = _drawFrames > 0 ? _cmdScriptsSum / _drawFrames : -1;
        CmdFlushMs = _drawFrames > 0 ? _cmdFlushSum / _drawFrames : -1;
        RtIdleMaxMs = _drawFrames > 0 ? _rtIdleSum / _drawFrames : -1;
        _drawCallSum = _cmdScriptsSum = _cmdFlushSum = _rtIdleSum = 0;
        _drawFrames = 0;
        if (_rtMarkersOn) RenderSections.Settle();
    }

    /// <summary>The FEAT-PERF-24 render-thread fields of the <c>[Perf]</c> line, keys always present,
    /// "-" without <c>--diag</c> on a separate render thread. The composite values use the invariant
    /// culture so their commas stay separators.</summary>
    public static string DescribeRenderThread()
    {
        static string Ms1(double ms) => ms < 0 ? "-" : ms.ToString("F1");
        static string Inv(double ms) => ms < 0 ? "-" : ms.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
        return $"drawCallMs={Ms1(DrawCallMs)} commandMs={Ms1(CommandMs)} rtIdleMaxMs={Ms1(RtIdleMaxMs)} " +
               $"cmdSplit=scripts:{Inv(CmdScriptsMs)},flush:{Inv(CmdFlushMs)} " +
               $"drawSplit=pre:{Inv(RenderSections.PreMs)},vp:{Inv(RenderSections.ViewportsMs)},post:{Inv(RenderSections.PostMs)}";
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
