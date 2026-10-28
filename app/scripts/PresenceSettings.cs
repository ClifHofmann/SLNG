using Godot;
using SLNG.Core.Presence;

namespace SLNG.App;

/// <summary>
/// FEAT-UI-75: what survives a restart of the Away / autorespond setup -- the two autorespond modes (Firestorm keeps
/// <c>FSAutorespondMode</c> / <c>FSAutorespondNonFriendsMode</c>), the "answer when Away" switch, the four texts and the
/// idle timeout (<c>AFKTimeout</c>). Away and Do Not Disturb themselves are deliberately not kept: logging in already
/// busy, with no sign of it on screen, is how a person ends up ignoring everyone for a week.
/// Stored in <c>preferences.cfg</c> section <c>presence</c>, the same <c>ConfigFile</c> pattern as
/// <see cref="ChatLogSettings"/>; nothing is written on the boot path, only when the person changes something.
/// </summary>
public static class PresenceSettings
{
    private const string ConfigPath = "user://preferences.cfg";
    private const string Section = "presence";

    /// <summary>The viewer's <c>AFKTimeout</c> default (settings.xml): five minutes.</summary>
    public const int DefaultAwayTimeoutSeconds = 300;

    /// <summary>The choices the reference viewer's Preferences offers (0 = never).</summary>
    public static readonly int[] AwayTimeoutChoices = { 0, 120, 300, 600, 1800, 3600, 5400, 7200 };

    public static int AwayTimeoutSeconds { get; private set; } = DefaultAwayTimeoutSeconds;

    /// <summary>Whether losing the window's focus sends the agent Away. The reference viewer does it whenever its timeout
    /// is on; here it is its own switch, off by default -- looking at another window (a second client, a browser) is not
    /// being away, and anyone who tests with two viewers sees the avatar go Away the moment the other one is clicked.</summary>
    public static bool AwayOnFocusLoss { get; private set; }

    /// <summary>The persisted part of the configuration; Away and Do Not Disturb start off.</summary>
    public static PresenceConfig Load()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(ConfigPath) != Error.Ok) return new PresenceConfig();

        string Text(string key, string fallback)
        {
            string v = (string)cfg.GetValue(Section, key, "");
            return string.IsNullOrWhiteSpace(v) ? fallback : v;
        }

        AwayTimeoutSeconds = (int)cfg.GetValue(Section, "away_timeout", DefaultAwayTimeoutSeconds);
        AwayOnFocusLoss = (bool)cfg.GetValue(Section, "away_on_focus_loss", false);
        return new PresenceConfig(
            Autorespond: (bool)cfg.GetValue(Section, "autorespond", false),
            AutorespondNonFriends: (bool)cfg.GetValue(Section, "autorespond_non_friends", false),
            SendAwayResponse: (bool)cfg.GetValue(Section, "send_away_response", false),
            AwayMessage: Text("away_message", PresenceConfig.DefaultAwayMessage),
            BusyMessage: Text("busy_message", PresenceConfig.DefaultBusyMessage),
            AutorespondMessage: Text("autorespond_message", PresenceConfig.DefaultAutorespondMessage),
            NonFriendsMessage: Text("non_friends_message", PresenceConfig.DefaultAutorespondMessage));
    }

    public static void Save(PresenceConfig config, int awayTimeoutSeconds, bool awayOnFocusLoss)
    {
        AwayTimeoutSeconds = awayTimeoutSeconds;
        AwayOnFocusLoss = awayOnFocusLoss;
        var cfg = new ConfigFile();
        cfg.Load(ConfigPath); // preserve sections owned by other settings classes
        cfg.SetValue(Section, "away_timeout", awayTimeoutSeconds);
        cfg.SetValue(Section, "away_on_focus_loss", awayOnFocusLoss);
        cfg.SetValue(Section, "autorespond", config.Autorespond);
        cfg.SetValue(Section, "autorespond_non_friends", config.AutorespondNonFriends);
        cfg.SetValue(Section, "send_away_response", config.SendAwayResponse);
        cfg.SetValue(Section, "away_message", config.AwayMessage);
        cfg.SetValue(Section, "busy_message", config.BusyMessage);
        cfg.SetValue(Section, "autorespond_message", config.AutorespondMessage);
        cfg.SetValue(Section, "non_friends_message", config.NonFriendsMessage);
        cfg.Save(ConfigPath);
    }
}
