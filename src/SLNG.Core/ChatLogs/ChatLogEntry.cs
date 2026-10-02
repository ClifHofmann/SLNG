namespace SLNG.Core.ChatLogs;

/// <summary>
/// One logged message, read back from a log file. A message can span several physical lines
/// (<see cref="FirestormLogFormat"/>); this is the whole of it.
/// </summary>
/// <param name="Timestamp">The text inside the brackets exactly as written
/// (<c>2026/04/11 08:07</c>, <c>2026/09/12 18:58:51</c>, <c>8:07 AM</c>), or empty when the line
/// had none.</param>
/// <param name="Time">The date and time when the stamp carried both; otherwise null. Wall-clock
/// time in whatever zone the writer used -- Firestorm writes Second Life time.</param>
/// <param name="From">The sender, percent-decoded; empty when the line had no name.</param>
/// <param name="Text">The message; continuation lines are joined with <c>\n</c>.</param>
public sealed record ChatLogEntry(string Timestamp, DateTime? Time, string From, string Text)
{
    /// <summary>One display line: <c>[stamp] Name: text</c>, leaving out whichever part the log
    /// did not have. A multi-line message keeps its line breaks.</summary>
    public string Display()
    {
        string stamp = Timestamp.Length > 0 ? "[" + Timestamp + "] " : "";
        return From.Length > 0 ? $"{stamp}{From}: {Text}" : stamp + Text;
    }
}
