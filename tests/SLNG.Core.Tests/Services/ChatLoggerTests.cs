using System.Text;
using SLNG.Core.ChatLogs;
using SLNG.Core.Services;
using Xunit;

namespace SLNG.Core.Tests.Services;

// BUG-GRID-01: a chat log belongs to one account on one grid; the logger starts with no directory,
// follows the account that logs in, and never writes or reads a log it was not pointed at.
// FEAT-UI-41: and what it writes is Firestorm's file, byte for byte in shape -- the other viewer must
// be able to read it, and SLNG must read what the other viewer wrote.
public sealed class ChatLoggerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "slng-chatlog-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    // 12:00 UTC on a day in PDT: the stamp written is 05:00.
    private static readonly DateTime When = new(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

    private static readonly string Nl = Environment.NewLine;

    // ---- BUG-GRID-01 -----------------------------------------------------------------------

    [Fact]
    public async Task Before_a_login_nothing_is_written_and_nothing_is_read()
    {
        var log = new ChatLogger();

        await log.AppendAsync(ChatLogKind.Local, "", "Someone", "hello", When);

        Assert.Null(log.RootDirectory);
        Assert.Null(log.FilePathFor(ChatLogKind.Local, ""));
        Assert.Empty(log.GetPage(ChatLogKind.Local, "", 0, 50, out int pages));
        Assert.Empty(log.GetTail(ChatLogKind.Local, "", 10));
        Assert.Equal(1, pages);
    }

    [Fact]
    public async Task The_same_account_name_on_two_grids_keeps_two_separate_chat_logs()
    {
        var log = new ChatLogger();
        string sl = Path.Combine(_root, FirestormLogLayout.AccountFolderName("Clifton", "Howlett", "Second Life"));
        string os = Path.Combine(_root, FirestormLogLayout.AccountFolderName("Clifton", "Howlett", "OSGrid"));

        log.UseDirectory(sl);
        await log.AppendAsync(ChatLogKind.Local, "", "Someone", "said on Second Life", When);

        log.UseDirectory(os);
        Assert.Empty(log.GetPage(ChatLogKind.Local, "", 0, 50, out _)); // OSGrid does not see SL's chat
        await log.AppendAsync(ChatLogKind.Local, "", "Someone", "said on OSGrid", When);

        Assert.Single(log.GetPage(ChatLogKind.Local, "", 0, 50, out _), l => l.Contains("said on OSGrid"));

        log.UseDirectory(sl);
        var back = log.GetPage(ChatLogKind.Local, "", 0, 50, out _);
        Assert.Single(back);
        Assert.Contains("said on Second Life", back[0]);
        Assert.EndsWith("clifton_howlett", sl);
        Assert.EndsWith("clifton_howlett.osgrid", os);
    }

    [Fact]
    public async Task Detaching_stops_writing()
    {
        var log = new ChatLogger(_root);
        await log.AppendAsync(ChatLogKind.Local, "", "A", "kept", When);

        log.UseDirectory(null);
        await log.AppendAsync(ChatLogKind.Local, "", "A", "dropped", When);
        log.UseDirectory(_root);

        var lines = log.GetPage(ChatLogKind.Local, "", 0, 50, out _);
        Assert.Single(lines);
        Assert.Contains("kept", lines[0]);
    }

    [Fact]
    public async Task Disabled_logging_writes_nothing_but_still_reads()
    {
        var log = new ChatLogger(_root);
        await log.AppendAsync(ChatLogKind.Local, "", "A", "before", When);

        log.Enabled = false;
        await log.AppendAsync(ChatLogKind.Local, "", "A", "while off", When);

        Assert.Single(log.GetPage(ChatLogKind.Local, "", 0, 50, out _));
    }

    // ---- FEAT-UI-41: file names ------------------------------------------------------------

    [Fact]
    public async Task Each_conversation_is_its_own_file_named_the_way_firestorm_names_it()
    {
        var log = new ChatLogger(_root, new ChatLogNaming(ImLogNameStyle.Account));

        await log.AppendAsync(ChatLogKind.Im, "Pink Ice", "Pink Ice", "hi", When);
        await log.AppendAsync(ChatLogKind.Group, "Firestorm Support Deutsch", "Zeta Resident", "hi", When);
        await log.AppendAsync(ChatLogKind.Local, "", "Someone", "hi", When);

        Assert.True(File.Exists(Path.Combine(_root, "pink_ice.txt")));
        Assert.True(File.Exists(Path.Combine(_root, "Firestorm Support Deutsch (group).txt")));
        Assert.True(File.Exists(Path.Combine(_root, "chat.txt")));
        Assert.Equal(Path.Combine(_root, "pink_ice.txt"), log.FilePathFor(ChatLogKind.Im, "Pink Ice"));
    }

    [Fact]
    public async Task Legacy_im_names_keep_case_and_the_space()
    {
        var log = new ChatLogger(_root, new ChatLogNaming(ImLogNameStyle.Legacy));

        await log.AppendAsync(ChatLogKind.Im, "Denise Resident", "Denise Resident", "hi", When);

        Assert.True(File.Exists(Path.Combine(_root, "Denise.txt")));
    }

    // ---- FEAT-UI-41: what is written -------------------------------------------------------

    [Fact]
    public async Task A_written_line_is_exactly_the_viewers_line_in_second_life_time()
    {
        var log = new ChatLogger(_root);

        await log.AppendAsync(ChatLogKind.Im, "Pink Ice", "Clifton Howlett", "sounds great", When);

        byte[] bytes = File.ReadAllBytes(Path.Combine(_root, "Pink Ice.txt"));
        Assert.Equal("[2026/10/02 05:00]  Clifton Howlett: sounds great" + Nl, Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task The_file_is_utf8_without_a_bom_and_non_ascii_survives()
    {
        var log = new ChatLogger(_root);

        await log.AppendAsync(ChatLogKind.Local, "", "Zoë", "grüße \U0001F389", When);

        byte[] bytes = File.ReadAllBytes(Path.Combine(_root, "chat.txt"));
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Contains("grüße \U0001F389", Encoding.UTF8.GetString(bytes));
        Assert.Contains("ü", Encoding.UTF8.GetString(bytes));
        Assert.True(bytes.AsSpan().IndexOf(new byte[] { 0xC3, 0xBC }) >= 0); // "ü" is c3 bc
    }

    [Fact]
    public async Task A_multi_line_message_continues_on_lines_that_start_with_a_space_with_the_platform_newline()
    {
        var log = new ChatLogger(_root);

        await log.AppendAsync(ChatLogKind.Local, "", "Grid", "one.\n\ntwo", When);

        Assert.Equal("[2026/10/02 05:00]  Grid: one." + Nl + " " + Nl + " two" + Nl,
            Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(_root, "chat.txt"))));
    }

    [Fact]
    public async Task A_system_line_is_sent_by_the_grids_system_name()
    {
        var log = new ChatLogger(_root, new ChatLogNaming(ImLogNameStyle.Account, SystemName: "Grid"));

        await log.AppendAsync(ChatLogKind.Im, "Pink Ice", "", "Pink Ice is offline.", When);

        Assert.Contains("  Grid: Pink Ice is offline.", File.ReadAllText(Path.Combine(_root, "pink_ice.txt")));
    }

    [Fact]
    public async Task Appending_keeps_everything_already_in_the_file_byte_for_byte()
    {
        string path = Path.Combine(_root, "chat.txt");
        Directory.CreateDirectory(_root);
        // What the other viewer wrote, with its own oddities: CRLF, a non-ASCII line, a multi-line message.
        byte[] theirs = Encoding.UTF8.GetBytes(
            "[2026/04/11 08:07]  Zeta Resident: (W+ 7.2.3 f) so ein mist :(  ok danke..\r\n" +
            "[2026/07/21 23:40]  Grid: Freundschaft an.\r\n \r\n Will you be my friend?\r\n" +
            "[2026/06/22 06:55]  Second Life: grüße\r\n");
        File.WriteAllBytes(path, theirs);

        var log = new ChatLogger(_root);
        await log.AppendAsync(ChatLogKind.Local, "", "A", "one", When);
        await log.AppendAsync(ChatLogKind.Local, "", "A", "two", When);

        byte[] now = File.ReadAllBytes(path);
        Assert.Equal(theirs, now.Take(theirs.Length).ToArray());
        Assert.Equal(2, Encoding.UTF8.GetString(now, theirs.Length, now.Length - theirs.Length).Split(Nl, StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public async Task A_file_cut_off_without_a_final_newline_does_not_get_two_records_on_one_line()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "chat.txt"), "[2026/04/11 08:07]  A: cut off", new UTF8Encoding(false));

        var log = new ChatLogger(_root);
        await log.AppendAsync(ChatLogKind.Local, "", "B", "next", When);

        var lines = log.GetPage(ChatLogKind.Local, "", 0, 50, out _);
        Assert.Equal(2, lines.Count);
        Assert.Equal("[2026/04/11 08:07] A: cut off", lines[0]);
    }

    [Fact]
    public async Task A_burst_of_appends_from_many_tasks_never_interleaves_lines()
    {
        var log = new ChatLogger(_root);

        await Task.WhenAll(Enumerable.Range(0, 60).Select(i => log.AppendAsync(ChatLogKind.Local, "", "A", $"message {i}", When)));

        var entries = log.GetPage(ChatLogKind.Local, "", 0, 1000, out _);
        Assert.Equal(60, entries.Count);
        Assert.Equal(60, entries.Distinct().Count());
    }

    // ---- FEAT-UI-41: what is read ----------------------------------------------------------

    [Fact]
    public void A_file_firestorm_wrote_is_shown_with_its_multi_line_message_in_one_piece()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, "pink_ice.txt"), Encoding.UTF8.GetBytes(
            "[2026/06/29 05:22]  Pink: I can send some complete avis and landmarks if you want\r\n" +
            "[2026/06/29 05:24]  Clifton Howlett: sounds great\r\n" +
            "[2026/06/29 05:24]  Grid: hop://login.example.org/x about Landmark übergeben:\r\n http://example.org/y\r\n"));
        var log = new ChatLogger(_root, new ChatLogNaming(ImLogNameStyle.Account));

        var lines = log.GetPage(ChatLogKind.Im, "Pink Ice", 0, 50, out int pages);

        Assert.Equal(1, pages);
        Assert.Equal(3, lines.Count);
        Assert.Equal("[2026/06/29 05:22] Pink: I can send some complete avis and landmarks if you want", lines[0]);
        Assert.Equal("[2026/06/29 05:24] Grid: hop://login.example.org/x about Landmark übergeben:\nhttp://example.org/y", lines[2]);
    }

    [Fact]
    public void A_file_an_older_slng_wrote_is_shown_too()
    {
        Directory.CreateDirectory(_root);
        // Mixed line endings, seconds, one space: all as found in the old chat folder.
        File.WriteAllBytes(Path.Combine(_root, "chat.txt"), Encoding.UTF8.GetBytes(
            "[2026.07.21 13:19:00] ZHAO Rock AO (Male): 1576% memory free\n" +
            "[2026/07/21 15:12:06] Clifton Howlett: huhu\r\n"));
        var log = new ChatLogger(_root);

        var lines = log.GetPage(ChatLogKind.Local, "", 0, 50, out _);

        Assert.Equal(new[]
        {
            "[2026.07.21 13:19:00] ZHAO Rock AO (Male): 1576% memory free",
            "[2026/07/21 15:12:06] Clifton Howlett: huhu",
        }, lines);
    }

    [Fact]
    public async Task What_the_writer_produces_the_reader_parses()
    {
        var log = new ChatLogger(_root);
        await log.AppendAsync(ChatLogKind.Group, "Howletts", "Reamon Bullmer", "Holla", When);
        await log.AppendAsync(ChatLogKind.Group, "Howletts", "Clifton Howlett", "multi\nline: with a colon", When.AddMinutes(1));
        await log.AppendAsync(ChatLogKind.Group, "Howletts", "Visitor @grid.example:8002", "hi", When.AddMinutes(2));

        var lines = log.GetPage(ChatLogKind.Group, "Howletts", 0, 50, out _);

        Assert.Equal(new[]
        {
            "[2026/10/02 05:00] Reamon Bullmer: Holla",
            "[2026/10/02 05:01] Clifton Howlett: multi\nline: with a colon",
            "[2026/10/02 05:02] Visitor @grid.example:8002: hi",
        }, lines);
    }

    [Fact]
    public async Task Pages_are_counted_in_messages_not_lines()
    {
        var log = new ChatLogger(_root);
        for (int i = 0; i < 25; i++)
            await log.AppendAsync(ChatLogKind.Local, "", "A", $"m{i}\nsecond line", When);

        var last = log.GetPage(ChatLogKind.Local, "", int.MaxValue, 10, out int pages);

        Assert.Equal(3, pages);
        Assert.Equal(5, last.Count);
        Assert.EndsWith("m24\nsecond line", last[^1]);
    }

    [Fact]
    public async Task The_tail_is_the_last_messages_and_reads_only_the_end_of_a_big_file()
    {
        // A real log is tens of megabytes; opening a conversation must not read all of it.
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "chat.txt");
        using (var w = new StreamWriter(path, false, new UTF8Encoding(false)) { NewLine = "\r\n" })
        {
            for (int i = 0; i < 60_000; i++) w.WriteLine($"[2026/04/11 08:07]  Zeta Resident: line number {i} of a long conversation with some padding text");
        }
        var log = new ChatLogger(_root);
        await log.AppendAsync(ChatLogKind.Local, "", "A", "the very last", When);

        var tail = log.GetTail(ChatLogKind.Local, "", 5);

        Assert.Equal(5, tail.Count);
        Assert.Equal("[2026/10/02 05:00] A: the very last", tail[^1]);
        Assert.Contains("line number 59999 ", tail[^2]);
        Assert.All(tail, l => Assert.StartsWith("[2026/", l)); // never a half line from the cut
    }

    [Fact]
    public void The_tail_of_a_small_file_is_all_of_it()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "chat.txt"), "[2026/04/11 08:07]  A: one\r\n[2026/04/11 08:08]  A: two\r\n");

        Assert.Equal(2, new ChatLogger(_root).GetTail(ChatLogKind.Local, "", 50).Count);
        Assert.Empty(new ChatLogger(_root).GetTail(ChatLogKind.Local, "", 0));
    }

    [Fact]
    public async Task With_the_date_suffix_on_the_newest_dated_file_is_read_when_today_has_none()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "chat-2024-09-28.txt"), "[2024/09/28 11:44]  A: old\r\n");
        var log = new ChatLogger(_root, new ChatLogNaming(DateSuffix: true));

        Assert.Equal("[2024/09/28 11:44] A: old", Assert.Single(log.GetTail(ChatLogKind.Local, "", 5)));

        await log.AppendAsync(ChatLogKind.Local, "", "A", "new", DateTime.UtcNow);
        Assert.Matches(@"^chat-\d{4}-\d{2}-\d{2}\.txt$", Path.GetFileName(Directory.GetFiles(_root, "chat-*").OrderBy(f => f).Last()));
    }
}
