namespace SLNG.Core.ChatLogs;

/// <summary>One append the import will make: whole records with their line endings.</summary>
public sealed record ChatLogPlannedAppend(string DestinationPath, string Block);
