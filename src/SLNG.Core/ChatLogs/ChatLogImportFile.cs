namespace SLNG.Core.ChatLogs;

/// <summary>What happened to one old file when planning an import.</summary>
/// <param name="Source">The old file.</param>
/// <param name="Destination">The file in the new layout it goes to; null if it is skipped.</param>
/// <param name="Kind">"nearby", "IM", "group" or "skipped".</param>
/// <param name="ToImport">Messages that are not in the destination yet.</param>
/// <param name="AlreadyThere">Messages that were already in the destination (an earlier import, or
/// the same chat logged by the other viewer).</param>
/// <param name="SkipReason">Why a file was skipped; null otherwise.</param>
public sealed record ChatLogImportFile(
    string Source, string? Destination, string Kind, int ToImport, int AlreadyThere, string? SkipReason);
