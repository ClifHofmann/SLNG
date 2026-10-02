using System.Text;
using SLNG.Core.ChatLogs;
using SLNG.Core.Services;
using Xunit;

namespace SLNG.Core.Tests.ChatLogs;

// FEAT-UI-41. The "old" files are shaped like the real ones in %APPDATA%\SLNG\logs\chat (local time,
// seconds, one space after the stamp, mixed line endings, group files without "(group)", a file named
// by a bare UUID); people and ids are made up.
public sealed class ChatLogImporterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "slng-import-" + Guid.NewGuid().ToString("N"));
    private string Old => Path.Combine(_root, "old");
    private string OldPerGrid => Path.Combine(_root, "oldgrid");
    private string Dest => Path.Combine(_root, "dest");

    // The old stamps were local time; for the test, a fixed zone: UTC+2.
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("t+2", TimeSpan.FromHours(2), "t+2", "t+2");
    private static readonly DateTime Now = new(2026, 10, 2, 17, 0, 0, DateTimeKind.Utc);
    private static readonly ChatLogNaming Naming = new(ImLogNameStyle.Account, SystemName: "Second Life");
    private static readonly string Nl = Environment.NewLine;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private void Write(string dir, string name, string content)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name), Encoding.UTF8.GetBytes(content));
    }

    private static async Task Apply(ChatLogImportPlan plan, string dest)
    {
        var log = new ChatLogger(dest);
        foreach (var a in plan.Appends) await log.AppendBlockAsync(dest, a.DestinationPath, a.Block);
    }

    private ChatLogImportPlan Plan(params string[] sources) => ChatLogImporter.Plan(sources, Dest, Naming, Now, Zone);

    [Fact]
    public void An_old_im_file_goes_to_the_partners_firestorm_file_with_the_time_converted_to_second_life_time()
    {
        Write(Old, "Pink Ice.txt",
            "[2026/08/27 20:30:04] Pink Ice: Holla\r\n[2026/08/27 20:30:11] Clifton Howlett: hello\r\n");

        var plan = Plan(Old);

        var append = Assert.Single(plan.Appends);
        Assert.Equal(Path.Combine(Dest, "pink_ice.txt"), append.DestinationPath);
        // 20:30 at UTC+2 is 18:30 UTC, which is 11:30 PDT.
        Assert.Equal(
            "[2026/08/27 11:30]  Pink Ice: Holla" + Nl + "[2026/08/27 11:30]  Clifton Howlett: hello" + Nl,
            append.Block);
        Assert.Equal("IM", Assert.Single(plan.Files).Kind);
    }

    [Fact]
    public void A_file_whose_name_is_not_one_of_its_senders_is_a_group_and_gets_the_group_suffix()
    {
        Write(Old, "Howletts.txt",
            "[2026/08/27 20:30:04] Reamon Bullmer: Holla\r\n[2026/08/27 20:30:11] Clifton Howlett: hello\r\n");

        var plan = Plan(Old);

        Assert.Equal(Path.Combine(Dest, "Howletts (group).txt"), Assert.Single(plan.Appends).DestinationPath);
        Assert.Equal("group", plan.Files[0].Kind);
    }

    [Fact]
    public void Nearby_chat_goes_to_chat_dot_txt_and_the_old_system_sender_becomes_the_grids()
    {
        Write(Old, "chat.txt",
            "[2026.07.21 13:19:00] ZHAO Rock AO (Male): 1576% memory free\n" +
            "[2026/07/21 15:28:57] System: Pink is offline.\r\n");

        var plan = Plan(Old);

        var append = Assert.Single(plan.Appends);
        Assert.Equal(Path.Combine(Dest, "chat.txt"), append.DestinationPath);
        Assert.Equal(
            "[2026/07/21 04:19]  ZHAO Rock AO (Male): 1576% memory free" + Nl +
            "[2026/07/21 06:28]  Second Life: Pink is offline." + Nl,
            append.Block);
    }

    [Fact]
    public void A_file_named_by_a_bare_id_is_skipped_and_reported_and_nothing_else_is_lost()
    {
        Write(Old, "2299a494-37aa-bc9d-4c90-e21594200d59.txt", "[2026/09/19 19:00:32] Celeste Example: sale\r\n");
        Write(Old, "Howletts.txt", "[2026/08/27 20:30:04] A: x\r\n");

        var plan = Plan(Old);

        Assert.Equal(1, plan.FilesSkipped);
        Assert.Single(plan.Appends);
        Assert.Contains(plan.Files, f => f.Kind == "skipped" && f.SkipReason!.Contains("bare id"));
    }

    [Fact]
    public void Planning_reads_and_writes_nothing()
    {
        Write(Old, "Howletts.txt", "[2026/08/27 20:30:04] A: x\r\n");
        byte[] before = File.ReadAllBytes(Path.Combine(Old, "Howletts.txt"));

        Plan(Old);

        Assert.False(Directory.Exists(Dest));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(Old, "Howletts.txt")));
    }

    [Fact]
    public async Task Importing_twice_appends_nothing_the_second_time()
    {
        Write(Old, "Howletts.txt",
            "[2026/08/27 20:30:04] A: hi\r\n[2026/08/27 20:30:09] A: hi\r\n[2026/08/27 20:31:00] B: bye\r\n");

        var first = Plan(Old);
        Assert.Equal(3, first.MessagesToImport);
        await Apply(first, Dest);
        string afterFirst = File.ReadAllText(Path.Combine(Dest, "Howletts (group).txt"));

        var second = Plan(Old);

        Assert.Empty(second.Appends);
        Assert.Equal(0, second.MessagesToImport);
        Assert.Equal(3, second.MessagesAlreadyThere);
        Assert.Equal(afterFirst, File.ReadAllText(Path.Combine(Dest, "Howletts (group).txt")));
    }

    [Fact]
    public async Task Two_identical_messages_in_one_minute_are_both_kept()
    {
        // "hi" twice within a minute is two messages, not one repeated line.
        Write(Old, "Howletts.txt", "[2026/08/27 20:30:04] A: hi\r\n[2026/08/27 20:30:09] A: hi\r\n");

        await Apply(Plan(Old), Dest);

        Assert.Equal(2, File.ReadAllLines(Path.Combine(Dest, "Howletts (group).txt")).Length);
    }

    [Fact]
    public async Task Appending_to_an_existing_file_leaves_it_untouched_and_adds_a_marker_then_the_messages()
    {
        Directory.CreateDirectory(Dest);
        byte[] theirs = Encoding.UTF8.GetBytes("[2026/09/01 08:00]  Zeta Resident: from firestorm" + Nl);
        File.WriteAllBytes(Path.Combine(Dest, "Howletts (group).txt"), theirs);
        Write(Old, "Howletts.txt", "[2026/08/27 20:30:04] A: from slng\r\n");

        var plan = Plan(Old);
        await Apply(plan, Dest);

        byte[] now = File.ReadAllBytes(Path.Combine(Dest, "Howletts (group).txt"));
        Assert.Equal(theirs, now.Take(theirs.Length).ToArray());
        string added = Encoding.UTF8.GetString(now, theirs.Length, now.Length - theirs.Length);
        // 17:00 UTC on 2 October is 10:00 PDT.
        Assert.Equal(
            "[2026/10/02 10:00]  Second Life: SLNG: 1 message(s) imported from an older SLNG log (2026/08/27 to 2026/08/27)." + Nl +
            "[2026/08/27 11:30]  A: from slng" + Nl,
            added);
    }

    [Fact]
    public void A_message_firestorm_already_logged_is_not_added_again()
    {
        Directory.CreateDirectory(Dest);
        File.WriteAllText(Path.Combine(Dest, "Howletts (group).txt"), "[2026/08/27 11:30]  A: from both" + Nl);
        Write(Old, "Howletts.txt", "[2026/08/27 20:30:04] A: from both\r\n[2026/08/27 20:31:00] A: only slng\r\n");

        var plan = Plan(Old);

        Assert.Equal(1, plan.MessagesToImport);
        Assert.Equal(1, plan.MessagesAlreadyThere);
        Assert.DoesNotContain("from both", Assert.Single(plan.Appends).Block);
    }

    [Fact]
    public void The_same_conversation_in_two_old_folders_is_imported_once()
    {
        Write(Old, "Howletts.txt", "[2026/08/27 20:30:04] A: hi\r\n");
        Write(OldPerGrid, "Howletts.txt", "[2026/08/27 20:30:04] A: hi\r\n[2026/08/27 20:40:00] A: more\r\n");

        var plan = Plan(Old, OldPerGrid);

        var append = Assert.Single(plan.Appends);
        Assert.Equal(2, append.Block.Split(Nl, StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void A_continuation_line_with_its_leading_space_stays_attached_to_its_message()
    {
        Write(Old, "Howletts.txt", "[2026/08/27 20:30:04] A: one\r\n two\r\n");

        var plan = Plan(Old);

        Assert.Equal("[2026/08/27 11:30]  A: one" + Nl + " two" + Nl, Assert.Single(plan.Appends).Block);
    }

    [Fact]
    public void A_missing_source_folder_is_not_an_error()
    {
        var plan = Plan(Path.Combine(_root, "nowhere"));

        Assert.Empty(plan.Appends);
        Assert.Empty(plan.Files);
    }

    [Fact]
    public async Task An_imported_file_is_read_back_by_the_reader_as_what_it_was()
    {
        Write(Old, "Pink Ice.txt",
            "[2026/08/27 20:30:04] Pink Ice: Holla\r\n[2026/08/27 20:30:11] Clifton Howlett: hello\r\n");
        await Apply(Plan(Old), Dest);

        var lines = new ChatLogger(Dest, Naming).GetPage(ChatLogKind.Im, "Pink Ice", 0, 50, out _);

        Assert.Equal(new[] { "[2026/08/27 11:30] Pink Ice: Holla", "[2026/08/27 11:30] Clifton Howlett: hello" }, lines);
    }
}
