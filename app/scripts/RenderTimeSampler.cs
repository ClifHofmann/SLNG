using System;
using System.Collections.Concurrent;
using System.Threading;
using Godot;

namespace SLNG.App;

/// <summary>
/// FEAT-PERF-13: one background thread that runs the engine's render-time getters, so the main
/// thread never has to.
///
/// <para>With Godot rendering on its own thread, most <c>RenderingServer</c> getters are a round
/// trip: the call goes into the render thread's command queue and the caller sleeps until the
/// render thread reaches it (<c>FUNC*RC</c> / <c>FUNC*S</c> in <c>rendering_server_default.h</c>,
/// 4.7-stable). The render thread reaches it only after the frame it is drawing. Asked once a frame
/// from <c>_Process</c>, <c>viewport_get_measured_render_time_cpu</c> would make the main thread wait
/// out the previous frame's whole draw every frame, which is exactly the serial frame this mode
/// exists to remove, and the A/B would measure the measuring.</para>
///
/// <para>A non-main thread is allowed to call the RenderingServer in either mode (that is what "Safe"
/// promises). Here it blocks instead of the main thread. A background .NET thread, not a Godot
/// callable on the render thread: <c>Callable.From(lambda)</c> is a custom callable that the
/// engine would invoke and release on the render thread, and the project has seen that bridge
/// crash off the main thread (see the <c>Callable.From</c> note in AGENTS.md / app-rules).</para>
///
/// <para>Work is dropped, not queued without limit, when the render thread is so far behind that
/// <see cref="MaxPending"/> requests are already waiting: a frame without a sample costs nothing
/// but one data point, and a growing queue would report ever staler numbers.</para>
/// </summary>
internal sealed class RenderTimeSampler : IDisposable
{
    internal const int MaxPending = 4;

    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Thread _thread;
    private int _pending;
    private volatile bool _stop;
    private bool _faultLogged;

    public RenderTimeSampler()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "RenderTimeSampler" };
        _thread.Start();
    }

    /// <summary>Queues <paramref name="work"/> for the sampler thread. False when it was dropped
    /// because too many requests are already waiting.</summary>
    public bool Post(Action work)
    {
        if (_stop || Volatile.Read(ref _pending) >= MaxPending) return false;
        Interlocked.Increment(ref _pending);
        _queue.Enqueue(work);
        _signal.Release();
        return true;
    }

    /// <summary>Blocks until everything posted so far has run (or <paramref name="timeoutMs"/>
    /// passed). For the selftest; the frame loop never waits.</summary>
    public bool WaitIdle(int timeoutMs)
    {
        var until = System.Environment.TickCount64 + timeoutMs;
        while (Volatile.Read(ref _pending) > 0)
        {
            if (System.Environment.TickCount64 > until) return false;
            Thread.Sleep(1);
        }
        return true;
    }

    private void Run()
    {
        while (true)
        {
            _signal.Wait();
            if (_stop) return;
            if (!_queue.TryDequeue(out var work)) continue;
            try
            {
                work();
            }
            catch (Exception ex)
            {
                // Once: a getter that fails will fail every frame.
                if (!_faultLogged)
                {
                    _faultLogged = true;
                    GD.PrintErr($"[RenderTimes] sampler thread: {ex.GetType().Name}: {ex.Message}");
                }
            }
            finally
            {
                Interlocked.Decrement(ref _pending);
            }
        }
    }

    /// <summary>Stops the thread. A getter still waiting on a render thread that has already exited
    /// would wait for ever, so the join is bounded; the thread is a background thread and cannot keep
    /// the process alive.</summary>
    public void Dispose()
    {
        _stop = true;
        _signal.Release();
        _thread.Join(250);
    }
}
