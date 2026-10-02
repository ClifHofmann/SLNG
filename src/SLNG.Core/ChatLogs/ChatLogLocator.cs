namespace SLNG.Core.ChatLogs;

/// <summary>
/// FEAT-UI-41: picks the folder and naming for one login. Reads (never writes) two of Firestorm's
/// settings files, only when following Firestorm:
/// <list type="bullet">
/// <item><c>&lt;profile&gt;/&lt;account folder&gt;/settings_per_account.xml</c>:
/// <c>InstantMessageLogPath</c> (where THIS account's logs live -- empty means the profile folder;
/// <c>llstartup.cpp</c> <c>setChatLogsDir</c>; the maintainer's Second Life account points at a
/// OneDrive folder) and <c>LogFileNamewithDate</c>;</item>
/// <item><c>&lt;profile&gt;/user_settings/settings.xml</c>: <c>UseLegacyIMLogNames</c>.</item>
/// </list>
/// A configured path that does not exist is ignored, as the viewer does (<c>FIRE-18247</c>: it falls
/// back to the profile folder). Nothing is created here.
/// </summary>
public static class ChatLogLocator
{
    public const string PerAccountSettingsFile = "settings_per_account.xml";
    public const string UserSettingsFolder = "user_settings";
    public const string GlobalSettingsFile = "settings.xml";

    public static ChatLogTarget Resolve(
        ChatLogMode mode,
        string firestormProfileDirectory,
        string slngOwnDirectory,
        string? customDirectory,
        string accountFolder,
        ImNamesChoice imNames,
        string systemName)
    {
        string? globalSettings = string.IsNullOrWhiteSpace(firestormProfileDirectory)
            ? null
            : LlsdSettingsFile.ReadFile(Path.Combine(firestormProfileDirectory, UserSettingsFolder, GlobalSettingsFile));

        ImLogNameStyle style = imNames switch
        {
            ImNamesChoice.Legacy => ImLogNameStyle.Legacy,
            ImNamesChoice.Account => ImLogNameStyle.Account,
            // The control's default is TRUE in the viewer source; the settings file only lists a
            // value that differs from the default, so a missing entry is legacy names.
            _ => (LlsdSettingsFile.ReadBool(globalSettings, "UseLegacyIMLogNames") ?? true)
                ? ImLogNameStyle.Legacy
                : ImLogNameStyle.Account,
        };

        string baseDir;
        bool dateSuffix = false;
        string why;

        switch (mode)
        {
            case ChatLogMode.Slng:
                baseDir = slngOwnDirectory;
                why = "SLNG's own folder";
                break;

            case ChatLogMode.Custom when !string.IsNullOrWhiteSpace(customDirectory):
                baseDir = customDirectory!;
                why = "the folder you chose";
                break;

            default: // Firestorm (and Custom with no folder chosen yet falls back here)
                baseDir = firestormProfileDirectory;
                why = "Firestorm's profile folder";
                string? perAccount = LlsdSettingsFile.ReadFile(
                    Path.Combine(firestormProfileDirectory, accountFolder, PerAccountSettingsFile));
                string? configured = LlsdSettingsFile.ReadValue(perAccount, "InstantMessageLogPath")?.Trim();
                if (!string.IsNullOrEmpty(configured) && Directory.Exists(configured))
                {
                    baseDir = configured;
                    why = "the log folder set in Firestorm for this account";
                }
                dateSuffix = LlsdSettingsFile.ReadBool(perAccount, "LogFileNamewithDate") ?? false;
                break;
        }

        return new ChatLogTarget(
            Path.Combine(baseDir, accountFolder),
            baseDir,
            new ChatLogNaming(style, dateSuffix, systemName),
            why);
    }
}
