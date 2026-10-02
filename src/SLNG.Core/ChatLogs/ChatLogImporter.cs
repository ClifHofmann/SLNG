using System.Globalization;
using System.Text;
using SLNG.Core.Services;

namespace SLNG.Core.ChatLogs;

/// <summary>
/// FEAT-UI-41: brings logs written by SLNG's OLDER builds into the Firestorm layout. Old builds
/// wrote <c>%APPDATA%\SLNG\logs\chat\&lt;conversation&gt;.txt</c> (and, in v0.26.13 only,
/// <c>&lt;grid&gt;\&lt;account&gt;\</c>) with <c>[2026/09/12 18:58:51] Name: text</c> in LOCAL time,
/// group files named by the bare group name, and a file named by a bare UUID when a group's name
/// was not known yet.
///
/// <para><b>Design: append with a marker, never merge.</b> Merging by timestamp would mean rewriting
/// a file the other viewer owns. Instead the old messages are appended to the end of the file in the
/// new layout (created if missing), preceded -- only when that file already has content -- by one
/// system line saying how many messages came from an older SLNG log. The cost is that imported
/// history sits after newer lines in a file that already existed; the gain is that nothing the other
/// viewer wrote is ever touched.</para>
///
/// <para><b>Idempotent.</b> A message already in the destination (same stamp, sender and text) is
/// not appended again, counted per occurrence so two genuinely identical messages in one minute
/// survive; importing twice appends nothing the second time, and no marker is written for an empty
/// plan.</para>
///
/// <para><b>What an old file becomes.</b> <c>chat.txt</c> -> <c>chat.txt</c>. Any other file is an IM
/// when its name matches one of its senders (the old writer named an IM after the partner), else a
/// group chat named <c>&lt;stem&gt; (group).txt</c>. That is a guess: an IM in which the partner
/// never spoke is filed as a group, and the report lists what each file became. A UUID-named file
/// is skipped -- its group's name is not in it. Times are converted from the machine's local zone
/// to Second Life time, to minute precision; the sender "System" becomes the grid's system name.
/// The old files are never changed or removed.</para>
/// </summary>
public static class ChatLogImporter
{
    /// <summary>Plans the import of every <c>*.txt</c> directly inside <paramref name="sourceDirectories"/>
    /// into <paramref name="destinationDirectory"/>. Reads, never writes.</summary>
    /// <param name="sourceZone">The zone the old stamps were written in; the machine's by default.</param>
    public static ChatLogImportPlan Plan(
        IEnumerable<string> sourceDirectories,
        string destinationDirectory,
        ChatLogNaming naming,
        DateTime nowUtc,
        TimeZoneInfo? sourceZone = null)
    {
        TimeZoneInfo zone = sourceZone ?? TimeZoneInfo.Local;
        var plan = new ChatLogImportPlan();

        // What each destination will hold once the plan is applied: for every record, how many of it
        // are already there or already planned, so a message is never added twice -- not by a second
        // source directory carrying the same conversation either.
        var seen = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        var hasContent = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var blocks = new Dictionary<string, StringBuilder>(StringComparer.OrdinalIgnoreCase);
        var counts = new Dictionary<string, (int Imported, DateTime? First, DateTime? Last)>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach (string sourceDir in sourceDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(sourceDir)) continue;

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(sourceDir, "*.txt").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (string file in files)
            {
                string stem = Path.GetFileNameWithoutExtension(file);

                if (Guid.TryParse(stem, out _))
                {
                    plan.Files.Add(new ChatLogImportFile(file, null, "skipped", 0, 0,
                        "the file is named by a bare id; the conversation's name is not in it"));
                    continue;
                }

                IReadOnlyList<ChatLogEntry> entries;
                try { entries = FirestormLogFormat.Parse(File.ReadLines(file, Encoding.UTF8)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    plan.Files.Add(new ChatLogImportFile(file, null, "skipped", 0, 0, "the file could not be read"));
                    continue;
                }
                if (entries.Count == 0)
                {
                    plan.Files.Add(new ChatLogImportFile(file, null, "skipped", 0, 0, "the file has no messages"));
                    continue;
                }

                (string kindName, ChatLogKind kind) = Classify(stem, entries);
                // The date suffix is not applied: imported history goes to the plain name (the spec
                // says why).
                string? destName = FirestormLogLayout.FileName(kind, stem, naming with { DateSuffix = false }, DateTime.Now);
                if (destName is null)
                {
                    plan.Files.Add(new ChatLogImportFile(file, null, "skipped", 0, 0, "no file name can be made from it"));
                    continue;
                }
                string dest = Path.Combine(destinationDirectory, destName);

                if (!seen.TryGetValue(dest, out var counts0))
                {
                    counts0 = new Dictionary<string, int>(StringComparer.Ordinal);
                    bool any = false;
                    if (File.Exists(dest))
                    {
                        try
                        {
                            foreach (ChatLogEntry e in FirestormLogFormat.Parse(File.ReadLines(dest, Encoding.UTF8)))
                            {
                                any = true;
                                string k = Key(e.Timestamp, e.From, e.Text);
                                counts0[k] = counts0.GetValueOrDefault(k) + 1;
                            }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            plan.Files.Add(new ChatLogImportFile(file, dest, "skipped", 0, 0, "the destination could not be read"));
                            continue;
                        }
                        any |= new FileInfo(dest).Length > 0;
                    }
                    seen[dest] = counts0;
                    hasContent[dest] = any;
                    blocks[dest] = new StringBuilder();
                    counts[dest] = (0, null, null);
                    order.Add(dest);
                }

                int added = 0, present = 0;
                var inFile = new Dictionary<string, int>(StringComparer.Ordinal);
                (int Imported, DateTime? First, DateTime? Last) tally = counts[dest];
                StringBuilder block = blocks[dest];
                foreach (ChatLogEntry e in entries)
                {
                    string stamp = e.Timestamp;
                    DateTime? slt = null;
                    if (e.Time is { } local)
                    {
                        slt = SecondLifeTime.FromZone(local, zone);
                        stamp = slt.Value.ToString(FirestormLogFormat.TimestampFormat, CultureInfo.InvariantCulture);
                    }

                    string from = string.IsNullOrWhiteSpace(e.From) || e.From == "System" ? naming.SystemName : e.From;
                    string key = Key(stamp, from, e.Text);
                    // This is the n-th time this file says this; the destination already holds as many
                    // as it holds. Only the ones beyond that are new, and then it holds them too.
                    int nth = inFile[key] = inFile.GetValueOrDefault(key) + 1;
                    if (nth <= counts0.GetValueOrDefault(key))
                    {
                        present++;
                        continue;
                    }
                    counts0[key] = nth;

                    block.Append(FirestormLogFormat.ToFileText(FirestormLogFormat.FormatRecord(stamp, from, e.Text)));
                    added++;
                    if (slt is { } t)
                    {
                        tally = (tally.Imported, tally.First is null || t < tally.First ? t : tally.First, tally.Last is null || t > tally.Last ? t : tally.Last);
                    }
                }
                counts[dest] = (tally.Imported + added, tally.First, tally.Last);
                plan.Files.Add(new ChatLogImportFile(file, dest, kindName, added, present, null));
            }
        }

        foreach (string dest in order)
        {
            if (blocks[dest].Length == 0) continue;
            var (imported, first, last) = counts[dest];

            var text = new StringBuilder();
            if (hasContent[dest])
            {
                string range = first is { } f && last is { } l
                    ? $" ({f.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture)} to {l.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture)})"
                    : "";
                text.Append(FirestormLogFormat.ToFileText(FirestormLogFormat.FormatRecord(
                    SecondLifeTime.FromUtc(nowUtc), naming.SystemName,
                    $"SLNG: {imported} message(s) imported from an older SLNG log{range}.")));
            }
            text.Append(blocks[dest]);
            plan.Appends.Add(new ChatLogPlannedAppend(dest, text.ToString()));
        }

        return plan;
    }

    private static string Key(string stamp, string from, string text)
        => stamp + "\u0001" + from + "\u0001" + text;

    private static (string Name, ChatLogKind Kind) Classify(string stem, IReadOnlyList<ChatLogEntry> entries)
    {
        if (stem.Equals(FirestormLogLayout.NearbyStem, StringComparison.OrdinalIgnoreCase))
            return ("nearby", ChatLogKind.Local);

        // The old writer made the file name by replacing characters a file name cannot hold with '_',
        // so a sender is compared the same way.
        foreach (ChatLogEntry e in entries)
        {
            if (e.From.Length == 0) continue;
            if (OldFileName(e.From).Equals(stem, StringComparison.OrdinalIgnoreCase))
                return ("IM", ChatLogKind.Im);
        }
        return ("group", ChatLogKind.Group);
    }

    private static string OldFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }
}
