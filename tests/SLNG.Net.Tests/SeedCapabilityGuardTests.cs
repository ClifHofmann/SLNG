using System.Net;
using System.Reflection;
using LibreMetaverse;
using Microsoft.Extensions.Logging;
using SLNG.Core;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>
/// BUG-NET-31: LibreMetaverse 3.1.6 retries a failed seed-capability request by calling itself again
/// with no delay and no limit, after cancelling the token the retry uses, so every retry fails at once
/// and one more stack frame piles up until the process dies ("Stack overflow." after ~780 lines of
/// "Seed capability returned no response. Trying again."). The guard stops that loop where it starts
/// and replaces the retry with a bounded, delayed one.
/// </summary>
public class SeedCapabilityGuardTests
{
    // The exact line from LibreMetaverse 3.1.6 Caps.cs:293, as the log showed it.
    private const string LmvRetryLine = "Seed capability returned no response. Trying again.";

    [Theory]
    [InlineData(LmvRetryLine, true)]
    [InlineData("Seed capability returned Forbidden. Trying again.", true)]
    [InlineData("[Some Name] Seed capability returned no response. Trying again.", true)]
    // The 404 branch does not retry, so it must not be treated as the loop.
    [InlineData("Seed capability returned a 404, capability system is aborting", false)]
    [InlineData("Event queue for Foo returned no response", false)]
    [InlineData("", false)]
    public void Only_the_librarys_retry_line_is_recognised(string message, bool expected)
    {
        Assert.Equal(expected, SeedCapabilityGuard.IsRetryLine(message));
    }

    [Fact]
    public void The_waits_grow_and_stay_bounded()
    {
        var delays = Enumerable.Range(1, SeedCapabilityGuard.MaxReseeds).Select(SeedCapabilityGuard.DelayBefore).ToArray();

        Assert.Equal(delays.OrderBy(d => d), delays);
        Assert.All(delays, d => Assert.InRange(d.TotalSeconds, 1, 15));
        Assert.True(delays.Sum(d => d.TotalSeconds) < 60, "a half-working client must not sit there for a minute");
    }

    private sealed class Harness
    {
        public readonly List<string> Log = new();
        public int Reseeds;
        public int GiveUps;
        public readonly List<TimeSpan> Waits = new();
        public Func<int, SeedSnapshot> StateAfterReseeds = _ => new SeedSnapshot(true, true, 0);

        public SeedCapabilityGuard Guard { get; }

        public Harness()
        {
            Guard = new SeedCapabilityGuard(
                () => StateAfterReseeds(Reseeds),
                () => Interlocked.Increment(ref Reseeds),
                (d, ct) => { lock (Waits) Waits.Add(d); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; },
                m => { lock (Log) Log.Add(m); },
                () => Interlocked.Increment(ref GiveUps));
        }
    }

    [Fact]
    public async Task A_region_that_answers_after_a_reseed_is_left_alone_afterwards()
    {
        var h = new Harness { StateAfterReseeds = n => new SeedSnapshot(true, n < 2, 0) };

        Assert.True(h.Guard.OnSeedFailure("HttpRequestException: refused"));
        await h.Guard.Completion;

        Assert.Equal(2, h.Reseeds);
        Assert.Equal(0, h.GiveUps);
        Assert.False(h.Guard.IsRecovering);
        Assert.Contains(h.Log, m => m.Contains("HttpRequestException: refused"));
        Assert.Contains(h.Log, m => m.Contains("answered after 2 re-seed"));
    }

    [Fact]
    public async Task A_region_that_never_answers_ends_the_session_after_the_bounded_number_of_tries()
    {
        var h = new Harness();

        h.Guard.OnSeedFailure(null);
        await h.Guard.Completion;

        Assert.Equal(SeedCapabilityGuard.MaxReseeds, h.Reseeds);
        Assert.Equal(1, h.GiveUps);
        // One wait before each re-seed, and one more to give the last re-seed its chance.
        Assert.Equal(SeedCapabilityGuard.MaxReseeds + 1, h.Waits.Count);
        Assert.Contains(h.Log, m => m.Contains("ending the session"));
    }

    [Fact]
    public async Task A_neighbour_that_never_answers_is_not_worth_ending_the_session()
    {
        var h = new Harness { StateAfterReseeds = _ => new SeedSnapshot(true, false, 1) };

        h.Guard.OnSeedFailure(null);
        await h.Guard.Completion;

        Assert.Equal(SeedCapabilityGuard.MaxReseeds, h.Reseeds);
        Assert.Equal(0, h.GiveUps);
        Assert.Contains(h.Log, m => m.Contains("neighbouring region"));
    }

    [Fact]
    public async Task Nothing_is_retried_once_the_session_is_gone()
    {
        var h = new Harness { StateAfterReseeds = _ => new SeedSnapshot(false, false, 0) };

        h.Guard.OnSeedFailure(null);
        await h.Guard.Completion;

        Assert.Equal(0, h.Reseeds);
        Assert.Equal(0, h.GiveUps);
    }

    [Fact]
    public async Task A_failure_that_was_not_the_current_regions_changes_nothing()
    {
        // The current region already has its capabilities: the failure came from a neighbour or from
        // something that reacts to the capabilities arriving.
        var h = new Harness { StateAfterReseeds = _ => new SeedSnapshot(true, false, 0) };

        h.Guard.OnSeedFailure(null);
        await h.Guard.Completion;

        Assert.Equal(0, h.Reseeds);
        Assert.Equal(0, h.GiveUps);
    }

    [Fact]
    public async Task Further_failures_while_recovering_do_not_start_a_second_recovery()
    {
        var gate = new TaskCompletionSource();
        int reseeds = 0;
        var guard = new SeedCapabilityGuard(
            () => new SeedSnapshot(true, reseeds < 1, 0),
            () => reseeds++,
            async (_, _) => await gate.Task,
            _ => { },
            () => { });

        Assert.True(guard.OnSeedFailure(null));
        Assert.True(guard.IsRecovering);
        Assert.False(guard.OnSeedFailure(null));
        Assert.False(guard.OnSeedFailure(null));

        gate.SetResult();
        await guard.Completion;

        Assert.False(guard.IsRecovering);
        Assert.True(guard.OnSeedFailure(null), "a later failure (a teleport's region) starts its own recovery");
        await guard.Completion;
    }

    [Fact]
    public async Task Cancelling_stops_a_recovery_without_giving_up()
    {
        var gate = new TaskCompletionSource();
        int giveUps = 0;
        var guard = new SeedCapabilityGuard(
            () => new SeedSnapshot(true, true, 0),
            () => { },
            async (_, ct) => await gate.Task.WaitAsync(ct),
            _ => { },
            () => giveUps++);

        guard.OnSeedFailure(null);
        guard.Cancel();
        await guard.Completion;

        Assert.Equal(0, giveUps);
        Assert.False(guard.IsRecovering);
    }

    [Fact]
    public async Task A_failing_reseed_costs_an_attempt_not_the_recovery()
    {
        int attempts = 0;
        int giveUps = 0;
        var guard = new SeedCapabilityGuard(
            () => new SeedSnapshot(true, true, 0),
            () => { attempts++; throw new InvalidOperationException("boom"); },
            (_, _) => Task.CompletedTask,
            _ => { },
            () => giveUps++);

        guard.OnSeedFailure(null);
        await guard.Completion;

        Assert.Equal(SeedCapabilityGuard.MaxReseeds, attempts);
        Assert.Equal(1, giveUps);
    }

    // ----- the part that actually stops the stack overflow -----

    private static ILogger LmvStyleLogger(Func<LogLevel, string, bool> filter) =>
        LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning).AddProvider(new LibreMetaverseLogRelay("SLNG", filter)))
            .CreateLogger("test");

    [Fact]
    public void The_abort_gets_through_the_logging_stack_to_the_caller()
    {
        // Microsoft.Extensions.Logging re-throws what a provider throws, as an AggregateException.
        var log = LmvStyleLogger((_, _) => throw new SeedRequestAbortedException());

        var thrown = Assert.ThrowsAny<Exception>(() => log.LogWarning("{Message}", LmvRetryLine));

        Assert.Contains(Flatten(thrown), e => e is SeedRequestAbortedException);
    }

    [Fact]
    public void Any_other_filter_failure_still_costs_nothing()
    {
        var log = LmvStyleLogger((_, _) => throw new InvalidOperationException("a broken filter"));

        log.LogWarning("{Message}", "some warning"); // must not throw
    }

    private static IEnumerable<Exception> Flatten(Exception e)
    {
        yield return e;
        if (e is AggregateException agg)
            foreach (var inner in agg.InnerExceptions.SelectMany(Flatten)) yield return inner;
        else if (e.InnerException != null)
            foreach (var inner in Flatten(e.InnerException)) yield return inner;
    }

    [Fact]
    public void The_cause_of_a_failure_is_named_when_it_is_known()
    {
        LastExceptionTracker.Start();
        try
        {
            try { throw new HttpRequestException("No connection could be made"); }
            catch (HttpRequestException) { }

            var described = LastExceptionTracker.DescribeLast();
            Assert.NotNull(described);
            Assert.StartsWith("HttpRequestException: No connection", described);

            // Our own control flow is never reported as the cause.
            try { throw new SeedRequestAbortedException(); }
            catch (SeedRequestAbortedException) { }
            Assert.Null(LastExceptionTracker.DescribeLast());
        }
        finally
        {
            LastExceptionTracker.Stop();
        }
    }

    [Fact]
    public void A_capabilities_unavailable_session_end_has_its_own_reason()
    {
        Assert.NotEqual(SessionEndReason.EventQueueDead, SessionEndReason.CapabilitiesUnavailable);
        Assert.True(Enum.IsDefined(SessionEndReason.CapabilitiesUnavailable));
    }
}
