using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SLNG.Assets;

/// <summary>
/// A concurrency limiter like <see cref="SemaphoreSlim"/>, except waiters are admitted
/// **highest-priority-first** instead of first-come-first-served.
///
/// FEAT-PERF-02: a plain SemaphoreSlim admits in arrival order, so on first entering a region the
/// texture for a wall 200m away occupies one of the few fetch slots exactly as readily as the one
/// directly in front of the camera -- with a queue of hundreds of pending textures and only a
/// handful of slots, what you're looking at can sit behind an arbitrary amount of scenery you
/// aren't. Ordering by on-screen prominence means the visible foreground resolves first and the
/// background fills in behind it, which is what a real viewer does and what the eye expects.
///
/// Priority is supplied per-<see cref="WaitAsync"/> call (higher = admitted sooner) and is
/// **captured at call time, never re-evaluated**: a request enqueued while its object was distant
/// keeps that low priority even if the camera later moves right up to it. In practice the common
/// case still works out -- moving/zooming somewhere new makes that area's objects issue *fresh*
/// requests, which enter the queue with correctly-high priorities and overtake the stale
/// background ones -- but a texture that was already queued when it was far away does not get
/// re-prioritized. Fixing that properly needs cancel-and-requeue on camera movement; deliberately
/// out of scope here (see the FEAT-PERF-02 spec).
/// </summary>
internal sealed class PriorityGate
{
    private readonly object _lock = new();
    // Min-heap by negated priority == max-heap by priority. The long tiebreaker keeps admission
    // FIFO among equal priorities (PriorityQueue itself is not stable) so a burst of same-priority
    // requests can't starve each other arbitrarily.
    private readonly PriorityQueue<TaskCompletionSource<bool>, (float NegPriority, long Seq)> _waiters = new();
    private long _seq;
    private int _availableSlots;

    /// <summary>Total slot count this gate was constructed with (not the current available
    /// count, which fluctuates) -- for diagnostics/logging only.</summary>
    public int Capacity { get; }

    public PriorityGate(int slots)
    {
        if (slots <= 0) throw new ArgumentOutOfRangeException(nameof(slots));
        Capacity = slots;
        _availableSlots = slots;
    }

    /// <summary>Waits for a slot. Among queued waiters the one with the highest
    /// <paramref name="priority"/> is admitted next.</summary>
    public Task WaitAsync(float priority)
    {
        lock (_lock)
        {
            if (_availableSlots > 0)
            {
                _availableSlots--;
                return Task.CompletedTask;
            }

            // RunContinuationsAsynchronously: without it, the continuation of whichever waiter we
            // admit would run inline on the thread calling Release() -- i.e. a fetch's completion
            // path would synchronously execute the next fetch's request code.
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Enqueue(tcs, (-priority, _seq++));
            return tcs.Task;
        }
    }

    /// <summary>Returns a slot, admitting the highest-priority queued waiter (if any).</summary>
    public void Release()
    {
        TaskCompletionSource<bool>? next = null;
        lock (_lock)
        {
            if (_waiters.TryDequeue(out var waiter, out _))
            {
                next = waiter; // hand the slot straight over -- don't return it to the pool
            }
            else
            {
                _availableSlots++;
            }
        }

        next?.TrySetResult(true);
    }
}
