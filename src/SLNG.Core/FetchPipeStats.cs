using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace SLNG.Core;

/// <summary>
/// A cheap counter set for one asset fetch pipeline (meshes, materials, texture HTTP), printed as one
/// line every few seconds under <c>--diag</c> -- never one line per request. Written for the cold-start
/// question "which fetch lane is the slow one": requests, how many are open (and the peak of the last
/// interval, which shows the real concurrency the HTTP stack gives us), completed, failed, bytes, and
/// the median and 95th-percentile latency of the interval.
///
/// <para>Latency is begin-to-end as the caller sees it, so it includes any wait for a slot; the in-flight
/// peak next to it says how many were open at once.</para>
///
/// <para>Thread-safe; the lock is held for a few instructions per request.</para>
/// </summary>
public sealed class FetchPipeStats
{
    /// <summary>What <see cref="TakeInterval"/> hands back. <c>Requests</c>, <c>Completed</c>, <c>Failed</c>
    /// and <c>Bytes</c> are cumulative; the <c>Interval</c> ones and the percentiles cover the time since the
    /// previous call.</summary>
    public readonly record struct Snapshot(
        long Requests, long Completed, long Failed, long Bytes,
        long IntervalRequests, long IntervalCompleted, long IntervalFailed, long IntervalBytes,
        int InFlight, int PeakInFlight, double P50Ms, double P95Ms);

    private readonly string _name;
    private readonly object _lock = new();
    private readonly double[] _samples;
    private int _sampleCount;      // samples seen this interval (may exceed the buffer)
    private long _requests, _completed, _failed, _bytes;
    private long _intervalRequests, _intervalCompleted, _intervalFailed, _intervalBytes;
    private int _inFlight, _peak;

    public FetchPipeStats(string name, int sampleCapacity = 2048)
    {
        _name = name;
        _samples = new double[Math.Max(8, sampleCapacity)];
    }

    /// <summary>A request starts. Returns the timestamp to give <see cref="End"/>.</summary>
    public long Begin()
    {
        lock (_lock)
        {
            _requests++;
            _intervalRequests++;
            if (++_inFlight > _peak) _peak = _inFlight;
        }
        return Stopwatch.GetTimestamp();
    }

    /// <summary>The request that started at <paramref name="startTimestamp"/> is over.</summary>
    public void End(long startTimestamp, bool ok, long bytes) =>
        EndTimed(Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds, ok, bytes);

    /// <summary>As above with the elapsed time already known (a test, or a caller that timed it).</summary>
    public void EndTimed(double elapsedMs, bool ok, long bytes)
    {
        lock (_lock)
        {
            _inFlight--;
            if (ok)
            {
                _completed++; _intervalCompleted++;
                _bytes += bytes; _intervalBytes += bytes;
            }
            else
            {
                _failed++; _intervalFailed++;
            }
            // Beyond the buffer, new samples overwrite old ones: the percentiles then describe the interval's
            // tail, which is what a slow end of a load is about.
            _samples[_sampleCount % _samples.Length] = elapsedMs;
            _sampleCount++;
        }
    }

    /// <summary>The counters, with the percentiles of the interval since the previous call; starts a new interval.</summary>
    public Snapshot TakeInterval()
    {
        lock (_lock)
        {
            int n = Math.Min(_sampleCount, _samples.Length);
            double p50 = 0, p95 = 0;
            if (n > 0)
            {
                var copy = new double[n];
                Array.Copy(_samples, copy, n);
                Array.Sort(copy);
                p50 = copy[Math.Min(n - 1, (int)(n * 0.50))];
                p95 = copy[Math.Min(n - 1, (int)(n * 0.95))];
            }

            var snap = new Snapshot(_requests, _completed, _failed, _bytes,
                _intervalRequests, _intervalCompleted, _intervalFailed, _intervalBytes,
                _inFlight, _peak, p50, p95);

            _intervalRequests = _intervalCompleted = _intervalFailed = _intervalBytes = 0;
            _sampleCount = 0;
            _peak = _inFlight;
            return snap;
        }
    }

    /// <summary>One log line for the interval since the previous call, or null when nothing happened in it
    /// and nothing is open (so an idle pipe is silent).</summary>
    public string? TakeLine()
    {
        var s = TakeInterval();
        if (s.IntervalRequests == 0 && s.IntervalCompleted == 0 && s.IntervalFailed == 0 && s.InFlight == 0)
            return null;
        var c = CultureInfo.InvariantCulture;
        return string.Create(c,
            $"[{_name}] req={s.Requests} done={s.Completed} failed={s.Failed} inflight={s.InFlight} peak={s.PeakInFlight} " +
            $"MB={s.Bytes / (1024.0 * 1024.0):0.0} | last interval: +{s.IntervalRequests} req +{s.IntervalCompleted} done " +
            $"+{s.IntervalFailed} failed {s.IntervalBytes / 1024}KB p50={s.P50Ms:0}ms p95={s.P95Ms:0}ms");
    }
}

/// <summary>
/// Prints the registered fetch-pipeline lines every <see cref="IntervalSeconds"/> seconds, when the
/// verbose switch (<see cref="Diag.Verbose"/>) is on. One timer for all of them; a provider that returns
/// null prints nothing that round.
/// </summary>
public static class FetchPipeReporter
{
    public const int IntervalSeconds = 5;

    private static readonly List<Func<string?>> _providers = new();
    private static readonly object _lock = new();
    private static Timer? _timer;

    /// <summary>Adds a line provider and starts the timer on the first one. Does nothing when the verbose
    /// switch is off, so a normal run has no timer at all.</summary>
    public static void Register(Func<string?> provider)
    {
        if (!Diag.Verbose) return;
        lock (_lock)
        {
            _providers.Add(provider);
            _timer ??= new Timer(_ => Tick(), null, TimeSpan.FromSeconds(IntervalSeconds), TimeSpan.FromSeconds(IntervalSeconds));
        }
    }

    public static void Register(FetchPipeStats stats) => Register(stats.TakeLine);

    private static void Tick()
    {
        Func<string?>[] snapshot;
        lock (_lock) snapshot = _providers.ToArray();
        foreach (var provider in snapshot)
        {
            try
            {
                string? line = provider();
                if (line != null) Diag.Write(line);
            }
            catch
            {
                // A diagnostic must never take the process down.
            }
        }
    }
}
