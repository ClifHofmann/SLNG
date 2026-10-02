namespace SLNG.Core.ChatLogs;

/// <summary>
/// FEAT-UI-41: puts together the folder and naming for one login. Pure: no file is read, none is
/// created. The base folder is the one the person chose for this account (or SLNG's default); the
/// account's own folder -- named the way Firestorm names it (<see cref="FirestormLogLayout.AccountFolderName"/>)
/// -- is created inside it by the writer, which is how pointing SLNG at the folder Firestorm uses for
/// an account makes both viewers share one history. SLNG does not look at Firestorm's installation
/// or settings to decide any of this.
/// </summary>
public static class ChatLogLocator
{
    /// <param name="chosenBase">The base folder chosen for this account, or null/empty for none.</param>
    /// <param name="defaultBase">SLNG's own folder, used when nothing is chosen.</param>
    public static ChatLogTarget Resolve(
        string? chosenBase,
        string defaultBase,
        string accountFolder,
        ImLogNameStyle imStyle,
        string systemName)
    {
        bool chosen = !string.IsNullOrWhiteSpace(chosenBase);
        string baseDir = chosen ? chosenBase!.Trim() : defaultBase;

        return new ChatLogTarget(
            Path.Combine(baseDir, accountFolder),
            baseDir,
            new ChatLogNaming(imStyle, DateSuffix: false, systemName),
            chosen ? "the folder chosen for this account" : "SLNG's default folder");
    }
}
