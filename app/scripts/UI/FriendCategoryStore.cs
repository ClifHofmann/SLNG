using Godot;
using SLNG.Core;

namespace SLNG.App.UI;

/// <summary>
/// Where the Friends list's categories live on disk (FEAT-UI-65): user://preferences.cfg, one section per
/// logged-in account -- same ConfigFile pattern and file as <see cref="UiSettings"/> / <see cref="GroupMuteSettings"/>.
/// A second account on the same machine has other friends and its own idea of how to sort them, so the key is the
/// agent id; a friend's UUID is the same for every account, but the categories are not.
/// <para>The Friends list's "show only online" switch rides along under the same section: it is a view choice of
/// the same list, and a second account may want it the other way round.</para>
/// <para>The book is kept as one JSON string (<see cref="FriendCategoryBook.ToJson"/>) so the file holds a single
/// key per account and the format can grow without a migration.</para>
/// </summary>
public static class FriendCategoryStore
{
    private const string ConfigPath = "user://preferences.cfg";
    private const string SectionPrefix = "friend_categories_";
    private const string Key = "data";
    private const string OnlyOnlineKey = "only_online";

    /// <summary>False keeps every change in memory only. <c>--selftest</c> boots against the developer's real
    /// <c>user://</c>, so a check that changes categories switches this off and never touches preferences.cfg.</summary>
    public static bool Persist { get; set; } = true;

    /// <summary>The saved book for this account, or an empty one when there is none (or it cannot be read).</summary>
    public static FriendCategoryBook Load(string agentId)
    {
        var cfg = new ConfigFile();
        if (cfg.Load(ConfigPath) != Error.Ok) return new FriendCategoryBook();
        string section = SectionPrefix + agentId;
        if (!cfg.HasSectionKey(section, Key)) return new FriendCategoryBook();
        var value = cfg.GetValue(section, Key);
        return FriendCategoryBook.FromJson(value.VariantType == Variant.Type.String ? (string)value : null);
    }

    /// <summary>Whether the list shows only friends who are online. Off by default.</summary>
    public static bool LoadOnlyOnline(string agentId)
    {
        var cfg = new ConfigFile();
        if (cfg.Load(ConfigPath) != Error.Ok) return false;
        return (bool)cfg.GetValue(SectionPrefix + agentId, OnlyOnlineKey, false);
    }

    public static void SaveOnlyOnline(string agentId, bool onlyOnline)
    {
        if (!Persist) return;

        var cfg = new ConfigFile();
        cfg.Load(ConfigPath);
        cfg.SetValue(SectionPrefix + agentId, OnlyOnlineKey, onlyOnline);
        cfg.Save(ConfigPath);
    }

    public static void Save(string agentId, FriendCategoryBook book)
    {
        if (!Persist) return;

        var cfg = new ConfigFile();
        cfg.Load(ConfigPath); // preserve sections owned by other features
        cfg.SetValue(SectionPrefix + agentId, Key, book.ToJson());
        cfg.Save(ConfigPath);
    }
}
