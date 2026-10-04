using System.Runtime.ExceptionServices;

namespace SLNG.Net;

/// <summary>
/// Remembers the exception most recently thrown on each thread, so a log line that says only "no
/// response" can be given the reason that caused it.
/// </summary>
/// <remarks>
/// It exists for BUG-NET-31. LibreMetaverse's seed-capability handler logs "no response" for every
/// exception that reaches it — a refused connection, a non-LLSD error body, a subscriber that threw —
/// and passes the exception to nothing, so the log could not say which of those happened. The handler
/// runs on the thread that threw, immediately after, so the last first-chance exception on that thread
/// is, in practice, the cause. Best effort and only for a log line: never use it to decide anything.
///
/// <para>The handler does the least a first-chance handler may: one thread-static store, no allocation,
/// no throwing.</para>
/// </remarks>
internal static class LastExceptionTracker
{
    [ThreadStatic] private static Exception? _last;

    private static int _subscribers;

    /// <summary>Starts tracking. Balanced with <see cref="Stop"/>; counts, so several sessions can
    /// share the one process-wide hook.</summary>
    internal static void Start()
    {
        if (Interlocked.Increment(ref _subscribers) == 1)
            AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
    }

    internal static void Stop()
    {
        if (Interlocked.Decrement(ref _subscribers) == 0)
            AppDomain.CurrentDomain.FirstChanceException -= OnFirstChance;
    }

    private static void OnFirstChance(object? sender, FirstChanceExceptionEventArgs e) => _last = e.Exception;

    /// <summary>The exception most recently thrown on the calling thread, as "Type: message", or null
    /// when there is none or it is only our own control-flow exception.</summary>
    internal static string? DescribeLast()
    {
        var ex = _last;
        if (ex == null || ex is SeedRequestAbortedException) return null;
        string message = ex.Message;
        if (message.Length > 200) message = message[..200];
        return $"{ex.GetType().Name}: {message}";
    }
}
