using System.Collections.Generic;
using Godot;
using SLNG.Core.Login;

namespace SLNG.App;

/// <summary>
/// FEAT-UI-72: where a login profile keeps its "Start at" choice and its favorites cache in <c>logins.cfg</c> --
/// two extra keys in the profile's own <c>[first last @ grid]</c> section, next to <c>pass_hash</c> and
/// <c>mfa_hash</c>, with the same lifetime: they disappear with the profile, and unticking "Save Login" erases them
/// the way it erases the password.
///
/// <list type="bullet">
/// <item><see cref="ChoiceKey"/> (<c>start</c>): <c>last</c> | <c>home</c> | <c>fav:&lt;asset guid&gt;</c> | <c>region:&lt;text&gt;</c>
/// (<see cref="StartChoice.Serialize"/>).</item>
/// <item><see cref="FavoritesKey"/> (<c>start_favorites</c>): one line of JSON (<see cref="LoginFavoriteBook"/>); the key
/// being present means "written at least once" even when the list is empty.</item>
/// </list>
///
/// Operates on a <see cref="ConfigFile"/> it is handed and never reads or writes a path, so the self-test runs it on an
/// in-memory file; saving is the caller's job, on the main thread (a ConfigFile is not thread-safe).
///
/// <para><b>Compatibility.</b> A <c>logins.cfg</c> written before this existed has neither key and reads as
/// "last / never populated". An older build never looks them up, and Godot's ConfigFile keeps keys it does not know when
/// it rewrites a section.</para>
///
/// <para><b>Protection.</b> Nothing secret is stored here: a choice, and the names and positions of landmarks the person
/// already has in their own inventory.</para>
/// </summary>
internal static class LoginStartStore
{
    /// <summary>The key holding the remembered choice.</summary>
    public const string ChoiceKey = "start";

    /// <summary>The key holding the favorites cache.</summary>
    public const string FavoritesKey = "start_favorites";

    /// <summary>Longest favorites value read back (64 entries of generous names and regions is ~40 KB). A hostile or
    /// corrupted file's megabyte string reads as an empty list instead of being parsed.</summary>
    public const int MaxFavoritesLength = 256 * 1024;

    /// <summary>The remembered choice for <paramref name="section"/>; <see cref="StartChoice.Last"/> when there is none,
    /// the section does not exist, or the value is not a string we would have written.</summary>
    public static StartChoice ReadChoice(ConfigFile config, string section)
    {
        string? text = ReadString(config, section, ChoiceKey, StartChoice.MaxSerializedLength);
        return text is null ? StartChoice.Last : StartChoice.Parse(text);
    }

    /// <summary>Stores <paramref name="choice"/> under <paramref name="section"/>. Returns true when the file's contents
    /// changed (so the caller knows whether a save is needed). An unreadable choice is stored as <c>last</c>.</summary>
    public static bool WriteChoice(ConfigFile config, string section, StartChoice choice)
    {
        if (string.IsNullOrEmpty(section)) return false;
        string text = StartChoice.Parse(choice.Serialize()).Serialize();
        if (config.HasSectionKey(section, ChoiceKey) && ReadString(config, section, ChoiceKey, int.MaxValue) == text)
            return false;
        config.SetValue(section, ChoiceKey, text);
        return true;
    }

    /// <summary>True when the profile's favorites have been written at least once (the key is there), even if the folder
    /// was empty. False for a profile that never completed a session with the cache switched on -- the login screen then
    /// says the favorites will appear after the next login.</summary>
    public static bool FavoritesPopulated(ConfigFile config, string section)
        => !string.IsNullOrEmpty(section) && config.HasSection(section) && config.HasSectionKey(section, FavoritesKey);

    /// <summary>The cached favorites for <paramref name="section"/>; empty when there are none or the value is unreadable.</summary>
    public static IReadOnlyList<LoginFavorite> ReadFavorites(ConfigFile config, string section)
    {
        string? json = ReadString(config, section, FavoritesKey, MaxFavoritesLength);
        return json is null ? System.Array.Empty<LoginFavorite>() : LoginFavoriteBook.FromJson(json);
    }

    /// <summary>Stores the favorites under <paramref name="section"/>. Returns true when the file's contents changed (so a
    /// save is needed). Writing an empty list is meaningful: it records a confirmed empty folder.</summary>
    public static bool WriteFavorites(ConfigFile config, string section, IReadOnlyList<LoginFavorite> favorites)
    {
        if (string.IsNullOrEmpty(section)) return false;
        string json = LoginFavoriteBook.ToJson(favorites);
        if (config.HasSectionKey(section, FavoritesKey) && ReadString(config, section, FavoritesKey, int.MaxValue) == json)
            return false;
        config.SetValue(section, FavoritesKey, json);
        return true;
    }

    /// <summary>Removes both keys. Returns true when something was removed. A missing section or key is a no-op -- in Godot
    /// erasing one that does not exist is an error.</summary>
    public static bool Erase(ConfigFile config, string section)
    {
        if (string.IsNullOrEmpty(section) || !config.HasSection(section)) return false;
        bool changed = false;
        foreach (string key in new[] { ChoiceKey, FavoritesKey })
        {
            if (!config.HasSectionKey(section, key)) continue;
            config.EraseSectionKey(section, key);
            changed = true;
        }
        return changed;
    }

    /// <summary>The key's value when it exists, is a string and is not longer than <paramref name="maxLength"/>; otherwise null.</summary>
    private static string? ReadString(ConfigFile config, string section, string key, int maxLength)
    {
        if (string.IsNullOrEmpty(section) || !config.HasSection(section) || !config.HasSectionKey(section, key)) return null;
        var value = config.GetValue(section, key, "");
        if (value.VariantType != Variant.Type.String) return null;
        string text = value.AsString();
        return text.Length > maxLength ? null : text;
    }
}
