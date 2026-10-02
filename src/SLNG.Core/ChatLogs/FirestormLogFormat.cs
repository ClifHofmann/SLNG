using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SLNG.Core.ChatLogs;

/// <summary>
/// FEAT-UI-41: the line format of Firestorm's chat logs -- written the way <c>LLLogChat::saveHistory</c>
/// writes it, read the way <c>LLLogChat::loadChatHistory</c> + <c>LLChatLogParser::parse</c> read it
/// (<c>indra/newview/lllogchat.cpp</c>), plus the one extra shape SLNG's own older builds wrote.
/// ONE parser: the History viewer and the preload of an open conversation both go through
/// <see cref="Parse"/>.
///
/// <para><b>Written line</b> (real example, group chat): <c>[2026/04/11 08:07]  YoSung Resident: text</c>
/// -- the stamp in Second Life time (see <see cref="SecondLifeTime"/>), TWO spaces, the sender with
/// every <c>:</c> written as <c>%3A</c>, <c>: </c>, then the message. A newline in the message is
/// written as a newline plus one space, so every continuation line starts with a space (a blank
/// paragraph line is a single space). The file is UTF-8 without BOM, lines end with the platform's
/// newline (CRLF on Windows).</para>
///
/// <para><b>Read line</b> (any of): optional stamp <c>[yyyy/m/d h:mm]</c>, <c>[yyyy/m/d h:mm:ss]</c>,
/// either with <c> AM</c>/<c> PM</c>, or time only; then <c>name: text</c> where the name is
/// everything before the FIRST colon (so a nameless <c>http://x</c> reads as sender "http", exactly
/// as in the viewer). A line starting with a space continues the previous message. SLNG's older
/// logs wrote <c>[2026/09/12 18:58:51] Name: text</c> and, older still, <c>[2026.07.21 13:19:00]</c>
/// -- both read.</para>
///
/// <para>Differences from the viewer, on purpose: an empty line is skipped (the viewer turns it
/// into an empty message); a continuation line with no message before it is dropped (the viewer
/// does the same).</para>
/// </summary>
public static class FirestormLogFormat
{
    /// <summary>The stamp SLNG writes: minute precision, 24-hour, with the date -- the viewer's
    /// defaults (<c>LogTimestamp</c>, <c>LogTimestampDate</c> on, <c>FSSecondsinChatTimestamps</c>
    /// off, <c>Use24HourClock</c> on) and what every real file on disk uses.</summary>
    public const string TimestampFormat = "yyyy/MM/dd HH:mm";

    private static readonly Regex StampPrefix = new(
        @"^\[(?:(?<y>\d{4})[/.](?<mo>\d{1,2})[/.](?<d>\d{1,2})\s+)?(?<h>\d{1,2}):(?<mi>\d{2})(?::(?<s>\d{2}))?(?:\s(?<ap>[AaPp][Mm]))?\]\s*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>One record as the viewer writes it, without the line ending:
    /// <c>[stamp]  from: text</c>, continuation lines prefixed with one space. An empty
    /// <paramref name="timestamp"/> writes no stamp; an empty <paramref name="from"/> writes no
    /// name (the caller substitutes the system name first).</summary>
    public static string FormatRecord(string timestamp, string from, string text)
    {
        var sb = new StringBuilder();
        if (timestamp.Length > 0) sb.Append('[').Append(timestamp).Append("]  ");

        string name = from.Trim().Replace(":", "%3A", StringComparison.Ordinal);
        if (name.Length > 0) sb.Append(name).Append(": ");

        // The viewer does not normalise line endings; a message from the network uses \n, and a
        // stray \r would split the record, so it is folded here.
        string body = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        sb.Append(body.Replace("\n", "\n ", StringComparison.Ordinal));
        return sb.ToString();
    }

    /// <summary><see cref="FormatRecord(string, string, string)"/> for a Second Life time.</summary>
    public static string FormatRecord(DateTime secondLifeTime, string from, string text)
        => FormatRecord(secondLifeTime.ToString(TimestampFormat, CultureInfo.InvariantCulture), from, text);

    /// <summary>A record as it goes into the file: every newline, the ones inside a multi-line
    /// message and the one that ends the record, is the platform's newline -- the viewer writes through
    /// a text-mode stream, so on Windows the whole file is CRLF (a real file has <c>an.</c> CRLF, a
    /// single space, CRLF, <c>Will you be my friend?</c>).</summary>
    public static string ToFileText(string record)
        => record.Replace("\n", Environment.NewLine, StringComparison.Ordinal) + Environment.NewLine;

    /// <summary>Reads a file's physical lines (line endings already removed, or still present --
    /// a trailing <c>\r</c> is stripped) into messages, in file order.</summary>
    public static IReadOnlyList<ChatLogEntry> Parse(IEnumerable<string> physicalLines)
    {
        var records = new List<(string Stamp, DateTime? Time, string From, StringBuilder Text)>();

        foreach (string raw in physicalLines)
        {
            string line = raw.TrimEnd('\r', '\n');
            if (line.Length > 0 && line[0] == '﻿') line = line[1..]; // the viewer strips a BOM per line
            if (line.Length == 0) continue;

            if (line[0] == ' ')
            {
                // "updated 1.23 plain text log format requires a space added before subsequent lines
                // in a multilined message" (lllogchat.cpp)
                if (records.Count > 0) records[^1].Text.Append('\n').Append(line, 1, line.Length - 1);
                continue;
            }

            string stuff = line;
            string stamp = "";
            DateTime? time = null;
            Match m = StampPrefix.Match(line);
            if (m.Success)
            {
                stamp = line[1..(m.Index + m.Length)].TrimEnd().TrimEnd(']');
                time = ParseTime(m);
                stuff = line[m.Length..];
            }

            string from = "";
            string text = stuff;
            int colon = stuff.IndexOf(':');
            if (colon > 0)
            {
                from = Uri.UnescapeDataString(stuff[..colon]);
                text = stuff[(colon + 1)..].TrimStart(' ', '\t', '\v', '\f');
            }

            records.Add((stamp, time, from, new StringBuilder(text)));
        }

        var result = new List<ChatLogEntry>(records.Count);
        foreach (var r in records) result.Add(new ChatLogEntry(r.Stamp, r.Time, r.From, r.Text.ToString()));
        return result;
    }

    private static DateTime? ParseTime(Match m)
    {
        if (!m.Groups["y"].Success) return null;

        int hour = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
        if (m.Groups["ap"].Success)
        {
            bool pm = char.ToLowerInvariant(m.Groups["ap"].Value[0]) == 'p';
            if (pm && hour < 12) hour += 12;
            else if (!pm && hour == 12) hour = 0;
        }

        try
        {
            return new DateTime(
                int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups["mo"].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture),
                hour,
                int.Parse(m.Groups["mi"].Value, CultureInfo.InvariantCulture),
                m.Groups["s"].Success ? int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture) : 0);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null; // a stamp that is not a date: keep the text, drop the instant
        }
    }
}
