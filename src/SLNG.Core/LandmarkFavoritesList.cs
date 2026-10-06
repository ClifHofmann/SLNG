using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace SLNG.Core;

/// <summary>
/// A single landmark pinned to the favorites bar (FEAT-UI-67).
/// </summary>
/// <param name="ItemId">The inventory item Guid, or Empty if unknown.</param>
/// <param name="AssetId">The landmark asset Guid, or Empty if pending resolution.</param>
/// <param name="Name">Display name of the favorite destination.</param>
public sealed record LandmarkFavoriteItem(Guid ItemId, Guid AssetId, string Name);

/// <summary>
/// Model managing favorite landmarks shown on the favorites bar (FEAT-UI-67).
/// Pure domain logic, thread-safe for reads, with JSON serialization.
/// </summary>
public sealed class LandmarkFavoritesList
{
    public const int MaxFavorites = 64;
    public const int MaxNameLength = 60;

    private readonly List<LandmarkFavoriteItem> _items = new();

    public IReadOnlyList<LandmarkFavoriteItem> Items => _items;

    public LandmarkFavoritesList() { }

    public LandmarkFavoritesList(IEnumerable<LandmarkFavoriteItem> items)
    {
        foreach (var item in items)
        {
            Add(item);
        }
    }

    /// <summary>
    /// Checks if a landmark is already in favorites by ItemId or AssetId.
    /// </summary>
    public bool Contains(Guid id)
    {
        if (id == Guid.Empty) return false;
        return _items.Any(i => (i.ItemId != Guid.Empty && i.ItemId == id) || (i.AssetId != Guid.Empty && i.AssetId == id));
    }

    /// <summary>
    /// Adds a favorite item if not already present and limit not reached.
    /// Returns true if added, false if already exists or invalid.
    /// </summary>
    public bool Add(LandmarkFavoriteItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Name)) return false;
        if (_items.Count >= MaxFavorites) return false;

        if (item.AssetId != Guid.Empty && _items.Any(i => i.AssetId == item.AssetId))
            return false;
        if (item.ItemId != Guid.Empty && _items.Any(i => i.ItemId == item.ItemId))
            return false;

        string name = item.Name.Trim();
        if (name.Length > MaxNameLength) name = name[..MaxNameLength];

        _items.Add(item with { Name = name });
        return true;
    }

    /// <summary>
    /// Removes a favorite landmark by ItemId or AssetId.
    /// </summary>
    public bool Remove(Guid id)
    {
        if (id == Guid.Empty) return false;
        return _items.RemoveAll(i => i.ItemId == id || i.AssetId == id) > 0;
    }

    /// <summary>
    /// Clears all favorites.
    /// </summary>
    public void Clear() => _items.Clear();

    /// <summary>
    /// Moves an item from one index to another (reordering).
    /// </summary>
    public bool Move(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= _items.Count) return false;
        if (toIndex < 0 || toIndex >= _items.Count) return false;
        if (fromIndex == toIndex) return true;

        var item = _items[fromIndex];
        _items.RemoveAt(fromIndex);
        _items.Insert(toIndex, item);
        return true;
    }

    private sealed class StoredDto
    {
        public List<ItemDto> Items { get; set; } = new();
    }

    private sealed class ItemDto
    {
        public string ItemId { get; set; } = "";
        public string AssetId { get; set; } = "";
        public string Name { get; set; } = "";
    }

    public string ToJson()
    {
        var dto = new StoredDto
        {
            Items = _items.Select(i => new ItemDto
            {
                ItemId = i.ItemId.ToString(),
                AssetId = i.AssetId.ToString(),
                Name = i.Name
            }).ToList()
        };
        return JsonSerializer.Serialize(dto);
    }

    public static LandmarkFavoritesList FromJson(string? json)
    {
        var list = new LandmarkFavoritesList();
        if (string.IsNullOrWhiteSpace(json)) return list;

        try
        {
            var dto = JsonSerializer.Deserialize<StoredDto>(json);
            if (dto?.Items != null)
            {
                foreach (var item in dto.Items)
                {
                    Guid.TryParse(item.ItemId, out var itemId);
                    Guid.TryParse(item.AssetId, out var assetId);
                    if (!string.IsNullOrWhiteSpace(item.Name) && (itemId != Guid.Empty || assetId != Guid.Empty))
                    {
                        list.Add(new LandmarkFavoriteItem(itemId, assetId, item.Name));
                    }
                }
            }
        }
        catch
        {
            // Tolerant fallback on corrupted data
        }

        return list;
    }
}
