using System.Collections.Concurrent;
using System.Diagnostics;
using LibreMetaverse;
using SLNG.Core;
using SLNG.Net.ObjectCache;

namespace SLNG.Net;

// GridSession, optimistic restore from the object cache. FEAT-NET-04, phase 3.
//
// A simulator that probes tells us, object by object, which of the ones we hold it would send, and
// the CRC says whether they are still good. Measured on Second Life (Agni) while the handshake
// still answered "cache empty" as its first reply: no probe ever arrived (cached=0 on every
// arrival). Whatever the reason -- a simulator that does not probe, or one that acts on the first
// reply -- a region that does not probe cannot be checked object by object. The cache is used on
// trust instead, and the trust is then checked:
//
//   1. no probe within a few seconds of the handshake -> show what is held, nearest first;
//   2. ask the simulator for every one of those objects (it answers with the full state);
//   3. once its answers have stopped, what it never answered for no longer exists -> take it out.
//
// What the user sees is the region as it was, at once, brought up to date while they look at it.
public sealed partial class GridSession
{
    /// <summary>How long a simulator gets to start probing before the cache is used on trust.</summary>
    private static readonly TimeSpan ProbeGrace = TimeSpan.FromSeconds(2);

    private const int RestoreChunkSize = 100;
    private const int SilentPolls = 8;
    private static readonly TimeSpan RestoreMinimumWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RestoreGiveUp = TimeSpan.FromSeconds(180);

    private readonly HashSet<ulong> _probesSeen = new();

    /// <summary>Per region being restored: completed once what the cache holds has been shown, so the
    /// re-request of objects known at departure (BUG-NET-21) asks only for what the cache could not
    /// give instead of for everything while the cache is still being replayed.</summary>
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource> _restoreReplayed = new();

    /// <summary>Per region being verified: the local ids the simulator has shown a sign of life for.</summary>
    private readonly ConcurrentDictionary<ulong, ConcurrentDictionary<uint, byte>> _answered = new();

    /// <summary>Set while cached blocks are being handed to LibreMetaverse: the updates that raises
    /// are ours, not the simulator's, and must not count as it answering.</summary>
    [ThreadStatic]
    private static bool t_replaying;

    /// <summary>A new arrival in a region starts from nothing. Measured on Second Life: the simulator
    /// probed everything on a first visit and sent not one probe when the same region was entered
    /// again (it holds back what it sent before) -- so "probes were seen" is a fact about THIS
    /// arrival, not about the region.</summary>
    internal void ForgetProbesForArrival(ulong regionHandle)
    {
        lock (_probesSeen) _probesSeen.Remove(regionHandle);
    }

    internal void NoteProbeSeen(ulong regionHandle)
    {
        lock (_probesSeen) _probesSeen.Add(regionHandle);
    }

    internal bool ProbeSeen(ulong regionHandle)
    {
        lock (_probesSeen) return _probesSeen.Contains(regionHandle);
    }

    /// <summary>Called for every object update the pipeline raises: an object the simulator has just
    /// described is an object it still has.</summary>
    private void NoteObjectAnswered(ulong regionHandle, uint localId)
    {
        if (t_replaying) return;
        if (_answered.TryGetValue(regionHandle, out var answered)) answered[localId] = 0;
    }

    private void ScheduleRestoreFromCache(Simulator sim, RegionKey key)
    {
        if (_objectCache.IsEmpty(key)) return;
        _restoreReplayed[sim.Handle] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(() => RestoreFromCacheAsync(sim, key));
    }

    /// <summary>Waits until the cache has been shown (or there was nothing to show). Bounded: the
    /// caller must not hang on a restore that never finishes.</summary>
    private async Task WaitForRestoreToBeShown(ulong regionHandle)
    {
        if (_restoreReplayed.TryGetValue(regionHandle, out var shown))
            await Task.WhenAny(shown.Task, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(false);
    }

    private async Task RestoreFromCacheAsync(Simulator sim, RegionKey key)
    {
        try
        {
            await Task.Delay(ProbeGrace).ConfigureAwait(false);
            if (!StillHere(sim)) return;
            if (ProbeSeen(sim.Handle)) return; // the simulator is probing: the protocol decides
            if (ReplayBroken) return;

            var here = _client.Self.SimPosition;
            var candidates = ObjectRestorePlan.NearestFirst(
                _objectCache.Snapshot(key).Where(o => !sim.ObjectsPrimitives.ContainsKey(o.LocalId)),
                here.X, here.Y, here.Z);
            if (candidates.Count == 0) return;

            Console.WriteLine($"[ObjectCache] {sim.Name} ({sim.Handle}) has not probed: showing {candidates.Count} " +
                              "objects from the cache and checking them with the simulator");

            var answered = _answered.GetOrAdd(sim.Handle, _ => new ConcurrentDictionary<uint, byte>());
            answered.Clear();

            var shown = new List<uint>(candidates.Count);
            for (int i = 0; i < candidates.Count; i += RestoreChunkSize)
            {
                if (!StillHere(sim)) return;
                var blocks = new List<LibreMetaverse.Packets.ObjectUpdateCompressedPacket.ObjectDataBlock>();
                for (int j = i; j < Math.Min(i + RestoreChunkSize, candidates.Count); j++)
                {
                    blocks.Add(new LibreMetaverse.Packets.ObjectUpdateCompressedPacket.ObjectDataBlock
                    {
                        UpdateFlags = candidates[j].UpdateFlags,
                        Data = candidates[j].Block,
                    });
                    shown.Add(candidates[j].LocalId);
                }
                Replay(sim, blocks);
                await Task.Delay(10).ConfigureAwait(false);
            }

            foreach (var chunk in ObjectRecoveryPlan.Chunk(shown.ToArray(), RestoreChunkSize))
            {
                if (!StillHere(sim)) return;
                _client.Objects.RequestObjects(sim, chunk.ToList());
                await Task.Delay(50).ConfigureAwait(false);
            }

            if (_restoreReplayed.TryGetValue(sim.Handle, out var replayed)) replayed.TrySetResult();
            await VerifyRestoredAsync(sim, shown, answered).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ObjectCache] {sim.Name}: restoring from the cache stopped: {ex.Message}");
        }
        finally
        {
            if (_restoreReplayed.TryGetValue(sim.Handle, out var done)) done.TrySetResult();
            _answered.TryRemove(sim.Handle, out _);
        }
    }

    /// <summary>Waits for the simulator's answers to stop, then takes out what it never answered for.
    /// Never on a timeout: a simulator still working through a long queue is slow, not silent.</summary>
    private async Task VerifyRestoredAsync(Simulator sim, List<uint> shown, ConcurrentDictionary<uint, byte> answered)
    {
        var counts = new List<int>();
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < RestoreGiveUp)
        {
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            if (!StillHere(sim)) return;
            counts.Add(answered.Count);
            if (clock.Elapsed >= RestoreMinimumWait && ObjectRecoveryPlan.HasSettled(counts, SilentPolls))
            {
                var gone = ObjectRestorePlan.Unanswered(shown, answered.Keys.ToHashSet());
                foreach (var id in gone)
                {
                    sim.ObjectsPrimitives.TryRemove(id, out _);
                    ObjectRemovedReceived?.Invoke(this, new ObjectRemovedEvent(sim.Handle, id));
                }
                Console.WriteLine($"[ObjectCache] {sim.Name}: {shown.Count - gone.Length} of {shown.Count} cached objects " +
                                  $"confirmed by the simulator after {clock.Elapsed.TotalSeconds:0}s, {gone.Length} gone and removed");
                return;
            }
        }
        Console.WriteLine($"[ObjectCache] {sim.Name}: the simulator was still answering after {RestoreGiveUp.TotalSeconds:0}s; " +
                          "nothing removed");
    }
}
