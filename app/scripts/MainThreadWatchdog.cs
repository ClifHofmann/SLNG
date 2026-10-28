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
    public const double StallSeconds = 2.0;

    /// <summary>BUG-PERF-22: stall threshold for falling back from separate render thread to main thread.</summary>
    public const double FallbackStallSeconds = 10.0;

    /// <summary>How often to re-report while a stall continues. Without it a five-minute hang would
    /// produce one line and no sense of duration; with it the log shows the stall growing, which also
    /// distinguishes a permanent deadlock from something slow that eventually finished.</summary>
    private const double RepeatSeconds = 5.0;

    private long _heartbeat;
    private Thread? _thread;
    private volatile bool _running;

    private bool _isSeparate;
    private bool _savedPreferenceIsSeparate;
    private string? _overrideFilePath;
    private string? _markerFilePath;
    private bool _isHeadlessOrSelfTest;

    private bool _fallbackHandled;
    private bool _fallbackApplied;
    private bool _fallbackRecoveredLogged;

    public bool IsRunning => _running;
    public bool FallbackApplied => _fallbackApplied;

    /// <summary>Called from _Process every frame. Just a timestamp write -- deliberately the cheapest
    /// thing that can be put in the frame path, since it is on every frame forever.</summary>
    public void Beat() => Interlocked.Exchange(ref _heartbeat, Stopwatch64.Now);

    public void ConfigureFallback(
        bool isSeparate,
        bool savedPreferenceIsSeparate,
        string overrideFilePath,
        string markerFilePath,
        bool isHeadlessOrSelfTest)
    {
        _isSeparate = isSeparate;
        _savedPreferenceIsSeparate = savedPreferenceIsSeparate;
        _overrideFilePath = overrideFilePath;
        _markerFilePath = markerFilePath;
        _isHeadlessOrSelfTest = isHeadlessOrSelfTest;
    }

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
            EvaluateStall(stalledFor, ref reportedAt);
        }
    }

    /// <summary>
    /// Evaluates stall duration for testing or watchdog loop.
    /// </summary>
    public bool EvaluateStall(double stalledFor)
    {
        double dummy = 0;
        return EvaluateStall(stalledFor, ref dummy);
    }

    private bool EvaluateStall(double stalledFor, ref double reportedAt)
    {
        if (stalledFor < StallSeconds)
        {
            if (_fallbackApplied && !_fallbackRecoveredLogged)
            {
                _fallbackRecoveredLogged = true;
                string recoveredMsg = "[RenderThread] main thread recovered after stall (saved choice remains main thread)";
                Console.WriteLine(recoveredMsg);
                GD.Print(recoveredMsg);
            }
            reportedAt = 0;
            return false;
        }

        if (reportedAt == 0 || stalledFor - reportedAt >= RepeatSeconds)
        {
            reportedAt = stalledFor;

            // GD.Print is used rather than Logger because this has to reach the log even when the
            // main thread is wedged, and it is the one place where a stall is worth a line no matter
            // what the log level says.
            GD.Print($"[Watchdog] main thread has not ticked for {stalledFor:0.0}s " +
                      $"phase={MainThreadPhase.Current} " +
                      $"lastWork={MainThreadWorkQueue.CurrentLabel ?? "(none)"} " +
                      $"queue={MainThreadWorkQueue.Depth}");
        }

        if (stalledFor >= FallbackStallSeconds && !_fallbackHandled)
        {
            _fallbackHandled = true;

            if (_isSeparate)
            {
                if (_savedPreferenceIsSeparate && !_isHeadlessOrSelfTest)
                {
                    if (!string.IsNullOrEmpty(_overrideFilePath))
                    {
                        RenderThread.SavePreferenceOffThread(_overrideFilePath, separate: false);
                    }
                    if (!string.IsNullOrEmpty(_markerFilePath))
                    {
                        SLNG.Core.RenderThreadFallback.WriteMarker(_markerFilePath, stalledFor);
                    }

                    string fallbackMsg = $"[RenderThread] main thread stalled {(int)stalledFor} s with the separate render thread -- switched the saved choice back to the main thread for the next start";
                    Console.WriteLine(fallbackMsg);
                    GD.Print(fallbackMsg);

                    _fallbackApplied = true;
                    return true;
                }
                else if (!_savedPreferenceIsSeparate)
                {
                    string loggedMsg = $"[RenderThread] main thread stalled {(int)stalledFor} s with the separate render thread (command line, not saved -- not switching)";
                    Console.WriteLine(loggedMsg);
                    GD.Print(loggedMsg);
                }
            }
        }

        return false;
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
