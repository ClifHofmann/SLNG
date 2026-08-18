using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Godot;

namespace SLNG.App;

/// <summary>
/// A time-budgeted queue for work that MUST run on the Godot main thread (building object visuals,
/// pushing a re-decoded texture into an existing ImageTexture) but does NOT have to run in any
/// particular frame.
///
/// Why this exists: the obvious way to marshal such work is <c>CallDeferred</c>, and that is what
/// the renderer and the texture cache both did. But Godot flushes its whole deferred-call queue
/// within the same frame, so the queue depth is decided by the network, not by the frame budget.
/// Walking into a dense parcel makes the sim deliver a few hundred ObjectUpdates at once, every one
/// of them queues a visual build, and the main thread then does all of it in a single frame.
/// Measured on OSGrid: median frame 16.7 ms (a solid 60 FPS) but p99 131.6 ms and ~2.2 such frames
/// per second, with Godot's process-time monitor at 63.8 ms against 0.4 ms of physics -- the cost
/// was main-thread C#, not the GPU, and it appeared only while moving.
///
/// IMPORTANT -- what budgeting alone does NOT fix. Spreading work across frames only helps if the
/// TOTAL is affordable. The first measurement after this queue went in showed a backlog of 21,335
/// items that never drained, and frame times slightly WORSE than before (p99 138 ms, 3.5 hitches/s):
/// instead of bursts separated by calm, the main thread now ground continuously. A queue converts a
/// latency problem into a throughput problem; it cannot create throughput. That is what
/// <see cref="ReportCosts"/> is for -- knowing which kind of work makes up the backlog, and what one
/// unit of it costs, is the difference between fixing it and guessing again.
///
/// Thread-safe to enqueue from anywhere -- LibreMetaverse's network threads and the asset decode
/// workers both do. Draining only ever happens on the main thread, from <see cref="Pump"/>, which
/// <see cref="MainThreadWorkPump"/> drives once per frame.
/// </summary>
public static class MainThreadWorkQueue
{
    /// <summary>Lanes exist so a flood of cosmetic work can't delay what the user is actually
    /// waiting to see. An object that has not appeared yet outranks sharpening the texture on an
    /// object already on screen.</summary>
    public enum Lane
    {
        /// <summary>Creating or updating an object's visual -- the user is waiting for this.</summary>
        Visual = 0,

        /// <summary>Cosmetic refinement of something already visible (texture sharpening).</summary>
        Refine = 1,
    }

    private sealed class Item
    {
        public Action Work = null!;
        public string? CoalesceKey;
        public string Label = "?";
    }

    /// <summary>Per-label cost accounting, so the backlog can be attributed instead of guessed at.</summary>
    private sealed class Cost
    {
        public int Count;
        public double TotalMs;
        public double MaxMs;
    }

    private static readonly ConcurrentQueue<Item>[] _lanes =
    {
        new ConcurrentQueue<Item>(), // Visual
        new ConcurrentQueue<Item>(), // Refine
    };

    /// <summary>Keys currently sitting in a lane. A plain HashSet under its own lock rather than a
    /// ConcurrentDictionary because enqueue must test-and-add atomically with respect to the drain
    /// that removes the key -- as two separate concurrent operations, a duplicate could slip in
    /// between them.</summary>
    private static readonly HashSet<string> _pendingKeys = new();
    private static readonly object _keyLock = new();

    /// <summary>Written only from the pump, i.e. the main thread, so it needs no lock.</summary>
    private static readonly Dictionary<string, Cost> _costs = new();

    private static readonly Stopwatch _clock = new();

    /// <summary>True while a pump is in the scene tree. Before that (and after it leaves) enqueued
    /// work falls back to CallDeferred, so nothing is silently dropped during startup or shutdown --
    /// unbudgeted is still far better than never.</summary>
    private static bool _pumpActive;

    /// <summary>Maintained with Interlocked rather than read from ConcurrentQueue.Count. Count has to
    /// walk the queue's internal segments, and at a depth of 20k that is not free -- it was being
    /// paid on every single enqueue, from LibreMetaverse's network threads.</summary>
    private static int _depth;

    /// <summary>Longest depth since the last <see cref="TakeStats"/>. A depth that never returns to
    /// zero means the budget cannot keep up with what the sim is delivering.</summary>
    private static int _peakDepth;

    public static int Depth => Volatile.Read(ref _depth);

    /// <summary>
    /// Queues <paramref name="work"/> to run on the main thread within the frame budget.
    ///
    /// <paramref name="coalesceKey"/> collapses repeats: while a key is still pending, further
    /// enqueues with the same key are dropped. That is safe -- and necessary -- for work that
    /// re-reads current world state when it runs, which is exactly what the renderer's
    /// CreateVisual/UpdateVisual do. Without it a physically moving object queues one update per
    /// TerseObjectUpdate, the backlog grows faster than any budget can drain it, and the visual falls
    /// progressively further behind the world. Pass null for work that must run once per call.
    ///
    /// <paramref name="label"/> groups the item for <see cref="ReportCosts"/>. Keep it a small fixed
    /// set of constants, never per-object text, or the cost table grows without bound.
    /// </summary>
    public static void Enqueue(Lane lane, Action work, string? coalesceKey = null, string label = "?")
    {
        if (coalesceKey != null)
        {
            lock (_keyLock)
            {
                if (!_pendingKeys.Add(coalesceKey)) return; // an equivalent item is already waiting
            }
        }

        if (!_pumpActive)
        {
            if (coalesceKey != null)
            {
                string key = coalesceKey;
                Godot.Callable.From(() => { ReleaseKey(key); work(); }).CallDeferred();
            }
            else
            {
                Godot.Callable.From(work).CallDeferred();
            }
            return;
        }

        _lanes[(int)lane].Enqueue(new Item { Work = work, CoalesceKey = coalesceKey, Label = label });

        int depth = Interlocked.Increment(ref _depth);
        if (depth > _peakDepth) _peakDepth = depth;
    }

    private static void ReleaseKey(string key)
    {
        lock (_keyLock) _pendingKeys.Remove(key);
    }

    /// <summary>
    /// Runs queued work until the budget is spent. Main thread only.
    ///
    /// Each lane is allowed one item even when the budget is already gone. That serves two purposes:
    /// a single pathologically expensive unit of work (a huge linkset) still makes progress instead
    /// of deadlocking against a budget it can never fit inside, and a permanently busy Visual lane
    /// cannot starve Refine forever. The flip side is that the floor on frame cost is the cost of one
    /// item, so if a single item is slow the budget cannot save the frame -- which is precisely what
    /// <see cref="ReportCosts"/>'s maxMs column is there to reveal.
    /// </summary>
    public static void Pump(double budgetMs)
    {
        _clock.Restart();

        for (int lane = 0; lane < _lanes.Length; lane++)
        {
            bool ranOne = false;
            while (true)
            {
                if (ranOne && _clock.Elapsed.TotalMilliseconds >= budgetMs) break;
                if (!_lanes[lane].TryDequeue(out var item)) break;

                Interlocked.Decrement(ref _depth);
                if (item.CoalesceKey != null) ReleaseKey(item.CoalesceKey);

                double before = _clock.Elapsed.TotalMilliseconds;
                try
                {
                    item.Work();
                }
                catch (Exception ex)
                {
                    // One bad unit of work must not take the pump down with it, or every later object
                    // in the queue silently never appears.
                    GD.PrintErr($"[MainThreadWork] item threw: {ex.Message}");
                }
                Record(item.Label, _clock.Elapsed.TotalMilliseconds - before);

                ranOne = true;
            }
        }
    }

    /// <summary>Times an arbitrary main-thread operation into the same cost table as queued work.
    /// Exists so a single queue item can be broken down into its parts without having to split it
    /// into separate queue items -- the parts have to run together, but their costs do not have to
    /// be reported together, and "which half of this is slow" is usually the whole question.</summary>
    public static void Measure(string label, Action work)
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            work();
        }
        finally
        {
            Record(label, (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency);
        }
    }

    private static void Record(string label, double ms)
    {
        if (!_costs.TryGetValue(label, out var c)) _costs[label] = c = new Cost();
        c.Count++;
        c.TotalMs += ms;
        if (ms > c.MaxMs) c.MaxMs = ms;
    }

    /// <summary>
    /// Writes one line per kind of work, ordered by total time spent, then clears the tally.
    ///
    /// Total time is the figure that matters here, not the average. Budgeting can only fix a queue
    /// whose total cost fits in the time available, so the question is "what is consuming the
    /// seconds", and a kind of work that is individually cheap but runs 20,000 times is exactly what
    /// an average would hide behind something rarer and slower. maxMs is the companion figure: it
    /// says whether any single item is on its own too big to fit in a frame.
    /// </summary>
    public static void ReportCosts()
    {
        if (_costs.Count == 0) return;

        foreach (var (label, c) in _costs.OrderByDescending(kv => kv.Value.TotalMs))
        {
            Logger.Info($"[WorkCost] {label,-18} n={c.Count,-6} totalMs={c.TotalMs,8:F1} " +
                        $"avgMs={c.TotalMs / Math.Max(c.Count, 1),6:F2} maxMs={c.MaxMs,7:F1}");
        }
        _costs.Clear();
    }

    /// <summary>Current depth plus the peak since the previous call, which this resets.</summary>
    public static (int depth, int peak) TakeStats()
    {
        int peak = _peakDepth;
        _peakDepth = Depth;
        return (Depth, peak);
    }

    internal static void SetPumpActive(bool active) => _pumpActive = active;
}
