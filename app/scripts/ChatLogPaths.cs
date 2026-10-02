using System.Collections.Generic;
using SLNG.Core.ChatLogs;
using SLNG.Core.Services;

namespace SLNG.App;

/// <summary>
/// FEAT-UI-41: the app's door to "where does this login's chat log go". Only builds paths; it creates
/// nothing and reads nothing from disk, so it is as safe on the login screen as <see cref="GridData"/>
/// is. The rules are in <see cref="FirestormLogLayout"/> / <see cref="ChatLogLocator"/> /
/// <see cref="GridLabels"/> (SLNG.Core, unit-tested); this class feeds them the account's chosen folder
/// (<see cref="ChatLogSettings"/>) and the machine's default. Nothing here knows whether Firestorm is
/// installed.
/// </summary>
public static class ChatLogPaths
{
    /// <summary>The sender on system lines: "Second Life" on a Linden grid, "Grid" on any other
    /// (<c>SYSTEM_FROM</c> in Firestorm's <c>llworld.cpp</c>).</summary>
    public static string SystemName(string? gridUri)
        => GridLabels.IsLindenGrid(gridUri) ? "Second Life" : "Grid";

    /// <summary>True when this grid's label can only come from asking the grid (one small GET at login).</summary>
    public static bool NeedsProbe(string gridUri) => GridLabels.NeedsProbe(gridUri);

    /// <summary>The folder and naming for a login under the current Preferences.
    /// <paramref name="probedGridName"/> is what the grid itself reported, or null.</summary>
    public static ChatLogTarget ResolveCurrent(string gridUri, string firstName, string lastName, string? probedGridName)
        => Resolve(gridUri, firstName, lastName, probedGridName,
            ChatLogSettings.FolderFor(ChatLogAccountKey.Of(gridUri, firstName, lastName)),
            ChatLogSettings.ImStyle, ChatLogger.DefaultLogDirectory());

    /// <summary>The same with everything given -- what the smoke test calls with folders that do not
    /// exist, to prove that resolving creates nothing.</summary>
    public static ChatLogTarget Resolve(string gridUri, string firstName, string lastName, string? probedGridName,
        string? chosenBase, ImLogNameStyle imStyle, string defaultBase)
    {
        string account = FirestormLogLayout.AccountFolderName(firstName, lastName, GridLabels.Resolve(gridUri, probedGridName));
        return ChatLogLocator.Resolve(chosenBase, defaultBase, account, imStyle, SystemName(gridUri));
    }

    /// <summary>The folders older SLNG builds wrote this login's logs into: the loose files in the
    /// chat root (every account, every grid, before v0.26.13) and v0.26.13's per-grid, per-account
    /// folder. Only read by the import, never changed.</summary>
    public static IReadOnlyList<string> OldSources(string gridUri, string firstName, string lastName)
        => new[] { ChatLogger.DefaultLogDirectory(), GridData.LegacyChatLogDirectory(gridUri, firstName, lastName) };
}
