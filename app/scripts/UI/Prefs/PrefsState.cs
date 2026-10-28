using Godot;

namespace SLNG.App.UI.Prefs;

/// <summary>
/// What the Preferences window remembers about itself (FEAT-UI-79): the category that was open and which sections had
/// their "Advanced" fold-out opened. Persisted to user://preferences.cfg under its own "preferences_window" section --
/// the same file and the same load-modify-save pattern as <see cref="UiSettings"/>, so the other settings in the file
/// are never touched. Nothing is written unless the value actually changes: opening the window, or building it at
/// boot, writes nothing.
/// </summary>
public static class PrefsState
{
    private const string Section = "preferences_window";
    private const string LastCategoryKey = "last_category";

    /// <summary>The file. A selftest points this somewhere else so it cannot touch the person's real preferences.</summary>
    public static string ConfigPath { get; set; } = "user://preferences.cfg";

    /// <summary>The category that was selected last, or null when none was ever recorded.</summary>
    public static string? LastCategory
    {
        get
        {
            var cfg = Load();
            return cfg.HasSectionKey(Section, LastCategoryKey) ? (string)cfg.GetValue(Section, LastCategoryKey, "") : null;
        }
    }

    /// <summary>Remembers the selected category. A no-op when it is already the remembered one.</summary>
    public static void SetLastCategory(string categoryId)
    {
        var cfg = Load();
        if (cfg.HasSectionKey(Section, LastCategoryKey) && (string)cfg.GetValue(Section, LastCategoryKey, "") == categoryId) return;
        cfg.SetValue(Section, LastCategoryKey, categoryId);
        cfg.Save(ConfigPath);
    }

    /// <summary>Whether the person left a section's Advanced fold-out open. Collapsed (false) when never recorded.</summary>
    public static bool IsAdvancedOpen(string categoryId, string sectionId)
        => (bool)Load().GetValue(Section, AdvancedKey(categoryId, sectionId), false);

    /// <summary>Remembers a section's Advanced fold-out as open or closed. A no-op when it already is that.</summary>
    public static void SetAdvancedOpen(string categoryId, string sectionId, bool open)
    {
        var cfg = Load();
        string key = AdvancedKey(categoryId, sectionId);
        if ((bool)cfg.GetValue(Section, key, false) == open) return;
        cfg.SetValue(Section, key, open);
        cfg.Save(ConfigPath);
    }

    private static string AdvancedKey(string categoryId, string sectionId) => $"advanced_{categoryId}.{sectionId}";

    private static ConfigFile Load()
    {
        var cfg = new ConfigFile();
        cfg.Load(ConfigPath); // a missing file is a fresh install: an empty config
        return cfg;
    }
}
