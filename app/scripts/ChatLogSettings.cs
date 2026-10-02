using System;
using System.IO;
using Godot;
using SLNG.Core.ChatLogs;
using SLNG.Core.Services;

namespace SLNG.App;

/// <summary>
/// FEAT-UI-41: where chat logs are kept (Preferences -> Chat logs). Same <c>ConfigFile</c> pattern as
/// <see cref="MediaSettings"/>, in <c>preferences.cfg</c>.
///
/// <para><b>The default is decided once, at the first login</b>, not at startup and not on every
/// run: Firestorm's profile folder (<c>%APPDATA%\Firestorm_x64</c>) when it exists, SLNG's own folder
/// otherwise. Writing it down at that moment is what stops a person who installs Firestorm
/// <em>later</em> from having SLNG silently switch to a second history. Nothing is written on the
/// boot path (<c>--selftest</c> boots against the real <c>user://</c>): until the first login the
/// default is only <em>computed</em> (<see cref="EffectiveMode"/>).</para>
/// </summary>
public static class ChatLogSettings
{
    private const string ConfigPath = "user://preferences.cfg";
    private const string Section = "chat_logs";

    /// <summary>The stored choice, or null when none was made yet.</summary>
    public static ChatLogMode? StoredMode { get; private set; }

    /// <summary>The folder for <see cref="ChatLogMode.Custom"/>; empty until one is entered.</summary>
    public static string CustomFolder { get; private set; } = "";

    public static ImNamesChoice ImNames { get; private set; } = ImNamesChoice.Auto;

    public static event Action? Changed;

    /// <summary>What applies right now: the stored choice, else the default.</summary>
    public static ChatLogMode EffectiveMode => StoredMode ?? DefaultMode();

    /// <summary>Firestorm when its profile folder exists, SLNG's own folder otherwise.</summary>
    public static ChatLogMode DefaultMode()
        => Directory.Exists(ChatLogger.DefaultFirestormProfileDirectory()) ? ChatLogMode.Firestorm : ChatLogMode.Slng;

    public static void Load()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(ConfigPath) != Error.Ok) return;

        StoredMode = ParseMode((string)cfg.GetValue(Section, "mode", ""));
        CustomFolder = (string)cfg.GetValue(Section, "custom_folder", "");
        ImNames = ParseNames((string)cfg.GetValue(Section, "im_names", "auto"));
    }

    /// <summary>Writes the default down if no choice was ever made. Called at login, never at boot.</summary>
    public static void DecideIfNeeded()
    {
        if (StoredMode is null) SetMode(DefaultMode());
    }

    public static void SetMode(ChatLogMode mode) => Persist("mode", ModeKey(mode), () => StoredMode = mode);
    public static void SetCustomFolder(string folder) => Persist("custom_folder", folder.Trim(), () => CustomFolder = folder.Trim());
    public static void SetImNames(ImNamesChoice choice) => Persist("im_names", NamesKey(choice), () => ImNames = choice);

    private static void Persist(string key, string value, Action assign)
    {
        assign();
        Changed?.Invoke();

        var cfg = new ConfigFile();
        cfg.Load(ConfigPath); // preserve sections owned by other settings classes
        cfg.SetValue(Section, key, value);
        cfg.Save(ConfigPath);
    }

    private static string ModeKey(ChatLogMode m) => m switch
    {
        ChatLogMode.Slng => "slng",
        ChatLogMode.Custom => "custom",
        _ => "firestorm",
    };

    private static ChatLogMode? ParseMode(string s) => s switch
    {
        "firestorm" => ChatLogMode.Firestorm,
        "slng" => ChatLogMode.Slng,
        "custom" => ChatLogMode.Custom,
        _ => null,
    };

    private static string NamesKey(ImNamesChoice c) => c switch
    {
        ImNamesChoice.Legacy => "legacy",
        ImNamesChoice.Account => "account",
        _ => "auto",
    };

    private static ImNamesChoice ParseNames(string s) => s switch
    {
        "legacy" => ImNamesChoice.Legacy,
        "account" => ImNamesChoice.Account,
        _ => ImNamesChoice.Auto,
    };
}
