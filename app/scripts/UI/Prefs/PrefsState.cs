using Godot;

namespace SLNG.App.UI.Prefs;

/// <summary>
/// What the Preferences window remembers about itself (FEAT-UI-79): the category that was open and which sections had
/// their "Advanced" fold-out opened. Persisted to user://preferences.cfg under its own "preferences_window" section --
/// the same file and the same load-modify-save pattern as <see cref="UiSettings"/>, so the other settings in the file
/// are never touched. Nothing is written unless the value actually changes: opening the window, or building it at
/// boot, writes nothing.
///
/// <para>Reading is cached: building the window asks about every section that has an Advanced fold-out, and parsing the
/// file for each of them added up. The cache is parsed once and reused until the file changes under it (another feature
/// saves its own section, a test replaces it) or <see cref="ConfigPath"/> points elsewhere; it is checked against the
/// file's modification time and size, which costs a stat, not a parse. Writing always starts from a fresh load, never
/// from the cache, so a section another feature saved in between is kept.</para>
/// </summary>
public static class PrefsState
{
    private const string Section = "preferences_window";
    private const string LastCategoryKey = "last_category";

    private static ConfigFile? _cache;
    private static string _cachePath = "";
    private static (ulong Modified, long Size) _cacheStamp;

    /// <summary>The file. A selftest points this somewhere else so it cannot touch the person's real preferences.</summary>
    public static string ConfigPath { get; set; } = "user://preferences.cfg";

    /// <summary>The category that was selected last, or null when none was ever recorded.</summary>
    public static string? LastCategory
    {
        get
        {
            var cfg = Read();
            return cfg.HasSectionKey(Section, LastCategoryKey) ? (string)cfg.GetValue(Section, LastCategoryKey, "") : null;
        }
    }

    /// <summary>Remembers the selected category. A no-op when it is already the remembered one.</summary>
    public static void SetLastCategory(string categoryId)
    {
        var current = Read();
        if (current.HasSectionKey(Section, LastCategoryKey) && (string)current.GetValue(Section, LastCategoryKey, "") == categoryId) return;
        Write(cfg => cfg.SetValue(Section, LastCategoryKey, categoryId));
    }

    /// <summary>Whether the person left a section's Advanced fold-out open. Collapsed (false) when never recorded.</summary>
    public static bool IsAdvancedOpen(string categoryId, string sectionId)
        => (bool)Read().GetValue(Section, AdvancedKey(categoryId, sectionId), false);

    /// <summary>Remembers a section's Advanced fold-out as open or closed. A no-op when it already is that.</summary>
    public static void SetAdvancedOpen(string categoryId, string sectionId, bool open)
    {
        string key = AdvancedKey(categoryId, sectionId);
        if ((bool)Read().GetValue(Section, key, false) == open) return;
        Write(cfg => cfg.SetValue(Section, key, open));
    }

    private static string AdvancedKey(string categoryId, string sectionId) => $"advanced_{categoryId}.{sectionId}";

    /// <summary>The file, parsed. Shared: callers only read from it.</summary>
    private static ConfigFile Read()
    {
        string path = ConfigPath;
        var stamp = StampOf(path);
        if (_cache == null || _cachePath != path || _cacheStamp != stamp)
        {
            var cfg = new ConfigFile();
            cfg.Load(path); // a missing file is a fresh install: an empty config
            _cache = cfg;
            _cachePath = path;
            _cacheStamp = stamp;
        }
        return _cache;
    }

    /// <summary>Loads the file afresh (other features save into it too), applies <paramref name="edit"/>, saves, and keeps
    /// the result as the cache.</summary>
    private static void Write(System.Action<ConfigFile> edit)
    {
        string path = ConfigPath;
        var cfg = new ConfigFile();
        cfg.Load(path);
        edit(cfg);
        cfg.Save(path);
        _cache = cfg;
        _cachePath = path;
        _cacheStamp = StampOf(path);
    }

    /// <summary>Modification time and size of the file; (0, 0) when it does not exist.</summary>
    private static (ulong Modified, long Size) StampOf(string path)
    {
        if (!FileAccess.FileExists(path)) return (0, 0);
        using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        return (FileAccess.GetModifiedTime(path), file == null ? 0 : (long)file.GetLength());
    }
}
