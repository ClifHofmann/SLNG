using Godot;
using SLNG.Core;
using SLNG.Core.Presence;

namespace SLNG.App;

/// <summary>
/// FEAT-UI-75 / BUG-UI-43: what survives a restart of the Away / autorespond setup -- the two autorespond modes (Firestorm
/// keeps <c>FSAutorespondMode</c> / <c>FSAutorespondNonFriendsMode</c>), the "answer when Away" switch, the four texts and the
/// idle timeout (<c>AFKTimeout</c>), all of them per account in the reference viewer. Away and Do Not Disturb themselves are
/// deliberately not kept: logging in already busy, with no sign of it on screen, is how a person ends up ignoring everyone
/// for a week.
///
/// <para>Stored in <c>preferences.cfg</c>, section <c>presence_&lt;grid&gt;_&lt;agentId&gt;</c>, one per login; the rules
/// (the key, the one-time copy of the old shared <c>presence</c> section, "no login, no file access") are
/// <see cref="PresenceSettingsStore"/>, this class only puts a Godot <c>ConfigFile</c> under them. It holds no state of its
/// own: what is loaded lives in <see cref="PresenceController"/>, which drops it at logout. Nothing is written on the boot
/// path, only when the person changes something.</para>
/// </summary>
public static class PresenceSettings
{
    /// <summary>Where the setup lives. A smoke test points it at a throwaway file so it cannot touch the real one.</summary>
    internal static string ConfigPath { get; set; } = "user://preferences.cfg";

    /// <summary>The viewer's <c>AFKTimeout</c> default (settings.xml): five minutes.</summary>
    public const int DefaultAwayTimeoutSeconds = PresenceSettingsStore.DefaultAwayTimeoutSeconds;

    /// <summary>The choices the reference viewer's Preferences offers (0 = never).</summary>
    public static readonly int[] AwayTimeoutChoices = { 0, 120, 300, 600, 1800, 3600, 5400, 7200 };

    /// <summary>The persisted part of the configuration of this login; Away and Do Not Disturb start off. Defaults when the
    /// login is not known.</summary>
    public static PresenceSettingsData Load(string? gridSlug, string? agentId)
        => PresenceSettingsStore.Load(new GodotConfigStore(ConfigPath), gridSlug, agentId);

    /// <summary>Writes this login's setup; does nothing without a login.</summary>
    public static void Save(string? gridSlug, string? agentId, PresenceSettingsData data)
        => PresenceSettingsStore.Save(new GodotConfigStore(ConfigPath), gridSlug, agentId, data);
}
