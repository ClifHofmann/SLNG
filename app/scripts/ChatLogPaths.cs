using System.Collections.Generic;
using System.IO;
using SLNG.Core.ChatLogs;
using SLNG.Core.Services;

namespace SLNG.App;

/// <summary>
/// FEAT-UI-41: the app's door to "where does this login's chat log go". Only builds paths and
/// <em>reads</em> Firestorm's settings files; it creates nothing, so it is as safe on the login screen
/// as <see cref="GridData"/> is. The rules are in <see cref="FirestormLogLayout"/> /
/// <see cref="ChatLogLocator"/> / <see cref="GridLabels"/> (SLNG.Core, unit-tested); this class only
/// feeds them the machine's folders and the user's setting.
/// </summary>
public static class ChatLogPaths
{
    /// <summary>The sender on system lines: "Second Life" on a Linden grid, "Grid" on any other
    /// (<c>SYSTEM_FROM</c> in Firestorm's <c>llworld.cpp</c>).</summary>
    public static string SystemName(string? gridUri)
        => GridLabels.LindenLabel(gridUri) is null ? "Grid" : "Second Life";

    /// <summary>Firestorm's grid lists, as text -- the label it stored for a grid is the one its
    /// folders carry. Missing files read as nothing.</summary>
    public static IReadOnlyList<string?> FirestormGridLists(string profileDirectory)
    {
        string dir = Path.Combine(profileDirectory, ChatLogLocator.UserSettingsFolder);
        return new[]
        {
            LlsdSettingsFile.ReadFile(Path.Combine(dir, "grids.user.xml")),
            LlsdSettingsFile.ReadFile(Path.Combine(dir, "grids.remote.xml")),
        };
    }

    /// <summary>True when this grid's label can only come from asking the grid (one small GET at
    /// login): not a Linden grid and not in Firestorm's lists.</summary>
    public static bool NeedsProbe(string gridUri)
        => GridLabels.NeedsProbe(gridUri, FirestormGridLists(ChatLogger.DefaultFirestormProfileDirectory()));

    /// <summary>The folder and naming for a login under the current Preferences.
    /// <paramref name="probedGridName"/> is what the grid itself reported, or null.</summary>
    public static ChatLogTarget ResolveCurrent(string gridUri, string firstName, string lastName, string? probedGridName)
        => Resolve(gridUri, firstName, lastName, probedGridName,
            ChatLogSettings.EffectiveMode, ChatLogSettings.ImNames, ChatLogSettings.CustomFolder,
            ChatLogger.DefaultFirestormProfileDirectory(), ChatLogger.DefaultLogDirectory());

    /// <summary>The same with everything given -- what the smoke test calls with folders that do not
    /// exist, to prove that resolving creates nothing.</summary>
    public static ChatLogTarget Resolve(string gridUri, string firstName, string lastName, string? probedGridName,
        ChatLogMode mode, ImNamesChoice names, string? customFolder, string profileDirectory, string slngDirectory)
    {
        string label = GridLabels.Resolve(gridUri, FirestormGridLists(profileDirectory), probedGridName);
        string account = FirestormLogLayout.AccountFolderName(firstName, lastName, label);
        return ChatLogLocator.Resolve(mode, profileDirectory, slngDirectory, customFolder, account, names, SystemName(gridUri));
    }

    /// <summary>The folders older SLNG builds wrote this login's logs into: the loose files in the
    /// chat root (every account, every grid, before v0.26.13) and v0.26.13's per-grid, per-account
    /// folder. Only read by the import, never changed.</summary>
    public static IReadOnlyList<string> OldSources(string gridUri, string firstName, string lastName)
        => new[] { ChatLogger.DefaultLogDirectory(), GridData.LegacyChatLogDirectory(gridUri, firstName, lastName) };
}
