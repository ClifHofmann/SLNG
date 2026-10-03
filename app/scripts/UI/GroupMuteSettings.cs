using System;
using System.Collections.Generic;
using Godot;

namespace SLNG.App.UI;

/// <summary>
/// The one per-group "Receive group chat" switch (M5-3 §4 "Group Chat Mute / Ignore", FEAT-UI-54):
/// which groups' chat the user has turned off, persisted to user://preferences.cfg under its own
/// section — same ConfigFile pattern and file as <see cref="UiSettings"/> / <see cref="ToolbarSettings"/>.
/// <b>Muted</b> here is the inverse of the group info window's <b>Receive group chat</b> checkbox
/// and the Groups tab's Mute button: all three read and write this one store.
///
/// Deliberately a LOCAL, client-side switch and not the group's server-side <c>AcceptNotices</c>
/// flag: those are different things. AcceptNotices is membership state that follows the account to
/// every viewer and covers group NOTICES; there is no server flag for chat at all. Firestorm's
/// "Receive group chat" is per viewer too (its own mute list on Second Life, a local file on
/// OpenSim) — see docs/specs/FEAT-UI-54-group-info.md.
///
/// What "off" means: nothing of the group's chat is shown, counted or logged by this client, its
/// tab is closed, and its chat session is left so the simulator stops sending it
/// (<c>GridSession.SetGroupChatReceiving</c> / <c>GridSession.GroupChatIgnored</c>).
///
/// Static because the Groups panel, the group info window, <see cref="ChatWindow"/> and the network
/// thread (through <c>GridSession.GroupChatIgnored</c>) need the same answer, and there is exactly
/// one user. Reads come from a network thread, so the set is guarded by a lock.
/// </summary>
public static class GroupMuteSettings
{
    private const string ConfigPath = "user://preferences.cfg";
    private const string Section = "group_mute";

    private static readonly object _lock = new();
    private static readonly HashSet<Guid> _muted = new();
    private static bool _loaded;

    /// <summary>False keeps every change in memory only. <c>--selftest</c> boots against the
    /// developer's real <c>user://</c>, so its checks switch this off and never touch
    /// preferences.cfg.</summary>
    public static bool Persist { get; set; } = true;

    /// <summary>Raised when a group's mute state changes, so an open Groups list or group info window
    /// can follow without polling. Raised on the thread that called <see cref="SetMuted"/> (the main
    /// thread).</summary>
    public static event Action<Guid, bool>? MuteChanged;

    private static void EnsureLoaded()
    {
        // Caller holds _lock.
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

    /// <summary>Reads the stored set now. The first read otherwise happens wherever it is first
    /// needed, which can be a network thread; calling this on the main thread at login keeps the
    /// file access off it.</summary>
    public static void Prime()
    {
        lock (_lock) EnsureLoaded();
    }

    /// <summary>True when this group's chat is switched off (the checkbox is unchecked).</summary>
    public static bool IsMuted(Guid groupId)
    {
        lock (_lock)
        {
            EnsureLoaded();
            return _muted.Contains(groupId);
        }
    }

    /// <summary>True when this group's chat is switched on (the checkbox is checked).</summary>
    public static bool ReceivesChat(Guid groupId) => !IsMuted(groupId);

    public static void SetMuted(Guid groupId, bool muted)
    {
        if (groupId == Guid.Empty) return;

        lock (_lock)
        {
            EnsureLoaded();
            if (muted ? !_muted.Add(groupId) : !_muted.Remove(groupId)) return; // no change

            if (Persist)
            {
                var cfg = new ConfigFile();
                cfg.Load(ConfigPath); // preserve sections owned by other features
                var key = groupId.ToString();
                if (muted) cfg.SetValue(Section, key, true);
                else if (cfg.HasSectionKey(Section, key)) cfg.EraseSectionKey(Section, key);
                cfg.Save(ConfigPath);
            }
        }

        MuteChanged?.Invoke(groupId, muted);
    }
}
