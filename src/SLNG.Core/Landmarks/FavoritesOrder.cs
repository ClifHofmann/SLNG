namespace SLNG.Core.Landmarks;

/// <summary>
/// FEAT-UI-72: the order in which the Favorites folder's landmarks are shown -- a port of the reference viewer's
/// <c>LLFavoritesSort</c> (<c>llfavoritesbar.cpp:376-401</c>).
///
/// <para>The order is <b>client-local</b>, not stored on the grid: each viewer keeps its own list of item ids
/// (<paramref name="userOrder"/> below), so another viewer never sees this one's order. Items the list has an index
/// for sort ascending by it; an item <b>without</b> an index sorts <b>before</b> the indexed ones (a fresh favorite
/// shows first until its place is stored); two items without an index sort newer <c>CreationDate</c> first and then by
/// name, ignoring case. Once an item has been shown the viewer freezes the shown order by storing it
/// (<c>needToSaveItemsOrder</c>, <c>:1691-1706</c>) -- <see cref="NeedsToSaveOrder"/>.</para>
/// </summary>
public static class FavoritesOrder
{
    /// <summary>The favorites in display order for the given stored order (see the class remarks).</summary>
    public static IReadOnlyList<LandmarkInventoryItem> Sort(
        IEnumerable<LandmarkInventoryItem> items, IReadOnlyList<Guid> userOrder)
    {
        var index = new Dictionary<Guid, int>();
        for (int i = 0; i < userOrder.Count; i++)
            index.TryAdd(userOrder[i], i);

        // Items with no index first (newest first, then by name), then the indexed ones in stored order. The id is
        // the last key so the result never depends on the input order of equal items.
        return items
            .OrderBy(i => index.ContainsKey(i.Id) ? 1 : 0)
            .ThenBy(i => index.TryGetValue(i.Id, out int at) ? at : 0)
            .ThenByDescending(i => i.CreationDate)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Id)
            .ToList();
    }

    /// <summary><paramref name="userOrder"/> with <paramref name="itemId"/> appended, unless it is already there.</summary>
    public static IReadOnlyList<Guid> WithAdded(IReadOnlyList<Guid> userOrder, Guid itemId)
    {
        var result = new List<Guid>(userOrder.Count + 1);
        var seen = new HashSet<Guid>();
        foreach (var id in userOrder)
            if (seen.Add(id)) result.Add(id);
        if (itemId != Guid.Empty && seen.Add(itemId)) result.Add(itemId);
        return result;
    }

    /// <summary><paramref name="userOrder"/> without the entries that are no longer favorites (and without repeats).</summary>
    public static IReadOnlyList<Guid> Pruned(IReadOnlyList<Guid> userOrder, IEnumerable<LandmarkInventoryItem> existing)
    {
        var alive = new HashSet<Guid>(existing.Select(i => i.Id));
        var seen = new HashSet<Guid>();
        var result = new List<Guid>(userOrder.Count);
        foreach (var id in userOrder)
            if (alive.Contains(id) && seen.Add(id)) result.Add(id);
        return result;
    }

    /// <summary>
    /// True when the shown list has an item the stored order does not know yet -- the viewer then stores the whole
    /// shown order (<c>1..n</c>) so what the person sees stops moving when more favorites arrive.
    /// </summary>
    public static bool NeedsToSaveOrder(IEnumerable<LandmarkInventoryItem> shown, IReadOnlyList<Guid> userOrder)
    {
        var known = new HashSet<Guid>(userOrder);
        foreach (var item in shown)
            if (!known.Contains(item.Id)) return true;
        return false;
    }
}
