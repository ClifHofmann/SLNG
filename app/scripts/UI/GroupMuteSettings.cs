using System;
using System.Collections.Generic;
using Godot;

namespace SLNG.App.UI;

/// <summary>
/// Which groups' chat the user has muted (M5-3 §4, "Group Chat Mute / Ignore"), persisted to
/// user://preferences.cfg under its own section — same ConfigFile pattern and file as
/// <see cref="UiSettings"/> / <see cref="ToolbarSettings"/>.
///
/// Deliberately a LOCAL, client-side mute and not the group's server-side
/// <c>AcceptNotices</c> flag: those are different things. AcceptNotices is membership state that
/// follows the account to every viewer and covers group NOTICES; this only silences a group's
/// live chat in this client, which is what "I'm in this group but its chat is too busy right now"
/// actually means.
///
/// Static because both <see cref="GroupsPanel"/> (toggling) and <see cref="ChatWindow"/>
/// (suppressing tab-open and unread badges) need the same answer, and there is exactly one user.
/// </summary>
public static class GroupMuteSettings
{
    private const string ConfigPath = "user://preferences.cfg";
    private const string Section = "group_mute";

    private static readonly HashSet<Guid> _muted = new();
    private static bool _loaded;

    /// <summary>Raised when a group's mute state changes, so an open Groups list can restyle its
    /// row without polling.</summary>
    public static event Action<Guid, bool>? MuteChanged;

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        var cfg = new ConfigFile();
        if (cfg.Load(ConfigPath) != Error.Ok) return;
        // HasSection first: GetSectionKeys on a missing section prints a Godot error, and on a
        // fresh profile that section never exists.
        if (!cfg.HasSection(Section)) return;
        foreach (var key in cfg.GetSectionKeys(Section))
        {
            if (Guid.TryParse(key, out var id) && (bool)cfg.GetValue(Section, key, false))
                _muted.Add(id);
        }
    }

    public static bool IsMuted(Guid groupId)
    {
        EnsureLoaded();
        return _muted.Contains(groupId);
    }

    public static void SetMuted(Guid groupId, bool muted)
    {
        EnsureLoaded();
        if (groupId == Guid.Empty) return;
        if (muted ? !_muted.Add(groupId) : !_muted.Remove(groupId)) return; // no change

        var cfg = new ConfigFile();
        cfg.Load(ConfigPath); // preserve sections owned by other features
        var key = groupId.ToString();
        if (muted) cfg.SetValue(Section, key, true);
        else if (cfg.HasSectionKey(Section, key)) cfg.EraseSectionKey(Section, key);
        cfg.Save(ConfigPath);

        MuteChanged?.Invoke(groupId, muted);
    }
}
