namespace SLNG.Net;

/// <summary>
/// The arithmetic behind asking a region for the objects it will not volunteer (BUG-NET-21).
///
/// <para>Leave a region and come back, and the simulator streams only what it had not sent before
/// -- measured live: 3232 objects on the return against 5439 on a fresh arrival, which is the
/// 2287 the first visit had delivered, taken away. We throw a region's objects out when we
/// teleport, so the nearest ones (the simulator sends nearest first) are exactly what is missing.
/// <c>RequestMultipleObjects</c> is answered with a full update whatever the simulator thinks we
/// know (OpenSim <c>Scene.RequestPrim</c>), so the missing ones are simply asked for by id.</para>
/// </summary>
internal static class ObjectRecoveryPlan
{
    /// <summary>The ids that were known when the region was left and have not come back since, in
    /// the order given (callers pass them nearest first) and each only once.</summary>
    public static uint[] MissingIds(IEnumerable<uint> known, ICollection<uint> present)
    {
        var seen = new HashSet<uint>();
        var missing = new List<uint>();
        foreach (var id in known)
        {
            if (present.Contains(id) || !seen.Add(id)) continue;
            missing.Add(id);
        }
        return missing.ToArray();
    }

    /// <summary>Splits the ids into requests of at most <paramref name="size"/>: one packet per
    /// chunk keeps the burst below what a simulator's inbound queue shrugs off.</summary>
    public static IEnumerable<IReadOnlyList<uint>> Chunk(uint[] ids, int size)
    {
        for (int i = 0; i < ids.Length; i += size)
            yield return new ArraySegment<uint>(ids, i, Math.Min(size, ids.Length - i));
    }

    /// <summary>Fewer than this many objects in place and there is nothing to judge by.</summary>
    public const int MinSampleToJudge = 30;

    /// <summary>Whether the simulator is holding back what it sent before: almost none of what it
    /// has delivered so far are objects we already knew when we left. A simulator that starts over
    /// resends the lot, so most of what arrives is already on the list; one that remembers what it
    /// sent delivers only the rest. Measured live: 16 of 2003. Judging needs a sample
    /// (<see cref="MinSampleToJudge"/>); with less the answer is "don't know yet" (false).</summary>
    public static bool IsWithholding(IReadOnlyCollection<uint> present, ISet<uint> known)
    {
        if (present.Count < MinSampleToJudge) return false;
        int seenBefore = 0;
        foreach (var id in present)
            if (known.Contains(id)) seenBefore++;
        return seenBefore < present.Count * 0.2;
    }

    /// <summary>Whether the simulator has stopped streaming: the object count has not moved for
    /// the last <paramref name="polls"/> polls, and something did arrive. A count of zero is a
    /// stream that has not begun, not one that has ended.</summary>
    public static bool HasSettled(IReadOnlyList<int> counts, int polls)
    {
        if (counts.Count < polls) return false;
        int last = counts[counts.Count - 1];
        if (last <= 0) return false;
        for (int i = counts.Count - polls; i < counts.Count; i++)
            if (counts[i] != last) return false;
        return true;
    }
}
