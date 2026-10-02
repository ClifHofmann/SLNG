namespace SLNG.Core.ChatLogs;

/// <summary>The result of <see cref="ChatLogLocator.Resolve"/>.</summary>
/// <param name="Directory">The account's log folder -- where files are read and written.</param>
/// <param name="BaseDirectory">The folder that contains every account's folder.</param>
/// <param name="Naming">How to name and stamp files for this login.</param>
/// <param name="Why">One line saying how the folder was chosen, for the log and the Preferences page.</param>
public sealed record ChatLogTarget(string Directory, string BaseDirectory, ChatLogNaming Naming, string Why);
