using System.Diagnostics;
using System.Threading;

namespace SLNG.App;

/// <summary>
/// Keeps worker threads that call into Godot -- <c>SurfaceTool</c>, packed arrays -- away from an
/// engine that is shutting down. BUG-PERF-06.
/// </summary>
/// <remarks>
/// The same hazard <see cref="GpuCache.BeginShutdown"/> documents for texture workers: a call into
/// Godot from a thread-pool thread that lands while the engine is finalising its C# interop dies
/// with an <c>AccessViolationException</c>, which is fatal and uncatchable. Mesh preparation moved
/// to workers for the same reason the texture work did, so it needs the same guard. GpuCache's
/// BeginShutdown closes this one too, so every quit path that already stops the texture workers
/// stops these.
///
/// <para>The order is the one GpuCache uses: a worker counts itself in FIRST and checks the flag
/// SECOND, so <see cref="Close"/> either sees it in the count and waits, or the worker sees the
/// flag and stays out.</para>
/// </remarks>
internal static class EngineWorkerGate
{
    private static volatile bool _closed;
    private static int _running;

    /// <summary>True when the caller may call into Godot; it must then call <see cref="Exit"/>.</summary>
    public static bool TryEnter()
    {
        Interlocked.Increment(ref _running);
        if (!_closed) return true;
        Interlocked.Decrement(ref _running);
        return false;
    }

    public static void Exit() => Interlocked.Decrement(ref _running);

    /// <summary>No new worker may enter after this; waits (bounded) for the ones already inside.</summary>
    public static void Close(int maxWaitMs = 2000)
    {
        _closed = true;
        var clock = Stopwatch.StartNew();
        while (Volatile.Read(ref _running) > 0 && clock.ElapsedMilliseconds < maxWaitMs)
            Thread.Sleep(5);
    }
}
