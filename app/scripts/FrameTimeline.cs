using System.Diagnostics;
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
/// </summary>
public partial class FrameTimeline : Node
{
    private static long _processFrame, _scriptsStart, _scriptsEnd, _preDraw, _lastPostDraw;
    private static double _preFlushSum, _scriptsSum, _postFlushSum, _drawSum, _frameSum;
    private static int _frames;
    private static bool _hooked;

    private bool _isEnd;

    /// <summary>Averages per frame over the last <see cref="Settle"/> window. They add up to
    /// <see cref="FrameMs"/>.</summary>
    public static double OtherMs { get; private set; }
    public static double PreFlushMs { get; private set; }
    public static double ScriptsMs { get; private set; }
    public static double PostFlushMs { get; private set; }
    public static double DrawMs { get; private set; }
    public static double FrameMs { get; private set; }

    /// <summary>Adds the two process markers under <paramref name="parent"/> (where they sit does
    /// not matter, only their priority) and hooks the frame signals once.</summary>
    public static void Install(Node parent)
    {
        parent.AddChild(new FrameTimeline { Name = "FrameTimelineStart", ProcessPriority = int.MinValue });
        parent.AddChild(new FrameTimeline { Name = "FrameTimelineEnd", ProcessPriority = int.MaxValue, _isEnd = true });
        if (_hooked) return;
        _hooked = true;
        parent.GetTree().ProcessFrame += () => _processFrame = Stopwatch.GetTimestamp();
        RenderingServer.FramePreDraw += OnPreDraw;
        // Separate render thread: post_draw arrives out of step with the frame (see the class
        // summary), and pre_draw closes the frame instead.
        if (!RenderThread.IsSeparate) RenderingServer.FramePostDraw += OnPostDraw;
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
    }

    public override void _Process(double delta)
    {
        if (_isEnd) _scriptsEnd = Stopwatch.GetTimestamp();
        else _scriptsStart = Stopwatch.GetTimestamp();
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
        _preFlushSum = _scriptsSum = _postFlushSum = _drawSum = _frameSum = 0;
        _frames = 0;
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
