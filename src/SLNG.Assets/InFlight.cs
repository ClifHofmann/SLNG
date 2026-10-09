using System.Collections.Concurrent;

namespace SLNG.Assets;

/// <summary>
/// One run per key at a time, shared by everyone who asks while it is in flight -- and never a
/// finished run handed out as if it were still going (BUG-ASSET-01).
///
/// <para>The shape this replaces was <c>map.GetOrAdd(key, async k =&gt; { try { ... } finally
/// { map.TryRemove(k, out _); } })</c>. An async factory that finishes without ever yielding (a
/// "not connected" early-out, a cache that answers at once, a fetch that fails on the spot) runs its
/// <c>finally</c> <em>before</em> <c>GetOrAdd</c> has stored the task it returns. The removal finds
/// nothing, the finished task is stored afterwards, and from then on every request for that key is
/// answered with the old result, for as long as the process lives: a material asked for before
/// login stayed <c>null</c> for good, and a stand-in animation became a permanent T-pose.</para>
///
/// <para>Here the entry is taken out by the task's own completion, removing exactly that task and
/// nothing newer, and a finished entry found on the way in is dropped.</para>
///
/// <para>BUG-PERF-10: the factory never runs on the CALLER's thread. An async method runs inline until its
/// first await that does not complete at once, and the first awaits of an asset fetch are a file
/// existence check and a read of a file the OS already holds in memory -- which complete at once. A
/// warm start asks for thousands of cached meshes from the Godot main thread, and every one of those
/// requests paid the file work (3.4 ms each, measured) inside the frame budget before this hopped to a
/// pool thread. Starting the factory on the pool costs microseconds and moves all of that away.</para>
/// </summary>
internal static class InFlight
{
    public static Task<T> GetOrStart<TKey, T>(
        ConcurrentDictionary<TKey, Task<T>> map, TKey key, Func<TKey, Task<T>> start)
        where TKey : notnull
    {
        var pairs = (ICollection<KeyValuePair<TKey, Task<T>>>)map;

        // A finished task is an answer to an earlier request, not a request in flight.
        if (map.TryGetValue(key, out var finished) && finished.IsCompleted)
            pairs.Remove(new KeyValuePair<TKey, Task<T>>(key, finished));

        var task = map.GetOrAdd(key, k => Task.Run(() => start(k)));

        // Removed by the task itself once it is done; by the time this runs GetOrAdd has stored it,
        // whichever way the factory finished. Equality is on the task, so a newer run is never hit.
        _ = task.ContinueWith(
            t => pairs.Remove(new KeyValuePair<TKey, Task<T>>(key, t)),
            TaskScheduler.Default);
        return task;
    }
}
