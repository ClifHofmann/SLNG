using SLNG.Core.Services;
using Xunit;

namespace SLNG.Core.Tests.Services;

// BUG-GRID-01. A chat log belongs to one account on one grid. The logger starts with no directory,
// follows the account that logs in, and never writes or reads a log it was not pointed at.
public sealed class ChatLoggerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "slng-chatlog-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private static readonly DateTime When = new(2026, 10, 2, 12, 0, 0);

    [Fact]
    public async Task Before_a_login_nothing_is_written_and_nothing_is_read()
    {
        var log = new ChatLogger();

        await log.AppendAsync(ChatLogKind.Local, "", "Someone", "hello", When);

        Assert.Null(log.RootDirectory);
        Assert.Null(log.FilePathFor(ChatLogKind.Local, ""));
        Assert.Empty(log.GetPage(ChatLogKind.Local, "", 0, 50, out int pages));
        Assert.Equal(1, pages);
    }

    [Fact]
    public async Task The_same_account_name_on_two_grids_keeps_two_separate_chat_logs()
    {
        var paths = new GridDataPaths(_root);
        var log = new ChatLogger();

        log.UseDirectory(paths.AccountDirectory("https://login.agni.lindenlab.com/cgi-bin/login.cgi", "Clifton", "Howlett"));
        await log.AppendAsync(ChatLogKind.Local, "", "Someone", "said on Second Life", When);

        log.UseDirectory(paths.AccountDirectory("http://hg.osgrid.org/", "Clifton", "Howlett"));
        Assert.Empty(log.GetPage(ChatLogKind.Local, "", 0, 50, out _)); // OSGrid does not see SL's chat
        await log.AppendAsync(ChatLogKind.Local, "", "Someone", "said on OSGrid", When);

        Assert.Single(log.GetPage(ChatLogKind.Local, "", 0, 50, out _), l => l.Contains("said on OSGrid"));

        log.UseDirectory(paths.AccountDirectory("https://login.agni.lindenlab.com/cgi-bin/login.cgi", "Clifton", "Howlett"));
        var back = log.GetPage(ChatLogKind.Local, "", 0, 50, out _);
        Assert.Single(back);
        Assert.Contains("said on Second Life", back[0]);
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
    public async Task Each_conversation_is_its_own_file_inside_the_account_directory()
    {
        var log = new ChatLogger(_root);

        await log.AppendAsync(ChatLogKind.Im, "Denise Resident", "Denise", "hi", When);

        Assert.True(File.Exists(Path.Combine(_root, "Denise Resident.txt")));
        Assert.Equal(Path.Combine(_root, "Denise Resident.txt"), log.FilePathFor(ChatLogKind.Im, "Denise Resident"));
    }
}
