using System;
using System.Collections.Generic;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

public class BumpQueueTests
{
    private sealed class Job
    {
        public Job(string name) { Name = name; }
        public string Name { get; }
    }

    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    private static List<string> Drain(BumpQueue<Job> q)
    {
        var order = new List<string>();
        while (q.TryDequeue(out var j)) order.Add(j.Name);
        return order;
    }

    [Fact]
    public void NoFocus_IsFifo()
    {
        var q = new BumpQueue<Job>(() => Guid.Empty);
        q.Enqueue(new Job("1"), A);
        q.Enqueue(new Job("2"));
        q.Enqueue(new Job("3"), B);

        Assert.Equal(new[] { "1", "2", "3" }, Drain(q));
        Assert.Equal(0, q.Count);
    }

    [Fact]
    public void Priority_OrdersHighestFirst_FifoAmongEquals()
    {
        var q = new BumpQueue<Job>(() => Guid.Empty);
        q.Enqueue(new Job("low"), priority: 1f);
        q.Enqueue(new Job("high1"), priority: 9f);
        q.Enqueue(new Job("high2"), priority: 9f);

        Assert.Equal(new[] { "high1", "high2", "low" }, Drain(q));
    }

    [Fact]
    public void FocusSubject_ComesOutBeforeEarlierUnfocusedItems()
    {
        Guid focus = A;
        var q = new BumpQueue<Job>(() => focus, maxConsecutiveBumps: 100);
        for (int i = 0; i < 5; i++) q.Enqueue(new Job("other" + i), B);
        q.Enqueue(new Job("focus0"), A);
        q.Enqueue(new Job("focus1"), A);

        var order = Drain(q);

        Assert.Equal(new[] { "focus0", "focus1", "other0", "other1", "other2", "other3", "other4" }, order);
    }

    [Fact]
    public void FocusSubject_KeepsItsOwnOrder()
    {
        var q = new BumpQueue<Job>(() => A, maxConsecutiveBumps: 100);
        q.Enqueue(new Job("x"), B);
        q.Enqueue(new Job("a-low"), A, priority: 1f);
        q.Enqueue(new Job("a-high"), A, priority: 5f);

        Assert.Equal(new[] { "a-high", "a-low", "x" }, Drain(q));
    }

    [Fact]
    public void BumpLimit_LetsOneRegularItemThroughAfterNBumps()
    {
        var q = new BumpQueue<Job>(() => A, maxConsecutiveBumps: 2);
        for (int i = 0; i < 4; i++) q.Enqueue(new Job("other" + i), B);
        for (int i = 0; i < 5; i++) q.Enqueue(new Job("focus" + i), A);

        // 2 focused, 1 regular, 2 focused, 1 regular, 1 focused, then the rest.
        Assert.Equal(
            new[] { "focus0", "focus1", "other0", "focus2", "focus3", "other1", "focus4", "other2", "other3" },
            Drain(q));
    }

    [Fact]
    public void BumpLimit_Zero_DisablesBumping()
    {
        var q = new BumpQueue<Job>(() => A, maxConsecutiveBumps: 0);
        q.Enqueue(new Job("other"), B);
        q.Enqueue(new Job("focus"), A);

        Assert.Equal(new[] { "other", "focus" }, Drain(q));
    }

    [Fact]
    public void FocusChange_RerranksItemsAlreadyQueued()
    {
        Guid focus = Guid.Empty;
        var q = new BumpQueue<Job>(() => focus, maxConsecutiveBumps: 100);
        q.Enqueue(new Job("a0"), A);
        q.Enqueue(new Job("b0"), B);
        q.Enqueue(new Job("a1"), A);
        q.Enqueue(new Job("b1"), B);

        Assert.True(q.TryDequeue(out var first));
        Assert.Equal("a0", first.Name); // nobody focused yet

        focus = B; // the focus changes while items wait
        Assert.Equal(new[] { "b0", "b1", "a1" }, Drain(q));
    }

    [Fact]
    public void Count_StaysExactWhenItemsLeaveThroughEitherPath()
    {
        Guid focus = Guid.Empty;
        var q = new BumpQueue<Job>(() => focus, maxConsecutiveBumps: 100);
        q.Enqueue(new Job("a0"), A);
        q.Enqueue(new Job("b0"), B);
        q.Enqueue(new Job("a1"), A);
        Assert.Equal(3, q.Count);

        focus = A;
        Assert.True(q.TryDequeue(out _));   // bumped
        Assert.Equal(2, q.Count);
        focus = Guid.Empty;
        Assert.True(q.TryDequeue(out var next)); // fifo head: a0 was already taken, so b0
        Assert.Equal("b0", next.Name);
        Assert.Equal(1, q.Count);
        Assert.True(q.TryDequeue(out var last));
        Assert.Equal("a1", last.Name);
        Assert.False(q.TryDequeue(out _));
        Assert.Equal(0, q.Count);
    }

    [Fact]
    public void FocusWithNothingQueued_FallsBackToFifo()
    {
        var q = new BumpQueue<Job>(() => A);
        q.Enqueue(new Job("b0"), B);
        q.Enqueue(new Job("b1"), B);

        Assert.Equal(new[] { "b0", "b1" }, Drain(q));
    }

    [Fact]
    public void BumpedTotal_CountsOnlyItemsThatJumpedTheQueue()
    {
        var q = new BumpQueue<Job>(() => A, maxConsecutiveBumps: 100);
        q.Enqueue(new Job("a0"), A);      // already first: not a jump
        q.Enqueue(new Job("b0"), B);
        q.Enqueue(new Job("a1"), A);      // jumps b0

        Drain(q);

        Assert.Equal(1, q.BumpedTotal);
    }

    [Fact]
    public void CountFor_IsTheWaitingItemsOfOneSubject()
    {
        Guid focus = Guid.Empty;
        var q = new BumpQueue<Job>(() => focus, maxConsecutiveBumps: 100);
        q.Enqueue(new Job("a0"), A);
        q.Enqueue(new Job("b0"), B);
        q.Enqueue(new Job("a1"), A);
        Assert.Equal(2, q.CountFor(A));
        Assert.Equal(1, q.CountFor(B));
        Assert.Equal(0, q.CountFor(Guid.Parse("00000000-0000-0000-0000-0000000000cc")));

        Assert.True(q.TryDequeue(out _));   // a0 leaves through the ordinary order
        Assert.Equal(1, q.CountFor(A));

        focus = B;
        Assert.True(q.TryDequeue(out _));   // b0 leaves through the subject path
        Assert.Equal(0, q.CountFor(B));
        Assert.Equal(1, q.CountFor(A));
    }
}
