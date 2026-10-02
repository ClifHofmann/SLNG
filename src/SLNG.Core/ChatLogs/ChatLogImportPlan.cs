namespace SLNG.Core.ChatLogs;

/// <summary>The outcome of <see cref="ChatLogImporter.Plan"/>: what to append and a per-file
/// report. Nothing has been written when this exists.</summary>
public sealed class ChatLogImportPlan
{
    public List<ChatLogImportFile> Files { get; } = new();
    public List<ChatLogPlannedAppend> Appends { get; } = new();

    public int MessagesToImport => Files.Sum(f => f.ToImport);
    public int MessagesAlreadyThere => Files.Sum(f => f.AlreadyThere);
    public int FilesSkipped => Files.Count(f => f.Kind == "skipped");
}
