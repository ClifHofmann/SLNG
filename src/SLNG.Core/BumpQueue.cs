using System;
using System.Collections.Generic;

namespace SLNG.Core;

/// <summary>
/// FEAT-PERF-27: a queue that is ordered by priority (higher first, first-in first-out among equals) and
/// lets the items of one <b>focus subject</b> -- the avatar under the camera -- jump ahead of everything
/// else, however long they have been waiting behind it.
///
/// <para>Why not just a higher priority for the focus avatar: a priority is fixed when the item is queued,
/// and the focus changes later, while thousands of items of the new focus are already waiting. Here the
/// subject index is consulted when an item is taken, so a change of focus (<c>focusSource</c> returns
/// another id) re-ranks everything already queued without touching a single entry.</para>
///
/// <para>Starvation guard: after <c>maxConsecutiveBumps</c> items taken from the focus subject in a row, one
/// item is taken from the ordinary order, so a subject with a deep backlog cannot hold the rest back
/// indefinitely. 0 turns bumping off.</para>
///
/// <para>Structure: one heap for the whole queue and one heap per subject, both ordered by the same key.
/// An item taken through the subject heap stays in the main heap as a dead entry that is dropped when it
/// reaches the top; an item taken through the main heap is, by the shared order, the top of its own
/// subject's heap. Every operation is O(log n) under one lock -- taken briefly, from any thread.</para>
/// </summary>
public sealed class BumpQueue<T> where T : class
{
    private sealed class Entry
    {
        public required T Value;
        public Guid Subject;
        public bool Taken;
    }

    private readonly Func<Guid> _focusSource;
    private readonly int _maxConsecutiveBumps;
    private readonly object _lock = new();
    private readonly PriorityQueue<Entry, (float NegPriority, long Seq)> _all = new();
    private readonly Dictionary<Guid, PriorityQueue<Entry, (float NegPriority, long Seq)>> _bySubject = new();
    private long _seq;
    private int _count;
    private int _streak;
    private long _bumpedTotal;

    /// <param name="focusSource">The subject to serve first right now; <see cref="Guid.Empty"/> for none.
    /// Read on every <see cref="TryDequeue"/>, so it must be cheap.</param>
    public BumpQueue(Func<Guid> focusSource, int maxConsecutiveBumps = DefaultMaxConsecutiveBumps)
    {
        _focusSource = focusSource;
        _maxConsecutiveBumps = maxConsecutiveBumps;
    }

    /// <summary>Three focused items to one ordinary one: the focus subject gets three quarters of the
    /// throughput, the rest keeps a quarter.</summary>
    public const int DefaultMaxConsecutiveBumps = 3;

    public int Count
    {
        get { lock (_lock) return _count; }
    }

    /// <summary>Items that were served ahead of an older, ordinary item because of the focus.</summary>
    public long BumpedTotal
    {
        get { lock (_lock) return _bumpedTotal; }
    }

    /// <summary>Items of <paramref name="subject"/> still waiting.</summary>
    public int CountFor(Guid subject)
    {
        lock (_lock) return _bySubject.TryGetValue(subject, out var heap) ? heap.Count : 0;
    }

    public void Enqueue(T item, Guid subject = default, float priority = 0f)
    {
        lock (_lock)
        {
            var key = (-priority, _seq++);
            var entry = new Entry { Value = item, Subject = subject };
            _all.Enqueue(entry, key);
            if (subject != Guid.Empty)
            {
                if (!_bySubject.TryGetValue(subject, out var heap))
                    _bySubject[subject] = heap = new PriorityQueue<Entry, (float, long)>();
                heap.Enqueue(entry, key);
            }
            _count++;
        }
    }

    public bool TryDequeue(out T item)
    {
        Guid focus = _focusSource();
        lock (_lock)
        {
            if (_count == 0)
            {
                item = null!;
                return false;
            }

            if (focus != Guid.Empty && _maxConsecutiveBumps > 0 && _streak < _maxConsecutiveBumps
                && _bySubject.TryGetValue(focus, out var heap) && heap.TryDequeue(out var focused, out _))
            {
                if (heap.Count == 0) _bySubject.Remove(focus);
                // A jump is only a jump when something ordinary was waiting in front of it.
                if (!ReferenceEquals(PeekLive(), focused)) _bumpedTotal++;
                focused.Taken = true;
                _streak++;
                _count--;
                item = focused.Value;
                return true;
            }

            var next = PeekLive();
            if (next == null)
            {
                item = null!;
                return false;
            }
            _all.Dequeue();
            next.Taken = true;
            _streak = 0;
            _count--;
            if (next.Subject != Guid.Empty && _bySubject.TryGetValue(next.Subject, out var own))
            {
                // By the shared order this is the top of its subject's heap.
                own.TryDequeue(out _, out _);
                if (own.Count == 0) _bySubject.Remove(next.Subject);
            }
            item = next.Value;
            return true;
        }
    }

    /// <summary>The first entry of the main heap that has not been taken through its subject; dead
    /// entries on top are dropped on the way. Caller holds the lock.</summary>
    private Entry? PeekLive()
    {
        while (_all.TryPeek(out var top, out _))
        {
            if (!top.Taken) return top;
            _all.Dequeue();
        }
        return null;
    }
}
