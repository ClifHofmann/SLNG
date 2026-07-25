using System.Collections.Concurrent;
using SLNG.Assets;
using Xunit;

namespace SLNG.Assets.Tests;

public class PriorityGateTests
{
    [Fact]
    public async Task AdmitsImmediately_WhileSlotsAreFree()
    {
        var gate = new PriorityGate(2);

        // Both should complete without anyone releasing.
        await gate.WaitAsync(1f);
        await gate.WaitAsync(1f);
    }

    [Fact]
    public async Task BlocksWhenExhausted_AndAdmitsOnRelease()
    {
        var gate = new PriorityGate(1);
        await gate.WaitAsync(1f);

        var blocked = gate.WaitAsync(1f);
        Assert.False(blocked.IsCompleted);

        gate.Release();
        await blocked; // must now be admitted
    }

    [Fact]
    public async Task AdmitsHighestPriorityFirst_RegardlessOfArrivalOrder()
    {
        var gate = new PriorityGate(1);
        await gate.WaitAsync(100f); // occupy the only slot

        // Enqueue low priority FIRST, then high -- arrival order is deliberately the opposite
        // of the expected admission order.
        var low = gate.WaitAsync(1f);
        var high = gate.WaitAsync(99f);

        gate.Release();
        await high; // the later-arriving, higher-priority waiter wins
        Assert.False(low.IsCompleted);

        gate.Release();
        await low;
    }

    [Fact]
    public async Task EqualPriorities_AreAdmittedInArrivalOrder()
    {
        var gate = new PriorityGate(1);
        await gate.WaitAsync(5f);

        var first = gate.WaitAsync(5f);
        var second = gate.WaitAsync(5f);

        gate.Release();
        await first;
        Assert.False(second.IsCompleted);

        gate.Release();
        await second;
    }

    [Fact]
    public async Task ConcurrentWaiters_NeverExceedSlotCount()
    {
        const int slots = 3;
        var gate = new PriorityGate(slots);
        int concurrent = 0;
        int peak = 0;
        var peakLock = new object();

        var workers = Enumerable.Range(0, 50).Select(async i =>
        {
            await gate.WaitAsync(i);
            try
            {
                int now = Interlocked.Increment(ref concurrent);
                lock (peakLock) { if (now > peak) peak = now; }
                await Task.Delay(1);
                Interlocked.Decrement(ref concurrent);
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();

        await Task.WhenAll(workers);
        Assert.True(peak <= slots, $"peak concurrency {peak} exceeded the {slots}-slot limit");
    }
}
