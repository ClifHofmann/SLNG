using System;
using System.Threading;
using Godot;

namespace SLNG.App;

/// <summary>
/// Background thread that reports when the main thread stops ticking.
///
/// Exists because of a gap in the existing <c>[FrameHitch]</c> diagnostic, which prints from inside
/// <c>_Process</c> when the previous frame took too long. That works for a slow frame, but says
/// nothing at all about a frame that never ends: a client that truly hangs and has to be killed
/// prints no hitch line, because the frame that would have printed it never runs. Across every
/// session log on this machine there is not one FrameHitch line, which is consistent both with "the
/// main thread never stalled" and with "it stalled so hard it never came back" -- exactly the two
/// possibilities a user report of "sometimes the whole client freezes" has to distinguish between.
///
/// So the observer has to live off the main thread. This one watches a heartbeat that _Process
/// updates and, when it goes stale, logs what the main thread was last seen doing.
///
/// Deliberately a plain background thread rather than a Godot timer or another node: everything
/// driven by the scene tree stops when the main thread stops, which is precisely when this is needed.
/// </summary>
public sealed class MainThreadWatchdog
{
    /// <summary>Well past any legitimate frame. The worst frame ever measured on this client was
    /// ~350 ms, and the frame-time budget work has since brought the p99 to about 17 ms, so anything
    /// at this scale is a hang rather than a slow frame.</summary>
    private const double StallSeconds = 2.0;

    /// <summary>How often to re-report while a stall continues. Without it a five-minute hang would
    /// produce one line and no sense of duration; with it the log shows the stall growing, which also
    /// distinguishes a permanent deadlock from something slow that eventually finished.</summary>
    private const double RepeatSeconds = 5.0;

    private long _heartbeat;
    private Thread? _thread;
    private volatile bool _running;

    /// <summary>Called from _Process every frame. Just a timestamp write -- deliberately the cheapest
    /// thing that can be put in the frame path, since it is on every frame forever.</summary>
    public void Beat() => Interlocked.Exchange(ref _heartbeat, Stopwatch64.Now);

    public void Start()
    {
        if (_running) return;
        _running = true;
        Beat();

        _thread = new Thread(Watch)
        {
            IsBackground = true, // must never keep the process alive on shutdown
            Name = "MainThreadWatchdog",
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
    }

    public void Stop() => _running = false;

    private void Watch()
    {
        double reportedAt = 0;

        while (_running)
        {
            Thread.Sleep(500);

            double stalledFor = Stopwatch64.SecondsSince(Interlocked.Read(ref _heartbeat));

            if (stalledFor < StallSeconds)
            {
                reportedAt = 0;
                continue;
            }

            if (reportedAt != 0 && stalledFor - reportedAt < RepeatSeconds) continue;
            reportedAt = stalledFor;

            // GD.Print is used rather than Logger because this has to reach the log even when the
            // main thread is wedged, and it is the one place where a stall is worth a line no matter
            // what the log level says.
            GD.Print($"[Watchdog] main thread has not ticked for {stalledFor:0.0}s " +
                      $"phase={MainThreadPhase.Current} " +
                      $"lastWork={MainThreadWorkQueue.CurrentLabel ?? "(none)"} " +
                      $"queue={MainThreadWorkQueue.Depth}");
        }
    }

    /// <summary>Monotonic clock helpers. <see cref="DateTime"/> is unusable here because a clock
    /// adjustment would fake a stall, and Stopwatch's own Elapsed is not safe to read from another
    /// thread while a different one restarts it.</summary>
    private static class Stopwatch64
    {
        public static long Now => System.Diagnostics.Stopwatch.GetTimestamp();

        public static double SecondsSince(long timestamp)
            => (System.Diagnostics.Stopwatch.GetTimestamp() - timestamp)
               / (double)System.Diagnostics.Stopwatch.Frequency;
    }
}
