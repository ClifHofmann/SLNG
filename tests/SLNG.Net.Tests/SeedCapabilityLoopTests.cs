using System.Net;
using System.Reflection;
using LibreMetaverse;
using Microsoft.Extensions.Logging;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>These replace LibreMetaverse's process-wide logger factory, which every
/// <see cref="GridSession"/> constructor also does, so they must not run beside the tests that build
/// sessions. A collection that disables parallelisation runs alone, after the parallel ones.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LmvGlobalLoggerCollection
{
    public const string Name = "LmvGlobalLogger";
}

/// <summary>
/// BUG-NET-31, against the library itself: the loop's shape, and the real <c>Caps</c> on a seed URL
/// that fails at once.
/// </summary>
[Collection(LmvGlobalLoggerCollection.Name)]
public class SeedCapabilityLoopTests
{
    private const string LmvRetryLine = "Seed capability returned no response. Trying again.";

    /// <summary>The shape of LibreMetaverse 3.1.6's <c>Caps.MakeSeedRequestAsync</c> /
    /// <c>SeedRequestCompleteHandler</c>, line for line where it matters: the handler is called from the
    /// <c>catch</c>, logs through the real <c>LibreMetaverse.Logger</c>, cancels its own token and calls
    /// the request method again, which then fails synchronously on the cancelled token.</summary>
    private sealed class LmvSeedLoopReplica
    {
        private readonly CancellationTokenSource _cts = new();
        public int Rounds;
        public int Depth;
        public int MaxDepth;
        public Task? FirstRequest;

        public void Start() => FirstRequest = MakeSeedRequestAsync();

        private async Task MakeSeedRequestAsync()
        {
            try
            {
                // A request that fails instantly, like one on an already-cancelled token.
                await Task.FromException(new HttpRequestException("connection refused"));
                Handler(null);
            }
            catch (Exception ex)
            {
                Handler(ex);
            }
        }

        private void Handler(Exception? error)
        {
            if (error == null) return;
            Rounds++;
            Depth++;
            MaxDepth = Math.Max(MaxDepth, Depth);
            try
            {
                // Real library, real logger: this is the call the SLNG filter sits behind.
                LibreMetaverse.Logger.Warn(LmvRetryLine);
                _cts.Cancel();
                // Past the cancel, every retry is a synchronous failure. Bounded here only so that a
                // regression fails this test rather than killing the test host with the real thing.
                if (Rounds < 3000) _ = MakeSeedRequestAsync();
            }
            finally
            {
                Depth--;
            }
        }
    }

    [Fact]
    public async Task The_librarys_retry_loop_ends_at_its_first_round_when_the_filter_aborts_it()
    {
        var oldLevel = LibreMetaverse.Settings.LogLevel;
        int hits = 0;
        try
        {
            LibreMetaverse.Settings.LogLevel = LogLevel.Warning;
            LibreMetaverse.Logger.SetLoggerFactory(
                LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning).AddProvider(new LibreMetaverseLogRelay("SLNG", (_, m) =>
                {
                    if (!SeedCapabilityGuard.IsRetryLine(m)) return true;
                    Interlocked.Increment(ref hits);
                    throw new SeedRequestAbortedException();
                }))),
                "SLNG");

            var loop = new LmvSeedLoopReplica();
            loop.Start();
            // The abort leaves the library's task faulted, which nobody observes; wait for it to settle.
            await Assert.ThrowsAnyAsync<Exception>(() => loop.FirstRequest!);

            Assert.Equal(1, loop.Rounds);
            Assert.Equal(1, hits);
            Assert.Equal(1, loop.MaxDepth);
        }
        finally
        {
            LibreMetaverse.Settings.LogLevel = oldLevel;
        }
    }

    // ----- the real library, end to end -----

    private static T Get<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;

    [Fact]
    public async Task A_seed_request_that_fails_at_once_no_longer_takes_the_process_down()
    {
        // Real GridSession, real LibreMetaverse Caps, a seed URL nothing listens on: the connection is
        // refused immediately, which is exactly the "instant failure" the log showed. Before the fix
        // this test host died with "Stack overflow."; now the library's loop is stopped at once and
        // the guard takes over (and is cancelled by Dispose before its first wait is up).
        using var session = new GridSession();
        var client = Get<GridClient>(session, "_client");

        // The library only issues the request when it believes it is connected.
        typeof(NetworkManager).GetProperty(nameof(NetworkManager.Connected))!
            .GetSetMethod(nonPublic: true)!.Invoke(client.Network, new object[] { true });
        var sim = new Simulator(client, new IPEndPoint(IPAddress.Loopback, 9000), 4242UL);

        sim.SetSeedCaps(new Uri("http://127.0.0.1:1/seed-that-nothing-serves"));

        var guard = session.SeedGuard!;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!guard.IsRecovering && DateTime.UtcNow < deadline) await Task.Delay(25);

        Assert.True(guard.IsRecovering, "the first failed seed request must start the recovery");
        Assert.NotNull(sim.Caps);
        Assert.Empty(sim.Caps!.Capabilities());
    }
}
