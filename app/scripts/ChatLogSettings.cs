using System;
using Godot;
using SLNG.Core.ChatLogs;

namespace SLNG.App;

/// <summary>
/// FEAT-UI-41: where chat logs are kept -- one base folder PER ACCOUNT, and how IM files are named.
/// Stored in <c>preferences.cfg</c> (same <c>ConfigFile</c> pattern as <see cref="MediaSettings"/>):
/// section <c>chat_log_dirs</c>, key = <see cref="ChatLogAccountKey.Of"/> (grid + account), value = the
/// base folder; and <c>chat_logs/im_names</c> (<c>legacy</c> | <c>account</c>).
///
/// <para><b>Why not logins.cfg:</b> that file is Boot's, holds the password hashes, is rewritten whole in
/// several places, and treats every section but <c>Settings</c>/<c>Window</c> as a login profile -- a new
/// section would show up in the login screen's profile list. It also only has accounts that were
/// <em>saved</em>, while a one-off login needs a folder too. A key derived from the same two things a
/// profile is made of (grid and name) needs neither.</para>
///
/// <para>Nothing is written on the boot path (<c>--selftest</c> boots against the real <c>user://</c>):
/// <see cref="Load"/> only reads, and a value is written when the person changes it. An account with
/// nothing chosen uses SLNG's own folder; SLNG does not look at any other viewer's installation.</para>
/// </summary>
public static class ChatLogSettings
{
    private const string ConfigPath = "user://preferences.cfg";
    private const string DirsSection = "chat_log_dirs";
    private const string Section = "chat_logs";

    private static ChatLogFolderMap _folders = new();

    public static ImLogNameStyle ImStyle { get; private set; } = ImLogNameStyle.Legacy;

    public static event Action? Changed;

    /// <summary>The base folder chosen for an account, or null (use SLNG's default).</summary>
    public static string? FolderFor(string accountKey) => _folders.Get(accountKey);

    public static void Load()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(ConfigPath) != Error.Ok) return;

        var map = new ChatLogFolderMap();
        if (cfg.HasSection(DirsSection))
        {
            foreach (string key in cfg.GetSectionKeys(DirsSection))
                map.Set(key, (string)cfg.GetValue(DirsSection, key, ""));
        }
        _folders = map;

        // "auto" (an earlier build: follow Firestorm's setting) is gone; it reads as the default.
        ImStyle = (string)cfg.GetValue(Section, "im_names", "legacy") == "account" ? ImLogNameStyle.Account : ImLogNameStyle.Legacy;
    }

    public static void SetFolder(string accountKey, string folder)
    {
        _folders.Set(accountKey, folder);
        Save(cfg =>
        {
            string? now = _folders.Get(accountKey);
            if (now is null)
            {
                if (cfg.HasSectionKey(DirsSection, accountKey)) cfg.EraseSectionKey(DirsSection, accountKey);
            }
            else
            {
                cfg.SetValue(DirsSection, accountKey, now);
            }
        });
    }

    /// <summary>Back to SLNG's default folder for this account.</summary>
    public static void ClearFolder(string accountKey) => SetFolder(accountKey, "");

    public static void SetImStyle(ImLogNameStyle style)
    {
        ImStyle = style;
        Save(cfg => cfg.SetValue(Section, "im_names", style == ImLogNameStyle.Account ? "account" : "legacy"));
    }

    private static void Save(Action<ConfigFile> change)
    {
        var cfg = new ConfigFile();
        cfg.Load(ConfigPath); // preserve sections owned by other settings classes
        change(cfg);
        cfg.Save(ConfigPath);
        Changed?.Invoke();
    }
}
