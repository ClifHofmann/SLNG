namespace SLNG.Net.ObjectCache;

/// <summary>The arithmetic of restoring a region from the cache without the simulator's say-so
/// (FEAT-NET-04, phase 3): what to show first, and what to take back out.</summary>
internal static class ObjectRestorePlan
{
    /// <summary>The objects ordered by distance from where the avatar stands, nearest first: the
    /// near ones are the ones on screen. One whose position cannot be read is shown last, not dropped.</summary>
    public static IReadOnlyList<CachedObject> NearestFirst(IEnumerable<CachedObject> objects, float x, float y, float z)
    {
        return objects
            .Select(o =>
            {
                if (!CompressedObjectBlock.TryReadPosition(o.Block, out var ox, out var oy, out var oz))
                    return (Object: o, Distance: float.MaxValue);
                float dx = ox - x, dy = oy - y, dz = oz - z;
                return (Object: o, Distance: dx * dx + dy * dy + dz * dz);
            })
            .OrderBy(t => t.Distance)
            .Select(t => t.Object)
            .ToList();
    }

    /// <summary>The objects that were shown from the cache and that the simulator never answered
    /// for: a simulator asked for an object that exists answers with its full state.</summary>
    public static uint[] Unanswered(IEnumerable<uint> shown, ISet<uint> answered)
        => shown.Where(id => !answered.Contains(id)).ToArray();
}
