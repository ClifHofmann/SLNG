using SLNG.Core.ChatLogs;
using SLNG.Core.Services;
using Xunit;

namespace SLNG.Core.Tests.ChatLogs;

// FEAT-UI-41. Every expectation below is a name that exists on the maintainer's disk (anonymised where
// it is a person) and is what lldir.cpp / lllogchat.cpp / llimview.cpp produce.
public sealed class FirestormLogLayoutTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "slng-layout-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private static readonly DateTime LocalNow = new(2026, 10, 2, 9, 35, 0, DateTimeKind.Local);

    // ---- the account folder ----------------------------------------------------------------

    [Theory]
    [InlineData("Clifton", "Howlett", "Second Life", "clifton_howlett")]            // real: clifton_howlett
    [InlineData("Clifton", "Howlett", "OSGrid", "clifton_howlett.osgrid")]          // real: clifton_howlett.osgrid
    [InlineData("Cilian", "Dupont", "Alife Virtual", "cilian_dupont.alife_virtual")] // real: label, not the nick "AV"
    [InlineData("Test", "User", "localhost", "test_user.localhost")]                // real: test_user.localhost
    [InlineData("Denise1976", "Resident", "Second Life", "denise1976_resident")]    // real: denise1976_resident
    [InlineData("Denise1976", "", "Second Life", "denise1976_resident")]            // a single-name account
    [InlineData("Pat", "Example", "Second Life Beta", "pat_example.second_life_beta")]
    [InlineData("Pat", "Example", "SECOND LIFE", "pat_example")]                    // compared lower-cased, spaces as _
    [InlineData("Pat", "Example", "", "pat_example")]
    [InlineData("Pat", "Example", null, "pat_example")]
    public void The_account_folder_is_first_last_lower_case_plus_the_grid_label_unless_it_is_second_life(
        string first, string last, string? label, string expected)
    {
        Assert.Equal(expected, FirestormLogLayout.AccountFolderName(first, last, label));
    }

    [Fact]
    public void A_label_that_fell_back_to_host_and_port_is_made_a_legal_folder_name()
    {
        // The viewer would fail to create "pat_example.someplace.example:9000" on Windows.
        Assert.Equal("pat_example.someplace.example_9000", FirestormLogLayout.AccountFolderName("Pat", "Example", "someplace.example:9000"));
    }

    // ---- file names ------------------------------------------------------------------------

    [Fact]
    public void The_dot_is_one_of_the_cleaned_characters_so_pink_dot_ice_is_pink_underscore_ice()
    {
        Assert.Equal("pink_ice", FirestormLogLayout.CleanFileName("pink.ice")); // real: pink_ice.txt
    }

    [Fact]
    public void Every_character_the_viewer_cleans_becomes_an_underscore()
    {
        foreach (char c in "\"'\\/?*:.<>|[]{}~")
            Assert.Equal("a_b", FirestormLogLayout.CleanFileName($"a{c}b"));
    }

    [Fact]
    public void Parentheses_and_spaces_survive_so_group_files_keep_their_names()
    {
        Assert.Equal("Firestorm Support Deutsch (group)", FirestormLogLayout.CleanFileName("Firestorm Support Deutsch (group)"));
    }

    [Theory]
    [InlineData("con", "_con")]
    [InlineData("NUL", "_NUL")]
    [InlineData("trailing ", "trailing_")]
    public void Names_windows_would_refuse_are_made_legal_where_the_viewer_would_just_fail(string name, string expected)
    {
        Assert.Equal(expected, FirestormLogLayout.CleanFileName(name));
    }

    [Theory]
    [InlineData("Pink Ice", ImLogNameStyle.Account, "pink_ice.txt")]              // real: pink_ice.txt
    [InlineData("Torsten Flatley", ImLogNameStyle.Account, "torsten_flatley.txt")] // real: torsten_flatley.txt
    [InlineData("Neelep Resident", ImLogNameStyle.Account, "neelep.txt")]         // real: neelep.txt (Resident dropped)
    [InlineData("Neelep", ImLogNameStyle.Account, "neelep.txt")]
    [InlineData("Pink Ice", ImLogNameStyle.Legacy, "Pink Ice.txt")]               // legacy: case kept, a space
    [InlineData("Neelep Resident", ImLogNameStyle.Legacy, "Neelep.txt")]          // legacy: " Resident" cut off
    [InlineData("Clifton Howlett", ImLogNameStyle.Legacy, "Clifton Howlett.txt")]
    public void An_im_log_is_named_after_the_other_persons_legacy_name(string name, ImLogNameStyle style, string expected)
    {
        var naming = new ChatLogNaming(style);

        Assert.Equal(expected, FirestormLogLayout.FileName(ChatLogKind.Im, name, naming, LocalNow));
    }

    [Fact]
    public void A_grid_visitor_name_goes_through_the_same_rule()
    {
        // Not seen on disk; derived from buildUsername + cleanFileName: first = up to the first space.
        Assert.Equal("clifton_howlett_@login_example_org_8002.txt",
            FirestormLogLayout.FileName(ChatLogKind.Im, "Clifton.Howlett @login.example.org:8002", new ChatLogNaming(ImLogNameStyle.Account), LocalNow));
    }

    [Fact]
    public void A_group_log_is_the_group_name_plus_group_in_parentheses()
    {
        Assert.Equal("Firestorm Support Deutsch (group).txt",   // real file name
            FirestormLogLayout.FileName(ChatLogKind.Group, "Firestorm Support Deutsch", ChatLogNaming.Default, LocalNow));
        Assert.Equal("Mr_ Foo_s group (group).txt",
            FirestormLogLayout.FileName(ChatLogKind.Group, "Mr. Foo's group", ChatLogNaming.Default, LocalNow));
    }

    [Fact]
    public void Nearby_chat_is_chat_dot_txt_whatever_the_name()
    {
        Assert.Equal("chat.txt", FirestormLogLayout.FileName(ChatLogKind.Local, "", ChatLogNaming.Default, LocalNow));
        Assert.Equal("chat.txt", FirestormLogLayout.FileName(ChatLogKind.Local, "Main", ChatLogNaming.Default, LocalNow));
    }

    [Fact]
    public void An_empty_conversation_name_makes_no_file_name_as_in_the_viewer()
    {
        Assert.Null(FirestormLogLayout.FileName(ChatLogKind.Im, "  ", ChatLogNaming.Default, LocalNow));
        Assert.Null(FirestormLogLayout.FileName(ChatLogKind.Group, "", ChatLogNaming.Default, LocalNow));
    }

    [Fact]
    public void The_date_suffix_is_per_month_for_conversations_and_per_day_for_nearby_chat()
    {
        var dated = new ChatLogNaming(ImLogNameStyle.Account, DateSuffix: true);

        Assert.Equal("chat-2026-10-02.txt", FirestormLogLayout.FileName(ChatLogKind.Local, "", dated, LocalNow));
        Assert.Equal("pink_ice-2026-10.txt", FirestormLogLayout.FileName(ChatLogKind.Im, "Pink Ice", dated, LocalNow));
        Assert.Equal("Howletts (group)-2026-10.txt", FirestormLogLayout.FileName(ChatLogKind.Group, "Howletts", dated, LocalNow));
    }

    // ---- which existing file is the history ------------------------------------------------

    [Fact]
    public void The_exact_file_wins_then_the_newest_dated_one_then_the_plain_one_as_in_the_viewer()
    {
        Directory.CreateDirectory(_dir);
        var dated = new ChatLogNaming(ImLogNameStyle.Account, DateSuffix: true);

        // Nothing there.
        Assert.Null(FirestormLogLayout.ResolveExisting(_dir, ChatLogKind.Im, "Pink Ice", dated, LocalNow));

        File.WriteAllText(Path.Combine(_dir, "pink_ice.txt"), "x");
        Assert.Equal(Path.Combine(_dir, "pink_ice.txt"), FirestormLogLayout.ResolveExisting(_dir, ChatLogKind.Im, "Pink Ice", dated, LocalNow));

        File.WriteAllText(Path.Combine(_dir, "pink_ice-2018-01.txt"), "x");
        File.WriteAllText(Path.Combine(_dir, "pink_ice-2018-08.txt"), "x");
        Assert.Equal(Path.Combine(_dir, "pink_ice-2018-08.txt"), FirestormLogLayout.ResolveExisting(_dir, ChatLogKind.Im, "Pink Ice", dated, LocalNow));

        File.WriteAllText(Path.Combine(_dir, "pink_ice-2026-10.txt"), "x");
        Assert.Equal(Path.Combine(_dir, "pink_ice-2026-10.txt"), FirestormLogLayout.ResolveExisting(_dir, ChatLogKind.Im, "Pink Ice", dated, LocalNow));
    }

    [Fact]
    public void With_the_date_suffix_off_a_dated_file_is_still_found_when_the_plain_one_is_missing()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "chat-2024-09-28.txt"), "x");

        Assert.Equal(Path.Combine(_dir, "chat-2024-09-28.txt"),
            FirestormLogLayout.ResolveExisting(_dir, ChatLogKind.Local, "", ChatLogNaming.Default, LocalNow));
    }
}
