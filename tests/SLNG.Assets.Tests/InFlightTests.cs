using System.Collections.Concurrent;
using Xunit;

namespace SLNG.Assets.Tests;

// BUG-ASSET-01. A finished run must never be handed out again. The old shape removed the entry inside
// the factory, before GetOrAdd had stored the task, whenever the factory finished without yielding --
// a factory that returns an already finished task is exactly that case.
public class InFlightTests
{
    private static ConcurrentDictionary<int, Task<string>> Map() => new();

    [Fact]
    public async Task A_run_that_finished_without_ever_yielding_is_not_handed_out_again()
    {
        var map = Map();
        int runs = 0;
        Task<string> Start() => InFlight.GetOrStart(map, 1, _ =>
        {
            runs++;
            // Not connected the first time, connected the next: finished before GetOrAdd returns.
            return Task.FromResult(runs == 1 ? "nothing" : "answer");
        });

        Assert.Equal("nothing", await Start());
        Assert.Equal("answer", await Start());   // the old shape answered "nothing" here, for good
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task Callers_that_ask_while_a_run_is_in_flight_share_it()
    {
        var map = Map();
        var gate = new TaskCompletionSource<string>();
        int runs = 0;

        var first = InFlight.GetOrStart(map, 7, _ => { runs++; return gate.Task; });
        var second = InFlight.GetOrStart(map, 7, _ => { runs++; return gate.Task; });
        gate.SetResult("done");

        Assert.Equal("done", await first);
        Assert.Equal("done", await second);
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task A_run_that_finished_after_waiting_is_not_handed_out_again_either()
    {
        var map = Map();
        int runs = 0;
        Task<string> Start() => InFlight.GetOrStart(map, 3, async _ =>
        {
            await Task.Delay(5);
            return $"run {++runs}";
        });

        Assert.Equal("run 1", await Start());
        Assert.Equal("run 2", await Start());
    }

    [Fact]
    public async Task A_run_that_failed_is_asked_again()
    {
        var map = Map();
        int runs = 0;
        Task<string> Start() => InFlight.GetOrStart(map, 4, async _ =>
        {
            runs++;
            await Task.Yield();
            if (runs == 1) throw new InvalidOperationException("the grid said no");
            return "answer";
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => Start());
        Assert.Equal("answer", await Start());
    }

    // BUG-PERF-10. The caller is the Godot main thread, and a factory that "starts" with a file check
    // and a read of a file the OS already holds finishes that part before it ever yields. If it ran
    // inline, that would be the caller's frame time. It must run on another thread, and the call must
    // come back without waiting for it.
    [Fact]
    public async Task The_factory_runs_on_another_thread_and_the_caller_does_not_wait_for_it()
    {
        var map = Map();
        using var release = new ManualResetEventSlim(false);
        int factoryThread = -1;
        int callerThread = -1;
        Task<string>? task = null;

        var caller = new Thread(() =>
        {
            callerThread = Environment.CurrentManagedThreadId;
            task = InFlight.GetOrStart(map, 9, _ =>
            {
                factoryThread = Environment.CurrentManagedThreadId;
                release.Wait();                       // blocks like synchronous file work would
                return Task.FromResult("answer");
            });
        });
        caller.Start();
        Assert.True(caller.Join(TimeSpan.FromSeconds(5)), "GetOrStart waited for its factory");

        Assert.NotNull(task);
        Assert.False(task!.IsCompleted);
        release.Set();
        Assert.Equal("answer", await task);
        Assert.NotEqual(callerThread, factoryThread);
        Assert.NotEqual(-1, factoryThread);
    }

    [Fact]
    public async Task Different_keys_do_not_share_a_run()
    {
        var map = Map();

        var a = await InFlight.GetOrStart(map, 1, _ => Task.FromResult("one"));
        var b = await InFlight.GetOrStart(map, 2, _ => Task.FromResult("two"));

        Assert.Equal(("one", "two"), (a, b));
    }

    [Fact]
    public async Task The_map_does_not_grow_with_every_key_it_has_served()
    {
        var map = Map();
        for (int key = 0; key < 50; key++)
            await InFlight.GetOrStart(map, key, _ => Task.FromResult("x"));

        // Completion removes each entry a moment after the fact; give that moment.
        for (int i = 0; i < 100 && !map.IsEmpty; i++) await Task.Delay(10);

        Assert.True(map.IsEmpty);
    }
}
