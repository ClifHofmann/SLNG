using System.Diagnostics;
using LibreMetaverse;

namespace SLNG.Net;

// GridSession, object recovery after returning to a region. BUG-NET-21; the reasoning is on
// ObjectRecoveryPlan.
//
// What this must not do: ask a region for things it is about to send anyway. So it looks at what
// the simulator has started to deliver. If that is almost all new to us, the simulator is holding
// back what it sent before, and the ask goes out at once, nearest first. If it is mostly old, the
// simulator is starting over, and the ask waits for its stream to go quiet and covers only the gaps.
public sealed partial class GridSession
{
    /// <summary>How many regions' worth of ids are kept. A return is to the region just left, or
    /// the one before it; more would only hold ids of regions nobody goes back to.</summary>
    private const int MaxRemembered = 4;

    private const int RecoveryChunkSize = 100;
    private const int SettledPolls = 5;
    private static readonly TimeSpan RecoveryPoll = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RecoveryFirstLook = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RecoveryGiveUp = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan RecoveryReport = TimeSpan.FromSeconds(25);

    private readonly record struct RememberedObject(uint LocalId, Vector3 RootPosition);

    private readonly object _departureLock = new();
    private readonly Dictionary<ulong, RememberedObject[]> _objectsAtDeparture = new();
    private readonly List<ulong> _departureOrder = new();

    /// <summary>Records which objects we held for a region as we leave it, with where their
    /// root sits so the ask can go nearest-first from wherever we land. Runs from
    /// <c>SimDisconnected</c>, which LibreMetaverse raises before it forgets the region's objects.</summary>
    private void RememberObjectsAtDeparture(Simulator sim)
    {
        try
        {
            var objects = sim.ObjectsPrimitives.Values
                .Where(p => IsWorldObject(sim, p))
                .Select(p => new RememberedObject(p.LocalID, RootOf(sim, p).Position))
                .ToArray();
            if (objects.Length == 0) return;

            lock (_departureLock)
            {
                _objectsAtDeparture[sim.Handle] = objects;
                _departureOrder.Remove(sim.Handle);
                _departureOrder.Add(sim.Handle);
                while (_departureOrder.Count > MaxRemembered)
                {
                    _objectsAtDeparture.Remove(_departureOrder[0]);
                    _departureOrder.RemoveAt(0);
                }
            }
        }
        catch (Exception ex)
        {
            // Bookkeeping for a recovery; losing it must never take the disconnect down with it.
            Console.WriteLine($"[Rerequest] could not record {sim.Name}'s objects: {ex.Message}");
        }
    }

    /// <summary>A world object as opposed to something an avatar wears: a worn attachment, and
    /// every child prim of one, is sent again with its avatar whatever the simulator remembers.</summary>
    private static bool IsWorldObject(Simulator sim, Primitive p)
    {
        if (IsUnpopulatedPrimitive(p)) return false;
        if (p.PrimData.AttachmentPoint != AttachmentPoint.Default) return false;
        if (p.ParentID == 0) return true;
        if (sim.ObjectsAvatars.ContainsKey(p.ParentID)) return false;
        return !(sim.ObjectsPrimitives.TryGetValue(p.ParentID, out var parent)
                 && parent.PrimData.AttachmentPoint != AttachmentPoint.Default);
    }

    private static Primitive RootOf(Simulator sim, Primitive p)
        => p.ParentID != 0 && sim.ObjectsPrimitives.TryGetValue(p.ParentID, out var parent) ? parent : p;

    /// <summary>Called when a region becomes the current one. If it is one we left and remember,
    /// works out whether the simulator will resend what it sent before, and asks for the rest.</summary>
    private void ScheduleObjectRecovery(Simulator sim)
    {
        RememberedObject[]? known;
        lock (_departureLock)
        {
            if (!_objectsAtDeparture.Remove(sim.Handle, out known)) return;
            _departureOrder.Remove(sim.Handle);
        }
        _ = Task.Run(() => RecoverObjectsAsync(sim, known));
    }

    private async Task RecoverObjectsAsync(Simulator sim, RememberedObject[] known)
    {
        try
        {
            // If the cache is being shown, let it finish: what it gives needs no asking for.
            await WaitForRestoreToBeShown(sim.Handle).ConfigureAwait(false);
            if (!StillHere(sim)) return;

            var knownIds = known.Select(k => k.LocalId).ToHashSet();
            var counts = new List<int>();
            var clock = Stopwatch.StartNew();
            string why = "the simulator's stream went quiet";
            while (clock.Elapsed < RecoveryGiveUp)
            {
                await Task.Delay(RecoveryPoll).ConfigureAwait(false);
                if (!StillHere(sim)) return; // left again before it mattered
                counts.Add(sim.ObjectsPrimitives.Count);
                if (clock.Elapsed < RecoveryFirstLook) continue;

                if (ObjectRecoveryPlan.IsWithholding(sim.ObjectsPrimitives.Keys.ToList(), knownIds))
                {
                    why = "it is not resending what it sent before";
                    break;
                }
                if (ObjectRecoveryPlan.HasSettled(counts, SettledPolls)) break;
            }

            // Nearest first, from wherever we are standing now: the simulator answers in the order
            // asked, and the near objects are the ones on screen.
            var here = _client.Self.SimPosition;
            var inOrder = known.OrderBy(k => Vector3.Distance(k.RootPosition, here)).Select(k => k.LocalId);
            var missing = ObjectRecoveryPlan.MissingIds(inOrder, sim.ObjectsPrimitives.Keys.ToHashSet());
            Console.WriteLine($"[Rerequest] {sim.Name} ({sim.Handle}): {known.Length} objects known when we left, " +
                              $"{sim.ObjectsPrimitives.Count} in place after {clock.Elapsed.TotalSeconds:0}s, " +
                              $"asking for {missing.Length} ({why})");
            if (missing.Length == 0) return;

            foreach (var chunk in ObjectRecoveryPlan.Chunk(missing, RecoveryChunkSize))
            {
                if (!StillHere(sim)) return;
                _client.Objects.RequestObjects(sim, chunk.ToList());
                await Task.Delay(50).ConfigureAwait(false);
            }

            await Task.Delay(RecoveryReport).ConfigureAwait(false);
            if (!StillHere(sim)) return;
            int stillMissing = ObjectRecoveryPlan.MissingIds(missing, sim.ObjectsPrimitives.Keys.ToHashSet()).Length;
            Console.WriteLine($"[Rerequest] {sim.Name}: {missing.Length - stillMissing} of {missing.Length} answered " +
                              $"({stillMissing} gone or unanswered), {sim.ObjectsPrimitives.Count} objects in place");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Rerequest] {sim.Name}: stopped: {ex.Message}");
        }
    }

    private bool StillHere(Simulator sim)
        => _client.Network.Connected && sim.Connected && sim == _client.Network.CurrentSim;
}
