using SLNG.Core.ChatLogs;
using Xunit;

namespace SLNG.Core.Tests.ChatLogs;

// FEAT-UI-41. The sample lines are real lines from the maintainer's Firestorm logs with the people
// and ids replaced by made-up ones of the same shape; everything else (spacing, punctuation, the
// continuation-line prefix, the non-ASCII text) is as found on disk.
public sealed class FirestormLogFormatTests
{
    // ---- reading what Firestorm wrote ------------------------------------------------------

    [Fact]
    public void A_group_line_has_two_spaces_after_the_stamp_and_keeps_the_text_after_the_first_colon()
    {
        var entries = FirestormLogFormat.Parse(new[]
        {
            "[2026/04/11 08:07]  Zeta Resident: (W+ 7.2.3 f) so ein mist :(  ok danke..",
        });

        var e = Assert.Single(entries);
        Assert.Equal("2026/04/11 08:07", e.Timestamp);
        Assert.Equal(new DateTime(2026, 4, 11, 8, 7, 0), e.Time);
        Assert.Equal("Zeta Resident", e.From);
        Assert.Equal("(W+ 7.2.3 f) so ein mist :(  ok danke..", e.Text);
    }

    [Fact]
    public void A_multi_line_message_is_stored_with_a_space_in_front_of_every_continuation_line()
    {
        // A friendship offer on OSGrid: the real file has " " for the blank paragraph lines.
        var entries = FirestormLogFormat.Parse(new[]
        {
            "[2026/07/21 23:40]  Grid: hop://login.example.org/app/agent/00000000-0000-4000-8000-000000000001/about bietet Ihnen die Freundschaft an.",
            " ",
            " Will you be my friend?",
            " ",
            " (Standardmäßig können Sie gegenseitig ihren Online-Status sehen.)",
            "[2026/07/21 23:44]  Grid: Ihr Freundschaftsangebot wurde angenommen.",
        });

        Assert.Equal(2, entries.Count);
        Assert.Equal("Grid", entries[0].From);
        Assert.Equal(
            "hop://login.example.org/app/agent/00000000-0000-4000-8000-000000000001/about bietet Ihnen die Freundschaft an.\n\n" +
            "Will you be my friend?\n\n(Standardmäßig können Sie gegenseitig ihren Online-Status sehen.)",
            entries[0].Text);
        Assert.Equal("Ihr Freundschaftsangebot wurde angenommen.", entries[1].Text);
    }

    [Fact]
    public void System_lines_are_sent_by_second_life_or_grid()
    {
        var entries = FirestormLogFormat.Parse(new[]
        {
            "[2026/06/22 06:55]  Clifton Howlett: (Gespeichert am Mo Jun 22 15:25:21 2026)Hallo",
            "[2026/06/22 06:55]  Second Life: Clifton Howlett ist online.",
        });

        Assert.Equal("(Gespeichert am Mo Jun 22 15:25:21 2026)Hallo", entries[0].Text);
        Assert.Equal("Second Life", entries[1].From);
        Assert.Equal("Clifton Howlett ist online.", entries[1].Text);
    }

    [Fact]
    public void The_viewers_own_comment_examples_all_parse()
    {
        // lllogchat.cpp, "Typical plain text chat log lines".
        var entries = FirestormLogFormat.Parse(new[]
        {
            "SuperCar: You aren't the owner",
            "[2:59]  SuperCar: You aren't the owner",
            "[2009/11/20 3:00]  SuperCar: You aren't the owner",
            "Katar Ivercourt is Offline",
            "[3:00]  Katar Ivercourt is Offline",
            "[2009/11/20 3:01 PM]  Corba ProductEngine is Offline",
        });

        Assert.Equal("", entries[0].Timestamp);
        Assert.Equal("SuperCar", entries[0].From);
        Assert.Equal("2:59", entries[1].Timestamp);
        Assert.Null(entries[1].Time);                         // a time without a date has no instant
        Assert.Equal(new DateTime(2009, 11, 20, 3, 0, 0), entries[2].Time);
        Assert.Equal("", entries[3].From);                    // no colon, no name
        Assert.Equal("Katar Ivercourt is Offline", entries[3].Text);
        Assert.Equal(new DateTime(2009, 11, 20, 15, 1, 0), entries[5].Time); // 12-hour clock
    }

    [Fact]
    public void A_name_is_everything_before_the_first_colon_exactly_as_the_viewer_reads_it()
    {
        // A nameless line that contains a colon reads as a sender in the viewer too.
        var e = Assert.Single(FirestormLogFormat.Parse(new[] { "http://example.org/x" }));

        Assert.Equal("http", e.From);
        Assert.Equal("//example.org/x", e.Text);
    }

    [Fact]
    public void A_colon_in_a_sender_is_written_as_percent_3A_and_read_back()
    {
        string line = FirestormLogFormat.FormatRecord("2026/10/02 05:00", "Visitor @grid.example:8002", "hi: there");

        Assert.Equal("[2026/10/02 05:00]  Visitor @grid.example%3A8002: hi: there", line);
        var e = Assert.Single(FirestormLogFormat.Parse(new[] { line }));
        Assert.Equal("Visitor @grid.example:8002", e.From);
        Assert.Equal("hi: there", e.Text);
    }

    [Fact]
    public void Line_endings_a_bom_and_blank_lines_do_not_matter_on_read()
    {
        var entries = FirestormLogFormat.Parse(new[]
        {
            "﻿[2026/10/02 05:00]  A: one\r",
            "",
            "[2026/10/02 05:01]  A: two\r\n",
        });

        Assert.Equal(new[] { "one", "two" }, entries.Select(e => e.Text));
    }

    [Fact]
    public void A_continuation_with_nothing_before_it_is_dropped_as_in_the_viewer()
    {
        var entries = FirestormLogFormat.Parse(new[] { " orphan", "[2026/10/02 05:00]  A: kept" });

        Assert.Equal("kept", Assert.Single(entries).Text);
    }

    // ---- reading what SLNG's older builds wrote --------------------------------------------

    [Fact]
    public void An_older_slng_line_with_seconds_and_one_space_reads_like_any_other()
    {
        var e = Assert.Single(FirestormLogFormat.Parse(new[]
        {
            "[2026/09/12 18:58:51] Celeste Example: Happy Weekend sales - Sept 19-20 is on!|HW Sale is on NOW!",
        }));

        Assert.Equal("2026/09/12 18:58:51", e.Timestamp);
        Assert.Equal(new DateTime(2026, 9, 12, 18, 58, 51), e.Time);
        Assert.Equal("Celeste Example", e.From);
        Assert.Equal("Happy Weekend sales - Sept 19-20 is on!|HW Sale is on NOW!", e.Text);
    }

    [Fact]
    public void The_oldest_slng_stamp_with_dots_reads_too()
    {
        var e = Assert.Single(FirestormLogFormat.Parse(new[] { "[2026.07.21 13:19:00] ZHAO Rock AO (Male): 1576% memory free" }));

        Assert.Equal(new DateTime(2026, 7, 21, 13, 19, 0), e.Time);
        Assert.Equal("ZHAO Rock AO (Male)", e.From);
        Assert.Equal("1576% memory free", e.Text);
    }

    // ---- writing ---------------------------------------------------------------------------

    [Fact]
    public void A_written_record_is_stamp_two_spaces_sender_colon_space_text()
    {
        Assert.Equal("[2026/04/11 08:07]  Zeta Resident: so ein mist",
            FirestormLogFormat.FormatRecord(new DateTime(2026, 4, 11, 8, 7, 0), "Zeta Resident", "so ein mist"));
    }

    [Fact]
    public void A_written_multi_line_message_puts_a_space_in_front_of_every_continuation_line()
    {
        string record = FirestormLogFormat.FormatRecord(new DateTime(2026, 7, 21, 23, 40, 0), "Grid", "one.\n\ntwo");

        Assert.Equal("[2026/07/21 23:40]  Grid: one.\n \n two", record);
    }

    [Fact]
    public void Carriage_returns_in_a_message_cannot_split_a_record()
    {
        Assert.Equal("[2026/07/21 23:40]  A: x\n y\n z", FirestormLogFormat.FormatRecord("2026/07/21 23:40", "A", "x\r\ny\rz"));
    }

    [Fact]
    public void A_written_record_reads_back_as_what_was_written()
    {
        string text = "first\n\n  indented second\nthird: with a colon\n";
        string record = FirestormLogFormat.FormatRecord(new DateTime(2026, 10, 2, 5, 0, 0), "Clifton Howlett", text);

        var e = Assert.Single(FirestormLogFormat.Parse(record.Split('\n')));

        Assert.Equal("Clifton Howlett", e.From);
        Assert.Equal(new DateTime(2026, 10, 2, 5, 0, 0), e.Time);
        Assert.Equal(text.TrimEnd('\n') + "\n", e.Text);
    }

    [Fact]
    public void A_nameless_record_writes_no_colon()
    {
        Assert.Equal("[2026/10/02 05:00]  just text", FirestormLogFormat.FormatRecord("2026/10/02 05:00", "  ", "just text"));
    }

    [Fact]
    public void Display_leaves_out_what_the_line_did_not_have()
    {
        Assert.Equal("[2026/10/02 05:00] A: hi", new ChatLogEntry("2026/10/02 05:00", null, "A", "hi").Display());
        Assert.Equal("A: hi", new ChatLogEntry("", null, "A", "hi").Display());
        Assert.Equal("hi", new ChatLogEntry("", null, "", "hi").Display());
    }
}
