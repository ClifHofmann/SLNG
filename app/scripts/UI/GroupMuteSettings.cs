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
    private const string SectionPrefix = "group_mute_";
    private const string LegacySection = "group_mute";

    private static readonly object _lock = new();
    private static readonly HashSet<Guid> _muted = new();
    private static bool _loaded;
    private static string? _gridSlug;
    private static string? _agentId;

    /// <summary>False keeps every change in memory only. <c>--selftest</c> boots against the
    /// developer's real <c>user://</c>, so its checks switch this off and never touch
    /// preferences.cfg.</summary>
    public static bool Persist { get; set; } = true;

    /// <summary>Raised when a group's mute state changes, so an open Groups list or group info window
    /// can follow without polling. Raised on the thread that called <see cref="SetMuted"/> (the main
    /// thread).</summary>
    public static event Action<Guid, bool>? MuteChanged;

    public static string? GetSection(string? gridSlug, string? agentId)
    {
        if (string.IsNullOrWhiteSpace(gridSlug) || string.IsNullOrWhiteSpace(agentId))
            return null;

        if (Guid.TryParse(agentId, out var guid) && guid == Guid.Empty)
            return null;

        return $"{SectionPrefix}{gridSlug.Trim().ToLowerInvariant()}_{agentId.Trim().ToLowerInvariant()}";
    }

    public static void Initialize(string? gridSlug, string? agentId)
    {
        lock (_lock)
        {
            _gridSlug = gridSlug;
            _agentId = agentId;
            _muted.Clear();
            _loaded = false;
            EnsureLoaded();
        }
    }

    public static void Reset()
    {
        lock (_lock)
        {
            _gridSlug = null;
            _agentId = null;
            _muted.Clear();
            _loaded = false;
        }
    }

    private static void EnsureLoaded()
    {
        // Caller holds _lock.
        if (_loaded) return;
        _loaded = true;

        string? section = GetSection(_gridSlug, _agentId);
        if (section == null) return;

        var cfg = new ConfigFile();
        if (cfg.Load(ConfigPath) != Error.Ok) return;

        // One-time cleanup of legacy/corrupted empty-guid sections
        bool dirty = false;
        if (cfg.HasSection("group_mute_00000000-0000-0000-0000-000000000000"))
        {
            cfg.EraseSection("group_mute_00000000-0000-0000-0000-000000000000");
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

        // Migrate legacy un-scoped [group_mute] if target section does not exist yet
        if (!cfg.HasSection(section) && cfg.HasSection(LegacySection))
        {
            foreach (var key in cfg.GetSectionKeys(LegacySection))
            {
                cfg.SetValue(section, key, cfg.GetValue(LegacySection, key));
            }
            cfg.EraseSection(LegacySection);
            dirty = true;
        }

        if (dirty && Persist)
        {
            cfg.Save(ConfigPath);
        }

        if (!cfg.HasSection(section)) return;
        foreach (var key in cfg.GetSectionKeys(section))
        {
            if (Guid.TryParse(key, out var id) && (bool)cfg.GetValue(section, key, false))
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

            string? section = GetSection(_gridSlug, _agentId);
            if (Persist && section != null)
            {
                var cfg = new ConfigFile();
                cfg.Load(ConfigPath); // preserve sections owned by other features
                var key = groupId.ToString();
                if (muted) cfg.SetValue(section, key, true);
                else if (cfg.HasSectionKey(section, key)) cfg.EraseSectionKey(section, key);
                cfg.Save(ConfigPath);
            }
        }

        MuteChanged?.Invoke(groupId, muted);
    }
}
