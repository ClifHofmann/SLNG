using Godot;
using SLNG.Core;

namespace SLNG.App.UI;

/// <summary>
/// Where favorite landmarks and favorites bar settings persist (FEAT-UI-67):
/// user://preferences.cfg, keyed by agent id (same ConfigFile pattern as <see cref="FriendCategoryStore"/>).
/// </summary>
public static class LandmarkFavoritesStore
{
    private const string ConfigPath = "user://preferences.cfg";
    private const string SectionPrefix = "favorites_bar_";
    private const string GlobalSection = "favorites_bar";
    private const string DataKey = "data";
    private const string VisibleKey = "visible";

    public static bool Persist { get; set; } = true;

    private static string GetSection(string? agentId) =>
        string.IsNullOrWhiteSpace(agentId) ? GlobalSection : SectionPrefix + agentId;

    public static LandmarkFavoritesList Load(string? agentId)
    {
        var cfg = new ConfigFile();
        if (cfg.Load(ConfigPath) != Error.Ok) return new LandmarkFavoritesList();

        string section = GetSection(agentId);
        if (!cfg.HasSectionKey(section, DataKey))
        {
            if (section != GlobalSection && cfg.HasSectionKey(GlobalSection, DataKey))
            {
                section = GlobalSection;
            }
            else
            {
                return new LandmarkFavoritesList();
            }
        }

        var value = cfg.GetValue(section, DataKey);
        return LandmarkFavoritesList.FromJson(value.VariantType == Variant.Type.String ? (string)value : null);
    }

    public static void Save(string? agentId, LandmarkFavoritesList list)
    {
        if (!Persist) return;
        var cfg = new ConfigFile();
        cfg.Load(ConfigPath);
        string section = GetSection(agentId);
        cfg.SetValue(section, DataKey, list.ToJson());
        cfg.Save(ConfigPath);
    }

    public static bool LoadVisible(string? agentId)
    {
        var cfg = new ConfigFile();
        if (cfg.Load(ConfigPath) != Error.Ok) return true;
        string section = GetSection(agentId);
        if (cfg.HasSectionKey(section, VisibleKey))
            return (bool)cfg.GetValue(section, VisibleKey, true);
        if (cfg.HasSectionKey(GlobalSection, VisibleKey))
            return (bool)cfg.GetValue(GlobalSection, VisibleKey, true);
        return true;
    }

    public static void SaveVisible(string? agentId, bool visible)
    {
        if (!Persist) return;
        var cfg = new ConfigFile();
        cfg.Load(ConfigPath);
        string section = GetSection(agentId);
        cfg.SetValue(section, VisibleKey, visible);
        cfg.Save(ConfigPath);
    }
}
