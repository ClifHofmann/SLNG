namespace SLNG.Net;

/// <summary>What the guard needs to know about the regions' capability seeding right now.</summary>
/// <param name="InSession">False once the session is gone (logged out, disconnected): there is
/// nothing left to recover.</param>
/// <param name="CurrentUnseeded">The region the agent stands in has a capability system whose seed
/// request has not produced any capability. This is the one that matters: no event queue, no
/// inventory, no HTTP textures without it.</param>
/// <param name="OthersUnseeded">Neighbouring regions in the same state. Dead ones are a loss, not an
/// emergency.</param>
internal readonly record struct SeedSnapshot(bool InSession, bool CurrentUnseeded, int OthersUnseeded);

/// <summary>
/// Replaces the retry LibreMetaverse 3.1.6 gets wrong for the seed capability (BUG-NET-31): bounded,
/// delayed, and ending in a clear outcome instead of a stack overflow.
/// </summary>
/// <remarks>
/// <b>What the library does.</b> <c>Caps.SeedRequestCompleteHandler</c> (LibreMetaverse v3.1.6,
/// <c>LibreMetaverse/Caps.cs</c>) is called with <c>(null, null, ex)</c> whenever the seed POST throws
/// <i>or</i> anything throws after it — the response parse, or any <c>CapabilitiesReceived</c>
/// subscriber, because the handler call sits inside the same <c>try</c>. It logs "Seed capability
/// returned no response. Trying again.", cancels its own <c>_HttpCts</c>, and calls
/// <c>MakeSeedRequestAsync()</c>. That request uses the token it just cancelled, so it fails at once,
/// the <c>await</c> completes synchronously, and the <c>catch</c> calls the handler again — one more
/// stack level per round, no delay, no counter, forever. The 404 branch that should stop it is
/// unreachable: <c>error != null</c> there always comes with <c>response == null</c>.
///
/// <para><b>What SLNG does.</b> The warning is logged before the cancel and the retry, so the log
/// sink throws <see cref="SeedRequestAbortedException"/> and the loop unwinds at depth one. The
/// request that failed is then gone for good, and this class replaces it: after a delay it looks at
/// whether the seed has been answered, and if not it re-seeds with a fresh capability system
/// (<c>Simulator.SetSeedCaps</c>, a public API), up to <see cref="MaxReseeds"/> times with growing
/// delays. If the current region still has no capabilities after that, the session is ended with a
/// message. The reference viewer gives up after 30 attempts
/// (<c>llviewerregion.cpp: MAX_CAP_REQUEST_ATTEMPTS</c>) but each of its attempts is a real HTTP
/// round trip; ours are spaced out by design.</para>
///
/// <para>The seams are delegates so the policy can be tested without a grid or a clock.</para>
/// </remarks>
internal sealed class SeedCapabilityGuard
{
    /// <summary>How many times the seed request is re-issued before the session is given up.</summary>
    internal const int MaxReseeds = 5;

    /// <summary>The text LibreMetaverse's retry line starts with. Matched loosely because the library
    /// may prefix a client name.</summary>
    private const string RetryLineMarker = "Seed capability returned";

    private const string RetryLineSuffix = "Trying again";

    private readonly Func<SeedSnapshot> _snapshot;
    private readonly Action _reseed;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Action<string> _log;
    private readonly Action _giveUp;
    private readonly CancellationTokenSource _cts = new();
    private int _running;
    private Task _completion = Task.CompletedTask;

    /// <summary>True while a recovery is in progress.</summary>
    internal bool IsRecovering => Volatile.Read(ref _running) == 1;

    /// <summary>Completes when the recovery started last has finished. For tests.</summary>
    internal Task Completion => _completion;

    internal SeedCapabilityGuard(
        Func<SeedSnapshot> snapshot,
        Action reseed,
        Func<TimeSpan, CancellationToken, Task> delay,
        Action<string> log,
        Action giveUp)
    {
        _snapshot = snapshot;
        _reseed = reseed;
        _delay = delay;
        _log = log;
        _giveUp = giveUp;
    }

    /// <summary>True for the library's "no response, trying again" line, the one that starts the loop.
    /// The 404 line ("capability system is aborting") does not retry and is left alone.</summary>
    internal static bool IsRetryLine(string message) =>
        message.Contains(RetryLineMarker, StringComparison.Ordinal)
        && message.Contains(RetryLineSuffix, StringComparison.Ordinal);

    /// <summary>The wait before re-seed number <paramref name="attempt"/> (1-based): 2, 4, 8, 15 and
    /// 15 seconds, 44 in all. Long enough for a login that raced a stale session on the grid to
    /// settle, short enough that nobody stares at a half-working client for a minute.</summary>
    internal static TimeSpan DelayBefore(int attempt) => attempt switch
    {
        <= 1 => TimeSpan.FromSeconds(2),
        2 => TimeSpan.FromSeconds(4),
        3 => TimeSpan.FromSeconds(8),
        _ => TimeSpan.FromSeconds(15),
    };

    /// <summary>
    /// A seed request failed. Starts the recovery unless one is already running (further failures
    /// during it are the old requests being cancelled by the re-seed, and carry no news). Never
    /// blocks and never throws: it runs inside the library's own failure path.
    /// </summary>
    /// <param name="cause">What most likely made the request fail, when known (see
    /// <see cref="LastExceptionTracker"/>); only for the log.</param>
    /// <returns>True when this call started a recovery.</returns>
    internal bool OnSeedFailure(string? cause)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return false;

        try
        {
            var state = _snapshot();
            _log("[Caps] the seed capability request failed" +
                 (string.IsNullOrEmpty(cause) ? "" : $" ({cause})") +
                 (state.CurrentUnseeded
                     ? " and the current region has no capabilities yet."
                     : " but the current region's capabilities are already in, so the failure was in something that reacts to them, or in a neighbouring region.") +
                 " LibreMetaverse's own retry is stopped here: it never waits and ends in a stack overflow." +
                 $" SLNG retries up to {MaxReseeds} times instead.");
        }
        catch (Exception ex)
        {
            _log($"[Caps] seed failure logging failed: {ex.Message}");
        }

        _completion = Task.Run(RunAsync);
        return true;
    }

    /// <summary>Stops a recovery in progress (logout, dispose).</summary>
    internal void Cancel()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    private async Task RunAsync()
    {
        try
        {
            var ct = _cts.Token;
            for (int attempt = 1; attempt <= MaxReseeds; attempt++)
            {
                await _delay(DelayBefore(attempt), ct).ConfigureAwait(false);

                var state = _snapshot();
                if (!state.InSession) return;
                if (!state.CurrentUnseeded && state.OthersUnseeded == 0)
                {
                    _log(attempt == 1
                        ? "[Caps] nothing left to recover."
                        : $"[Caps] the seed capability answered after {attempt - 1} re-seed(s).");
                    return;
                }

                _log($"[Caps] the seed capability is still unanswered; re-seeding (attempt {attempt} of {MaxReseeds}).");
                try { _reseed(); }
                catch (Exception ex) { _log($"[Caps] re-seed failed: {ex.Message}"); }
            }

            await _delay(DelayBefore(MaxReseeds), ct).ConfigureAwait(false);

            var last = _snapshot();
            if (!last.InSession) return;
            if (last.CurrentUnseeded)
            {
                _log($"[Caps] the seed capability never answered after {MaxReseeds} re-seeds; ending the session.");
                _giveUp();
            }
            else if (last.OthersUnseeded > 0)
            {
                _log($"[Caps] {last.OthersUnseeded} neighbouring region(s) never answered their seed capability; they stay without HTTP capabilities.");
            }
            else
            {
                _log($"[Caps] the seed capability answered after {MaxReseeds} re-seed(s).");
            }
        }
        catch (OperationCanceledException)
        {
            // Logout or dispose: nothing to recover any more.
        }
        catch (Exception ex)
        {
            _log($"[Caps] seed recovery stopped on an error: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }
}
