using System;
using System.Collections.Generic;
using System.Linq;

namespace SLNG.Core.Landmarks;

public enum LandmarkDuplicateMatchKind
{
    ExactAsset,
    ExactName
}

public sealed class LandmarkDuplicateGroup
{
    public string GroupKey { get; }
    public string CanonicalName { get; }
    public LandmarkDuplicateMatchKind MatchKind { get; }
    public IReadOnlyList<LandmarkInventoryItem> Items { get; }
    public Guid RecommendedKeepItemId { get; set; }

    public LandmarkDuplicateGroup(
        string groupKey,
        string canonicalName,
        LandmarkDuplicateMatchKind matchKind,
        IEnumerable<LandmarkInventoryItem> items,
        Guid recommendedKeepItemId)
    {
        GroupKey = groupKey;
        CanonicalName = canonicalName;
        MatchKind = matchKind;
        Items = items.ToList().AsReadOnly();
        RecommendedKeepItemId = recommendedKeepItemId;
    }
}

public static class LandmarkDuplicateDetector
{
    public static List<LandmarkDuplicateGroup> FindDuplicates(
        IEnumerable<LandmarkInventoryItem> items,
        ISet<Guid>? favoriteIds = null,
        string? primaryFolderName = "Landmarks")
    {
        var result = new List<LandmarkDuplicateGroup>();
        var validItems = items
            .Where(i => i.Id != Guid.Empty && !string.IsNullOrWhiteSpace(i.Name))
            .ToList();

        var claimedItemIds = new HashSet<Guid>();

        // 1. Group by AssetId (exact asset duplicates)
        var assetGroups = validItems
            .Where(i => i.AssetId != Guid.Empty)
            .GroupBy(i => i.AssetId)
            .Where(g => g.Count() > 1);

        foreach (var g in assetGroups)
        {
            var list = g.ToList();
            foreach (var item in list)
                claimedItemIds.Add(item.Id);

            string name = list[0].Name;
            var keepId = ScoreAndSelectKeepItem(list, favoriteIds, primaryFolderName);
            result.Add(new LandmarkDuplicateGroup(
                groupKey: $"asset_{g.Key}",
                canonicalName: name,
                matchKind: LandmarkDuplicateMatchKind.ExactAsset,
                items: list,
                recommendedKeepItemId: keepId));
        }

        // 2. Group unclaimed items by normalized name (exact name duplicates)
        var nameGroups = validItems
            .Where(i => !claimedItemIds.Contains(i.Id))
            .GroupBy(i => i.Name.Trim().ToLowerInvariant())
            .Where(g => g.Count() > 1);

        foreach (var g in nameGroups)
        {
            var list = g.ToList();
            string name = list[0].Name;
            var keepId = ScoreAndSelectKeepItem(list, favoriteIds, primaryFolderName);
            result.Add(new LandmarkDuplicateGroup(
                groupKey: $"name_{g.Key}",
                canonicalName: name,
                matchKind: LandmarkDuplicateMatchKind.ExactName,
                items: list,
                recommendedKeepItemId: keepId));
        }

        return result
            .OrderByDescending(g => g.Items.Count)
            .ThenBy(g => g.CanonicalName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static Guid ScoreAndSelectKeepItem(
        IReadOnlyList<LandmarkInventoryItem> items,
        ISet<Guid>? favoriteIds,
        string? primaryFolderName)
    {
        if (items.Count == 0) return Guid.Empty;
        if (items.Count == 1) return items[0].Id;

        // Order candidates so the single best item to KEEP comes first:
        // 1. Is in favorites bar (user explicitly pinned it)
        // 2. Newest creation date (latest landmark is preferred over outdated copies)
        // 3. Primary folder (e.g. Landmarks)
        // 4. Not in Received Items / Trash / Unpack
        // 5. Shortest path length
        var bestItem = items
            .OrderByDescending(item => favoriteIds != null && (favoriteIds.Contains(item.Id) || (item.AssetId != Guid.Empty && favoriteIds.Contains(item.AssetId))))
            .ThenByDescending(item => item.CreationDate != default ? item.CreationDate : DateTime.MinValue)
            .ThenByDescending(item => !string.IsNullOrEmpty(primaryFolderName) && item.FolderPath.Contains(primaryFolderName, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(item => !item.FolderPath.Contains("Received Items", StringComparison.OrdinalIgnoreCase) &&
                                      !item.FolderPath.Contains("Trash", StringComparison.OrdinalIgnoreCase) &&
                                      !item.FolderPath.Contains("Unpack", StringComparison.OrdinalIgnoreCase))
            .ThenBy(item => item.FolderPath.Length)
            .First();

        return bestItem.Id;
    }
}
