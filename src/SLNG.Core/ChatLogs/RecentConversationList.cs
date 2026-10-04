using System.Text.Json;
using SLNG.Core.Services;

namespace SLNG.Core.ChatLogs;

/// <summary>One conversation in the "Recent" list: enough to reopen it. The chat log on disk is keyed by
/// name only (<see cref="FirestormLogLayout"/> cleans it, so it cannot be turned back into an id), and an
/// IM tab needs the agent id to send -- hence this separate record.</summary>
/// <param name="Kind"><see cref="ChatLogKind.Im"/> or <see cref="ChatLogKind.Group"/>; nearby chat is not a
/// conversation partner and is never listed.</param>
/// <param name="Id">The other agent's id (IM) or the group's id.</param>
/// <param name="Name">The name the log file is named after.</param>
/// <param name="LastActivityUtc">When the last line was sent or received.</param>
public sealed record RecentConversation(ChatLogKind Kind, Guid Id, string Name, DateTime LastActivityUtc);

/// <summary>
/// FEAT-UI-15: the most recently active IM and group conversations, newest first, capped. Pure and
/// engine-agnostic: the app feeds it a message at a time and gives it a file to live in (one per chat-log
/// folder, so one per account and grid). Not thread-safe; use it from the main thread.
/// </summary>
public sealed class RecentConversationList
{
    public const int DefaultCapacity = 30;

    private readonly List<RecentConversation> _items = new();
    private readonly int _capacity;

    public RecentConversationList(int capacity = DefaultCapacity) => _capacity = Math.Max(1, capacity);

    /// <summary>Newest first.</summary>
    public IReadOnlyList<RecentConversation> Items => _items;

    /// <summary>Records activity in a conversation and moves it to the top. Returns true when the list
    /// changed in a way worth saving (a new entry, a new position or a new name); a repeat message in the
    /// conversation already on top only refreshes the time and returns false. Nearby chat and empty ids
    /// are ignored.</summary>
    public bool Touch(ChatLogKind kind, Guid id, string name, DateTime whenUtc)
    {
        if (kind == ChatLogKind.Local || id == Guid.Empty || string.IsNullOrWhiteSpace(name)) return false;

        int at = _items.FindIndex(c => c.Kind == kind && c.Id == id);
        if (at == 0)
        {
            bool renamed = _items[0].Name != name;
            _items[0] = _items[0] with { Name = name, LastActivityUtc = whenUtc };
            return renamed;
        }

        if (at > 0) _items.RemoveAt(at);
        _items.Insert(0, new RecentConversation(kind, id, name, whenUtc));
        if (_items.Count > _capacity) _items.RemoveRange(_capacity, _items.Count - _capacity);
        return true;
    }

    public bool Remove(ChatLogKind kind, Guid id) => _items.RemoveAll(c => c.Kind == kind && c.Id == id) > 0;

    public void Clear() => _items.Clear();

    /// <summary>Replaces the contents with what <paramref name="path"/> holds. A missing or unreadable file
    /// is an empty list, never an error: this is a convenience, not a record.</summary>
    public void Load(string? path)
    {
        _items.Clear();
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        try
        {
            var stored = JsonSerializer.Deserialize<List<Stored>>(File.ReadAllText(path));
            if (stored is null) return;
            foreach (var s in stored)
            {
                if (s.Kind == ChatLogKind.Local || s.Id == Guid.Empty || string.IsNullOrWhiteSpace(s.Name)) continue;
                if (_items.Exists(c => c.Kind == s.Kind && c.Id == s.Id)) continue;
                _items.Add(new RecentConversation(s.Kind, s.Id, s.Name, s.LastActivityUtc));
            }
            _items.Sort((a, b) => b.LastActivityUtc.CompareTo(a.LastActivityUtc));
            if (_items.Count > _capacity) _items.RemoveRange(_capacity, _items.Count - _capacity);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _items.Clear();
        }
    }

    /// <summary>Writes the list to <paramref name="path"/>. Failure is swallowed (returns false): losing the
    /// list must never break chat.</summary>
    public bool Save(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var stored = _items.Select(c => new Stored(c.Kind, c.Id, c.Name, c.LastActivityUtc)).ToList();
            File.WriteAllText(path, JsonSerializer.Serialize(stored));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed record Stored(ChatLogKind Kind, Guid Id, string Name, DateTime LastActivityUtc);
}
