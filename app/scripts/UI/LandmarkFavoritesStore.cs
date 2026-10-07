using Godot;
using SLNG.Core;

namespace SLNG.App.UI;

/// <summary>
/// Where favorite landmarks and favorites bar settings persist (FEAT-UI-67, BUG-UI-33):
/// user://preferences.cfg, strictly keyed by grid and agent id (favorites_bar_{grid}_{agentId}).
///
/// Favorites are specific to both the resident and the grid: landmark assets and items exist only on their
/// originating grid, and different users or grids must never share, inherit, or leak favorite landmarks.
/// </summary>
public static class LandmarkFavoritesStore
{
    private const string ConfigPath = "user://preferences.cfg";
    private const string SectionPrefix = "favorites_bar_";
    private const string DataKey = "data";
    private const string VisibleKey = "visible";

    public static bool Persist { get; set; } = true;

    public static string? GetSection(string? gridSlug, string? agentId)
    {
        if (string.IsNullOrWhiteSpace(gridSlug) || string.IsNullOrWhiteSpace(agentId))
            return null;

        if (System.Guid.TryParse(agentId, out var guid) && guid == System.Guid.Empty)
            return null;

        return $"{SectionPrefix}{gridSlug.Trim().ToLowerInvariant()}_{agentId.Trim().ToLowerInvariant()}";
    }

    public static LandmarkFavoritesList Load(string? gridSlug, string? agentId)
    {
        string? section = GetSection(gridSlug, agentId);
        if (section == null) return new LandmarkFavoritesList();

        var cfg = new ConfigFile();
        if (cfg.Load(ConfigPath) != Error.Ok) return new LandmarkFavoritesList();

        // One-time cleanup of legacy/corrupted empty-guid sections from earlier sessions
        bool dirty = false;
        if (cfg.HasSection("favorites_bar"))
        {
            cfg.EraseSection("favorites_bar");
            dirty = true;
        }
        if (cfg.HasSection("favorites_bar_00000000-0000-0000-0000-000000000000"))
        {
            cfg.EraseSection("favorites_bar_00000000-0000-0000-0000-000000000000");
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
        if (dirty && Persist)
        {
            cfg.Save(ConfigPath);
        }

        return LoadFromConfig(cfg, section);
    }

    public static LandmarkFavoritesList LoadFromConfig(ConfigFile cfg, string section)
    {
        if (!cfg.HasSectionKey(section, DataKey))
            return new LandmarkFavoritesList();

        var value = cfg.GetValue(section, DataKey);
        return LandmarkFavoritesList.FromJson(value.VariantType == Variant.Type.String ? (string)value : null);
    }

    public static void Save(string? gridSlug, string? agentId, LandmarkFavoritesList list)
    {
        if (!Persist) return;
        string? section = GetSection(gridSlug, agentId);
        if (section == null) return;

        var cfg = new ConfigFile();
        cfg.Load(ConfigPath);
        SaveToConfig(cfg, section, list);
        cfg.Save(ConfigPath);
    }

    public static void SaveToConfig(ConfigFile cfg, string section, LandmarkFavoritesList list)
    {
        cfg.SetValue(section, DataKey, list.ToJson());
    }

    public static bool LoadVisible(string? gridSlug, string? agentId)
    {
        string? section = GetSection(gridSlug, agentId);
        if (section == null) return true;

        var cfg = new ConfigFile();
        if (cfg.Load(ConfigPath) != Error.Ok) return true;
        return LoadVisibleFromConfig(cfg, section);
    }

    public static bool LoadVisibleFromConfig(ConfigFile cfg, string section)
    {
        if (cfg.HasSectionKey(section, VisibleKey))
            return (bool)cfg.GetValue(section, VisibleKey, true);
        return true;
    }

    public static void SaveVisible(string? gridSlug, string? agentId, bool visible)
    {
        if (!Persist) return;
        string? section = GetSection(gridSlug, agentId);
        if (section == null) return;

        var cfg = new ConfigFile();
        cfg.Load(ConfigPath);
        SaveVisibleToConfig(cfg, section, visible);
        cfg.Save(ConfigPath);
    }

    public static void SaveVisibleToConfig(ConfigFile cfg, string section, bool visible)
    {
        cfg.SetValue(section, VisibleKey, visible);
    }
}
