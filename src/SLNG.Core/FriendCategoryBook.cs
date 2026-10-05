using System.Text.Json;

namespace SLNG.Core;

/// <summary>
/// One block of the Friends list: a category and the friends in it, or the friends that have no category.
/// </summary>
/// <param name="Category">The category's name, or <c>null</c> for the friends without one.</param>
/// <param name="Friends">The friends in this section, in the order they were handed to
/// <see cref="FriendCategoryBook.Group"/>.</param>
/// <param name="Collapsed">Whether the person folded this section up.</param>
public sealed record FriendSection(string? Category, IReadOnlyList<FriendEntry> Friends, bool Collapsed);

/// <summary>
/// The person's own filing of their friends (FEAT-UI-65): named categories, which friend sits in which, and which
/// categories are folded up. Purely local -- the grid has no such thing, so it is never sent anywhere and another
/// viewer does not see it.
/// <para>A friend is in at most one category; one in none is shown under a block of their own. Category names are
/// unique ignoring case, so "Family" and "family" cannot both exist and be told apart only by looking closely.</para>
/// <para>Engine-agnostic and not thread-safe: the Friends panel owns one and uses it on the main thread. Storage
/// is the app's business: it keeps the string <see cref="ToJson"/> gives and hands it back to <see cref="FromJson"/>.</para>
/// </summary>
public sealed class FriendCategoryBook
{
    /// <summary>Longest category name kept. Longer input is cut, so the list header cannot run off the window.</summary>
    public const int MaxNameLength = 40;

    private readonly List<string> _categories = new();
    private readonly Dictionary<Guid, string> _assignments = new();
    private readonly HashSet<string> _collapsed = new(StringComparer.OrdinalIgnoreCase);

    public FriendCategoryBook()
    {
    }

    /// <summary>Rebuilds a book from what was stored. Forgiving on purpose -- a hand-edited or older file must not
    /// stop the list from showing: blank and duplicate names are dropped, and an assignment or fold state that names
    /// a category which does not exist is ignored.</summary>
    public FriendCategoryBook(
        IEnumerable<string> categories,
        IEnumerable<KeyValuePair<Guid, string>> assignments,
        IEnumerable<string> collapsed,
        bool uncategorizedCollapsed)
    {
        foreach (var name in categories)
            Add(name);
        foreach (var (friend, category) in assignments)
            Assign(friend, category);
        foreach (var name in collapsed)
            if (Find(name) is { } existing)
                _collapsed.Add(existing);
        UncategorizedCollapsed = uncategorizedCollapsed;
    }

    /// <summary>Everything the book holds, as one string for the app to store.</summary>
    public string ToJson() => JsonSerializer.Serialize(new Stored
    {
        Categories = _categories.ToList(),
        Assignments = _assignments.ToDictionary(a => a.Key, a => a.Value),
        Collapsed = _collapsed.ToList(),
        UncategorizedCollapsed = UncategorizedCollapsed,
    });

    /// <summary>Reads what <see cref="ToJson"/> wrote. An empty, missing or unreadable string gives an empty book
    /// rather than an error: the Friends list has to show whatever is in the file.</summary>
    public static FriendCategoryBook FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new FriendCategoryBook();
        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(json);
            if (stored == null) return new FriendCategoryBook();
            return new FriendCategoryBook(
                stored.Categories ?? new List<string>(),
                stored.Assignments ?? new Dictionary<Guid, string>(),
                stored.Collapsed ?? new List<string>(),
                stored.UncategorizedCollapsed);
        }
        catch (JsonException)
        {
            return new FriendCategoryBook();
        }
    }

    private sealed class Stored
    {
        public List<string>? Categories { get; set; }
        public Dictionary<Guid, string>? Assignments { get; set; }
        public List<string>? Collapsed { get; set; }
        public bool UncategorizedCollapsed { get; set; }
    }

    /// <summary>The categories in the order they are listed.</summary>
    public IReadOnlyList<string> Categories => _categories;

    /// <summary>Which category each filed friend is in. A friend in none is absent.</summary>
    public IReadOnlyDictionary<Guid, string> Assignments => _assignments;

    /// <summary>The categories that are folded up.</summary>
    public IReadOnlyCollection<string> CollapsedCategories => _collapsed;

    /// <summary>Whether the block of friends without a category is folded up.</summary>
    public bool UncategorizedCollapsed { get; set; }

    /// <summary>The name as it will be stored: trimmed and cut to <see cref="MaxNameLength"/>. Empty when there is
    /// nothing usable in it.</summary>
    public static string Normalize(string? name)
    {
        string trimmed = (name ?? "").Trim();
        return trimmed.Length > MaxNameLength ? trimmed.Substring(0, MaxNameLength).TrimEnd() : trimmed;
    }

    /// <summary>The existing category this name refers to (ignoring case), or <c>null</c>.</summary>
    public string? Find(string? name)
    {
        string wanted = Normalize(name);
        return _categories.FirstOrDefault(c => string.Equals(c, wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Adds a category at the end of the list. Returns its stored name, or <c>null</c> when the name is
    /// blank or a category of that name already exists.</summary>
    public string? Add(string? name)
    {
        string clean = Normalize(name);
        if (clean.Length == 0 || Find(clean) != null) return null;
        _categories.Add(clean);
        return clean;
    }

    /// <summary>Renames a category; its friends and its fold state come along. Returns false when there is no such
    /// category, the new name is blank, or it belongs to a different category already. A change of case only
    /// ("family" to "Family") is allowed.</summary>
    public bool Rename(string oldName, string newName)
    {
        string? existing = Find(oldName);
        string clean = Normalize(newName);
        if (existing == null || clean.Length == 0) return false;
        if (Find(clean) is { } clash && !string.Equals(clash, existing, StringComparison.Ordinal)) return false;

        int index = _categories.IndexOf(existing);
        _categories[index] = clean;
        foreach (var friend in _assignments.Where(a => a.Value == existing).Select(a => a.Key).ToList())
            _assignments[friend] = clean;
        if (_collapsed.Remove(existing)) _collapsed.Add(clean);
        return true;
    }

    /// <summary>Deletes a category. Its friends are not deleted, they go back to having none.</summary>
    public bool Remove(string name)
    {
        string? existing = Find(name);
        if (existing == null) return false;

        _categories.Remove(existing);
        _collapsed.Remove(existing);
        foreach (var friend in _assignments.Where(a => a.Value == existing).Select(a => a.Key).ToList())
            _assignments.Remove(friend);
        return true;
    }

    /// <summary>Where a category stands in the list (0 is the top), or -1 when there is no such category.</summary>
    public int IndexOf(string name) => Find(name) is { } existing ? _categories.IndexOf(existing) : -1;

    /// <summary>Puts a category at a position in the list; the ones in between shift by one. An index outside the
    /// list is cut to its first or last place, so "far past the end" means "last". Returns true only when the order
    /// changed -- false for an unknown category and for one that already stands there.</summary>
    public bool Move(string category, int newIndex)
    {
        string? existing = Find(category);
        if (existing == null) return false;

        int from = _categories.IndexOf(existing);
        int to = Math.Clamp(newIndex, 0, _categories.Count - 1);
        if (from == to) return false;

        _categories.RemoveAt(from);
        _categories.Insert(to, existing);
        return true;
    }

    /// <summary>Where <paramref name="dragged"/> would land if dropped on <paramref name="target"/>: it takes the place the
    /// target has now, so moving up lands above and moving down lands below. A <c>null</c> target stands for the block of
    /// friends without a category, which is always last, so a drop on it goes to the end, just above that block.
    /// <see cref="CategoryDropPlacement.None"/> when nothing would change: unknown names, onto itself, or to the end when
    /// it is already last.</summary>
    public CategoryDropPlacement DropPlacement(string dragged, string? target)
    {
        int from = IndexOf(dragged);
        if (from < 0) return CategoryDropPlacement.None;
        if (target == null)
            return from == _categories.Count - 1 ? CategoryDropPlacement.None : CategoryDropPlacement.Above;

        int to = IndexOf(target);
        if (to < 0 || to == from) return CategoryDropPlacement.None;
        return from > to ? CategoryDropPlacement.Above : CategoryDropPlacement.Below;
    }

    /// <summary>Files a friend under a category, or under none when <paramref name="category"/> is <c>null</c>.
    /// Returns false for a category that does not exist; the friend stays where they were.</summary>
    public bool Assign(Guid friend, string? category)
    {
        if (category == null)
        {
            _assignments.Remove(friend);
            return true;
        }
        string? existing = Find(category);
        if (existing == null) return false;
        _assignments[friend] = existing;
        return true;
    }

    /// <summary>The category this friend is filed under, or <c>null</c>.</summary>
    public string? CategoryOf(Guid friend) => _assignments.TryGetValue(friend, out var category) ? category : null;

    public bool IsCollapsed(string category) => Find(category) is { } existing && _collapsed.Contains(existing);

    /// <summary>Folds a category up or opens it again. Returns false when there is no such category.</summary>
    public bool SetCollapsed(string category, bool collapsed)
    {
        string? existing = Find(category);
        if (existing == null) return false;
        if (collapsed) _collapsed.Add(existing); else _collapsed.Remove(existing);
        return true;
    }

    /// <summary>Splits friends into the list's blocks: one per category in list order, then the friends without a
    /// category. The friends keep the order they came in, so the caller sorts first.
    /// <para>Without any category there is nothing to group by, and the result is a single block with
    /// <c>Category == null</c> that is never collapsed -- the list then looks as it did before categories existed.
    /// With categories, every one gets a block even when empty (so it can still be filled or deleted), unless
    /// <paramref name="hideEmpty"/> is set -- what a filter wants, where a block with no hit is noise. The block of
    /// friends without a category is only there when it has someone in it.</para></summary>
    /// <param name="ignoreCollapsed">Report every block as open. A filter wants this: a hit inside a folded
    /// block would otherwise be invisible.</param>
    public IReadOnlyList<FriendSection> Group(IEnumerable<FriendEntry> friends, bool hideEmpty = false, bool ignoreCollapsed = false)
    {
        var list = friends.ToList();
        if (_categories.Count == 0)
            return list.Count == 0 && hideEmpty ? Array.Empty<FriendSection>() : new[] { new FriendSection(null, list, false) };

        var sections = new List<FriendSection>();
        foreach (var category in _categories)
        {
            var members = list.Where(f => CategoryOf(f.Id) == category).ToList();
            if (members.Count == 0 && hideEmpty) continue;
            sections.Add(new FriendSection(category, members, !ignoreCollapsed && _collapsed.Contains(category)));
        }

        var rest = list.Where(f => CategoryOf(f.Id) == null).ToList();
        if (rest.Count > 0)
            sections.Add(new FriendSection(null, rest, !ignoreCollapsed && UncategorizedCollapsed));
        return sections;
    }
}
