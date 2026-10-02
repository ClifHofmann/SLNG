namespace SLNG.Core.ChatLogs;

/// <summary>
/// FEAT-UI-41: the chat-log base folder chosen per account, in memory. An account with nothing chosen
/// is simply absent and falls back to SLNG's default folder. The app persists the pairs
/// (<c>preferences.cfg</c>, section <c>chat_log_dirs</c>); this holds the rules: an empty folder clears,
/// keys are compared exactly, two accounts never share an entry.
/// </summary>
public sealed class ChatLogFolderMap
{
    private readonly Dictionary<string, string> _folders = new(StringComparer.Ordinal);

    /// <summary>Every account that has a folder, as stored.</summary>
    public IReadOnlyDictionary<string, string> Entries => _folders;

    /// <summary>The folder chosen for an account, or null when none is.</summary>
    public string? Get(string key) => _folders.TryGetValue(key, out string? folder) ? folder : null;

    /// <summary>Chooses a folder for an account; an empty or blank one clears the choice.</summary>
    public void Set(string key, string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) _folders.Remove(key);
        else _folders[key] = folder.Trim();
    }

    public void Clear(string key) => _folders.Remove(key);

    /// <summary>Rebuilds the map from stored pairs; blank values are dropped.</summary>
    public static ChatLogFolderMap FromPairs(IEnumerable<KeyValuePair<string, string>> pairs)
    {
        var map = new ChatLogFolderMap();
        foreach (var (key, folder) in pairs) map.Set(key, folder);
        return map;
    }
}
