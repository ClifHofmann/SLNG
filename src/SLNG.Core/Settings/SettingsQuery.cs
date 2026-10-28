namespace SLNG.Core.Settings;

/// <summary>
/// A search query after <see cref="SettingsSearch.Parse"/>: its normalized words. Made once per keystroke and handed to
/// every <see cref="SettingsSearchEntry"/>, so no entry re-parses it.
/// </summary>
public sealed class SettingsQuery
{
    /// <summary>The query that matches everything (the field is empty).</summary>
    public static readonly SettingsQuery Empty = new(Array.Empty<string>());

    internal SettingsQuery(string[] words)
    {
        Words = words;
    }

    /// <summary>The normalized words; every one has to occur for an entry to match.</summary>
    public IReadOnlyList<string> Words { get; }

    /// <summary>True when there is nothing to look for.</summary>
    public bool IsEmpty => Words.Count == 0;
}
