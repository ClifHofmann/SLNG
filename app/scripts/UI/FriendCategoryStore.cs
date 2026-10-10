using Godot;
using SLNG.Core;

namespace SLNG.App.UI;

/// <summary>
/// Where the Friends list's categories live on disk (FEAT-UI-65, BUG-UI-41): user://preferences.cfg, strictly
/// keyed by grid and agent id (friend_categories_{grid}_{agentId}).
///
/// Categories are specific to both the resident and the grid: different accounts or servers maintain completely
/// independent categories, assignments, fold states, and view switches.
/// </summary>
public static class FriendCategoryStore
{
    private const string ConfigPath = "user://preferences.cfg";
    private const string SectionPrefix = "friend_categories_";
    private const string Key = "data";
    private const string OnlyOnlineKey = "only_online";
    private const string ShowCategoriesKey = "show_categories";

    /// <summary>False keeps every change in memory only. <c>--selftest</c> boots against the developer's real
    /// <c>user://</c>, so a check that changes categories switches this off and never touches preferences.cfg.</summary>
    public static bool Persist { get; set; } = true;

    public static string? GetSection(string? gridSlug, string? agentId)
    {
        if (string.IsNullOrWhiteSpace(gridSlug) || string.IsNullOrWhiteSpace(agentId))
            return null;

        if (System.Guid.TryParse(agentId, out var guid) && guid == System.Guid.Empty)
            return null;

        return $"{SectionPrefix}{gridSlug.Trim().ToLowerInvariant()}_{agentId.Trim().ToLowerInvariant()}";
    }

    /// <summary>The saved book for this account on this grid, or an empty one when there is none (or it cannot be read).</summary>
    public static FriendCategoryBook Load(string? gridSlug, string? agentId)
    {
        string? section = GetSection(gridSlug, agentId);
        if (section == null) return new FriendCategoryBook();

        var cfg = new ConfigFile();
        if (cfg.Load(ConfigPath) != Error.Ok) return new FriendCategoryBook();

        // One-time cleanup of legacy/corrupted empty-guid sections from earlier sessions
        bool dirty = false;
        if (cfg.HasSection("friend_categories_00000000-0000-0000-0000-000000000000"))
        {
            cfg.EraseSection("friend_categories_00000000-0000-0000-0000-000000000000");
            dirty = true;
        }
        foreach (var s in cfg.GetSections())
        {
            if (s.StartsWith(SectionPrefix) && s.EndsWith("00000000-0000-0000-0000-000000000000"))
            {
                cfg.EraseSection(s);
                dirty = true;
            }
        }

        // Migrate legacy un-scoped friend_categories_<agentId> if section does not exist yet
        string legacySection = SectionPrefix + agentId!.Trim().ToLowerInvariant();
        if (!cfg.HasSection(section) && cfg.HasSection(legacySection))
        {
            if (cfg.HasSectionKey(legacySection, Key))
                cfg.SetValue(section, Key, cfg.GetValue(legacySection, Key));
            if (cfg.HasSectionKey(legacySection, OnlyOnlineKey))
                cfg.SetValue(section, OnlyOnlineKey, cfg.GetValue(legacySection, OnlyOnlineKey));
            if (cfg.HasSectionKey(legacySection, ShowCategoriesKey))
                cfg.SetValue(section, ShowCategoriesKey, cfg.GetValue(legacySection, ShowCategoriesKey));
            cfg.EraseSection(legacySection);
            dirty = true;
        }

        if (dirty && Persist)
        {
            cfg.Save(ConfigPath);
        }

        return LoadFromConfig(cfg, section);
    }

    public static FriendCategoryBook LoadFromConfig(ConfigFile cfg, string section)
    {
        if (!cfg.HasSectionKey(section, Key)) return new FriendCategoryBook();
        var value = cfg.GetValue(section, Key);
        return FriendCategoryBook.FromJson(value.VariantType == Variant.Type.String ? (string)value : null);
    }

    /// <summary>Whether the list shows only friends who are online. Off by default.</summary>
    public static bool LoadOnlyOnline(string? gridSlug, string? agentId)
    {
        string? section = GetSection(gridSlug, agentId);
        if (section == null) return false;

        var cfg = new ConfigFile();
        if (cfg.Load(ConfigPath) != Error.Ok) return false;
        return LoadOnlyOnlineFromConfig(cfg, section);
    }

    public static bool LoadOnlyOnlineFromConfig(ConfigFile cfg, string section)
    {
        return (bool)cfg.GetValue(section, OnlyOnlineKey, false);
    }

    /// <summary>Whether the list is drawn grouped under the categories. On by default; off draws one plain list and
    /// leaves the categories and the filing as they are.</summary>
    public static bool LoadShowCategories(string? gridSlug, string? agentId)
    {
        string? section = GetSection(gridSlug, agentId);
        if (section == null) return true;

        var cfg = new ConfigFile();
        if (cfg.Load(ConfigPath) != Error.Ok) return true;
        return LoadShowCategoriesFromConfig(cfg, section);
    }

    public static bool LoadShowCategoriesFromConfig(ConfigFile cfg, string section)
    {
        return (bool)cfg.GetValue(section, ShowCategoriesKey, true);
    }

    public static void SaveShowCategories(string? gridSlug, string? agentId, bool show)
    {
        if (!Persist) return;
        string? section = GetSection(gridSlug, agentId);
        if (section == null) return;

        var cfg = new ConfigFile();
        cfg.Load(ConfigPath);
        SaveShowCategoriesToConfig(cfg, section, show);
        cfg.Save(ConfigPath);
    }

    public static void SaveShowCategoriesToConfig(ConfigFile cfg, string section, bool show)
    {
        cfg.SetValue(section, ShowCategoriesKey, show);
    }

    public static void SaveOnlyOnline(string? gridSlug, string? agentId, bool onlyOnline)
    {
        if (!Persist) return;
        string? section = GetSection(gridSlug, agentId);
        if (section == null) return;

        var cfg = new ConfigFile();
        cfg.Load(ConfigPath);
        SaveOnlyOnlineToConfig(cfg, section, onlyOnline);
        cfg.Save(ConfigPath);
    }

    public static void SaveOnlyOnlineToConfig(ConfigFile cfg, string section, bool onlyOnline)
    {
        cfg.SetValue(section, OnlyOnlineKey, onlyOnline);
    }

    public static void Save(string? gridSlug, string? agentId, FriendCategoryBook book)
    {
        if (!Persist) return;
        string? section = GetSection(gridSlug, agentId);
        if (section == null) return;

        var cfg = new ConfigFile();
        cfg.Load(ConfigPath); // preserve sections owned by other features
        SaveToConfig(cfg, section, book);
        cfg.Save(ConfigPath);
    }

    public static void SaveToConfig(ConfigFile cfg, string section, FriendCategoryBook book)
    {
        cfg.SetValue(section, Key, book.ToJson());
    }
}
