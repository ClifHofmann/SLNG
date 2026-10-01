using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

public class RadarTableTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private static Guid Id(int n) => new(n, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static RadarRow Row(
        string name,
        int id = 0,
        float distance = 10f,
        bool heightKnown = true,
        bool inRegion = true,
        bool sitting = false,
        bool typing = false,
        float? voice = null,
        TimeSpan? seen = null,
        AvatarBriefProfile? profile = null) =>
        new(
            Id(id == 0 ? name.GetHashCode(StringComparison.Ordinal) | 1 : id),
            name,
            Vector3.Zero,
            distance,
            heightKnown,
            RadarRelation.Other,
            inRegion,
            sitting,
            typing,
            voice,
            seen ?? TimeSpan.Zero,
            profile,
            WithinDrawDistance: true);

    private static AvatarBriefProfile Profile(
        string? notes = null, PaymentInfo payment = PaymentInfo.Unknown, int? ageDays = null, bool ageHidden = false) =>
        new(Guid.NewGuid(), notes, payment, ageDays is { } d ? Now.AddDays(-d) : null, ageHidden);

    private static string Names(IEnumerable<RadarRow> rows) => string.Join(",", rows.Select(r => r.Name));

    // ---- Filter ----------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_filter_keeps_everyone(string? text)
    {
        var rows = new[] { Row("Alice"), Row("Bob") };

        Assert.Equal("Alice,Bob", Names(RadarTable.Filter(rows, text)));
    }

    [Theory]
    [InlineData("ALI")]
    [InlineData("ali")]
    [InlineData("ice")]
    public void The_filter_is_a_case_insensitive_contains_on_the_name(string text)
    {
        var rows = new[] { Row("Alice Smith"), Row("Bob Jones") };

        Assert.Equal("Alice Smith", Names(RadarTable.Filter(rows, text)));
    }

    [Fact]
    public void A_filter_that_matches_nobody_gives_an_empty_list()
    {
        var rows = new[] { Row("Alice"), Row("Bob") };

        Assert.Empty(RadarTable.Filter(rows, "zed"));
    }

    [Fact]
    public void A_trailing_space_is_part_of_the_filter()
    {
        var rows = new[] { Row("John Smith"), Row("Johnny Cash") };

        Assert.Equal("John Smith", Names(RadarTable.Filter(rows, "john ")));
    }

    [Fact]
    public void Filtering_keeps_the_input_order_and_leaves_the_input_alone()
    {
        var rows = new List<RadarRow> { Row("Zed"), Row("Zoe"), Row("Amy") };

        var kept = RadarTable.Filter(rows, "z");

        Assert.Equal("Zed,Zoe", Names(kept));
        Assert.Equal(3, rows.Count);
    }

    // ---- FormatRange -----------------------------------------------------------------------

    [Theory]
    [InlineData(39.78f, "39.78")]
    [InlineData(0f, "0.00")]
    [InlineData(5f, "5.00")]
    [InlineData(123.456f, "123.46")]
    [InlineData(1020.5f, "1020.50")]
    public void A_known_height_shows_the_distance_with_two_decimals(float distance, string expected)
    {
        Assert.Equal(expected, RadarTable.FormatRange(Row("A", distance: distance), 256f));
    }

    [Fact]
    public void An_unknown_height_shows_at_least_the_far_clip_with_a_greater_than_sign()
    {
        Assert.Equal(">256.00", RadarTable.FormatRange(Row("A", distance: 100f, heightKnown: false), 256f));
    }

    [Fact]
    public void An_unknown_height_beyond_the_far_clip_keeps_its_own_lower_bound()
    {
        Assert.Equal(">300.50", RadarTable.FormatRange(Row("A", distance: 300.5f, heightKnown: false), 256f));
    }

    [Fact]
    public void The_range_uses_a_dot_whatever_the_ui_culture()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            Assert.Equal("39.78", RadarTable.FormatRange(Row("A", distance: 39.78f), 256f));
            Assert.Equal(">256.00", RadarTable.FormatRange(Row("A", heightKnown: false), 256f));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    // ---- FormatSeen ------------------------------------------------------------------------

    [Theory]
    [InlineData(0, "0:00:00")]
    [InlineData(5, "0:00:05")]
    [InlineData(65, "0:01:05")]
    [InlineData(3723, "1:02:03")]
    [InlineData(25 * 3600, "25:00:00")]
    [InlineData(100 * 3600 + 59 * 60 + 59, "100:59:59")]
    public void Seen_reads_hours_minutes_seconds_without_rolling_into_days(int seconds, string expected)
    {
        Assert.Equal(expected, RadarTable.FormatSeen(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void A_negative_seen_span_reads_as_zero()
    {
        Assert.Equal("0:00:00", RadarTable.FormatSeen(TimeSpan.FromSeconds(-30)));
    }

    [Fact]
    public void Seen_drops_fractions_of_a_second_rather_than_rounding_up()
    {
        Assert.Equal("0:00:59", RadarTable.FormatSeen(TimeSpan.FromSeconds(59.9)));
    }

    // ---- FormatAge -------------------------------------------------------------------------

    [Fact]
    public void Age_is_empty_until_the_profile_arrives()
    {
        Assert.Equal("", RadarTable.FormatAge(null, Now));
    }

    [Fact]
    public void Age_is_empty_when_the_profile_has_no_creation_date()
    {
        Assert.Equal("", RadarTable.FormatAge(Profile(), Now));
    }

    [Fact]
    public void A_hidden_age_reads_n_a()
    {
        Assert.Equal("n.a.", RadarTable.FormatAge(Profile(ageDays: 400, ageHidden: true), Now));
    }

    [Fact]
    public void A_hidden_age_reads_n_a_even_without_a_creation_date()
    {
        Assert.Equal("n.a.", RadarTable.FormatAge(Profile(ageHidden: true), Now));
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(91, "91")]
    [InlineData(5000, "5000")]
    public void Age_is_the_whole_days(int days, string expected)
    {
        Assert.Equal(expected, RadarTable.FormatAge(Profile(ageDays: days), Now));
    }

    // ---- FormatPayment ---------------------------------------------------------------------

    [Theory]
    [InlineData(PaymentInfo.Used, "$$")]
    [InlineData(PaymentInfo.OnFile, "$")]
    [InlineData(PaymentInfo.None, "")]
    [InlineData(PaymentInfo.Unknown, "")]
    public void Payment_is_one_or_two_dollar_signs_or_nothing(PaymentInfo payment, string expected)
    {
        Assert.Equal(expected, RadarTable.FormatPayment(payment));
    }

    // ---- BandOf ----------------------------------------------------------------------------

    [Theory]
    [InlineData(0f, RangeBand.Say)]
    [InlineData(19.99f, RangeBand.Say)]
    [InlineData(20f, RangeBand.Say)]
    [InlineData(20.01f, RangeBand.Shout)]
    [InlineData(99.99f, RangeBand.Shout)]
    [InlineData(100f, RangeBand.Shout)]
    [InlineData(100.01f, RangeBand.Far)]
    [InlineData(1020f, RangeBand.Far)]
    public void The_chat_band_bounds_are_inclusive(float distance, RangeBand expected)
    {
        Assert.Equal(expected, RadarTable.BandOf(distance));
    }

    [Fact]
    public void The_chat_ranges_can_be_overridden()
    {
        Assert.Equal(RangeBand.Say, RadarTable.BandOf(10f, 10f, 30f));
        Assert.Equal(RangeBand.Shout, RadarTable.BandOf(30f, 10f, 30f));
        Assert.Equal(RangeBand.Far, RadarTable.BandOf(30.5f, 10f, 30f));
    }

    // ---- Counts ----------------------------------------------------------------------------

    [Fact]
    public void Counts_are_total_in_region_and_in_chat_range()
    {
        var rows = new[]
        {
            Row("A", distance: 5f),
            Row("B", distance: 20f),
            Row("C", distance: 20.5f),
            Row("D", distance: 300f, inRegion: false),
            Row("E", distance: 50f, inRegion: false),
        };

        var (total, inRegion, inChat) = RadarTable.Counts(rows);

        Assert.Equal(5, total);
        Assert.Equal(3, inRegion);
        Assert.Equal(2, inChat);
        Assert.Equal("[5/3/2]", RadarTable.CountsSuffix(rows));
    }

    [Fact]
    public void The_chat_range_for_the_counts_can_be_overridden()
    {
        var rows = new[] { Row("A", distance: 5f), Row("B", distance: 30f) };

        Assert.Equal(2, RadarTable.Counts(rows, 50f).InChatRange);
        Assert.Equal("[2/2/2]", RadarTable.CountsSuffix(rows, 50f));
    }

    [Fact]
    public void An_empty_table_counts_zero_everywhere()
    {
        Assert.Equal("[0/0/0]", RadarTable.CountsSuffix(Array.Empty<RadarRow>()));
    }

    // ---- Sort ------------------------------------------------------------------------------

    private static string SortedNames(IEnumerable<RadarRow> rows, RadarColumn column, bool ascending) =>
        Names(RadarTable.Sort(rows, column, ascending, Now));

    [Fact]
    public void Name_sorts_case_insensitively_in_both_directions()
    {
        var rows = new[] { Row("carol"), Row("Alice"), Row("bob") };

        Assert.Equal("Alice,bob,carol", SortedNames(rows, RadarColumn.Name, true));
        Assert.Equal("carol,bob,Alice", SortedNames(rows, RadarColumn.Name, false));
    }

    [Fact]
    public void Range_sorts_by_distance()
    {
        var rows = new[] { Row("far", distance: 90f), Row("near", distance: 3f), Row("mid", distance: 40f) };

        Assert.Equal("near,mid,far", SortedNames(rows, RadarColumn.Range, true));
        Assert.Equal("far,mid,near", SortedNames(rows, RadarColumn.Range, false));
    }

    [Fact]
    public void Seen_sorts_by_how_long_they_have_been_listed()
    {
        var rows = new[]
        {
            Row("hour", seen: TimeSpan.FromHours(1)),
            Row("new", seen: TimeSpan.FromSeconds(4)),
            Row("day", seen: TimeSpan.FromHours(30)),
        };

        Assert.Equal("new,hour,day", SortedNames(rows, RadarColumn.Seen, true));
        Assert.Equal("day,hour,new", SortedNames(rows, RadarColumn.Seen, false));
    }

    [Theory]
    [InlineData(RadarColumn.InRegion)]
    [InlineData(RadarColumn.Sitting)]
    [InlineData(RadarColumn.Typing)]
    public void A_flag_column_puts_true_last_ascending_and_first_descending(RadarColumn column)
    {
        RadarRow Make(string name, bool flag) => column switch
        {
            RadarColumn.InRegion => Row(name, inRegion: flag),
            RadarColumn.Sitting => Row(name, sitting: flag),
            _ => Row(name, typing: flag),
        };
        var rows = new[] { Make("b-yes", true), Make("c-no", false), Make("a-yes", true), Make("d-no", false) };

        Assert.Equal("c-no,d-no,a-yes,b-yes", SortedNames(rows, column, true));
        Assert.Equal("a-yes,b-yes,c-no,d-no", SortedNames(rows, column, false));
    }

    [Fact]
    public void Voice_sorts_by_level_with_unknown_last_in_both_directions()
    {
        var rows = new[]
        {
            Row("none", voice: null),
            Row("loud", voice: 0.8f),
            Row("quiet", voice: 0.2f),
            Row("mid", voice: 0.5f),
            Row("also-none", voice: null),
        };

        Assert.Equal("quiet,mid,loud,also-none,none", SortedNames(rows, RadarColumn.Voice, true));
        Assert.Equal("loud,mid,quiet,also-none,none", SortedNames(rows, RadarColumn.Voice, false));
    }

    [Fact]
    public void Age_sorts_by_days_with_unknown_and_hidden_last_in_both_directions()
    {
        var rows = new[]
        {
            Row("hidden", profile: Profile(ageDays: 900, ageHidden: true)),
            Row("old", profile: Profile(ageDays: 500)),
            Row("no-profile"),
            Row("young", profile: Profile(ageDays: 10)),
            Row("no-date", profile: Profile()),
        };

        Assert.Equal("young,old,hidden,no-date,no-profile", SortedNames(rows, RadarColumn.Age, true));
        Assert.Equal("old,young,hidden,no-date,no-profile", SortedNames(rows, RadarColumn.Age, false));
    }

    [Fact]
    public void Age_is_measured_against_the_clock_that_is_passed_in()
    {
        var rows = new[]
        {
            Row("a", profile: new AvatarBriefProfile(Guid.NewGuid(), BornOnUtc: Now.AddDays(-100))),
            Row("b", profile: new AvatarBriefProfile(Guid.NewGuid(), BornOnUtc: Now.AddDays(-200))),
        };

        // Same two births, one clock far in the future: the order does not change, but it must not
        // throw or read the machine clock.
        var later = RadarTable.Sort(rows, RadarColumn.Age, true, Now.AddYears(5));

        Assert.Equal("a,b", Names(later));
    }

    [Fact]
    public void Payment_sorts_none_then_on_file_then_used_with_unknown_last_in_both_directions()
    {
        var rows = new[]
        {
            Row("used", profile: Profile(payment: PaymentInfo.Used)),
            Row("no-profile"),
            Row("none", profile: Profile(payment: PaymentInfo.None)),
            Row("on-file", profile: Profile(payment: PaymentInfo.OnFile)),
            Row("not-arrived", profile: Profile(payment: PaymentInfo.Unknown)),
        };

        Assert.Equal("none,on-file,used,no-profile,not-arrived", SortedNames(rows, RadarColumn.Payment, true));
        Assert.Equal("used,on-file,none,no-profile,not-arrived", SortedNames(rows, RadarColumn.Payment, false));
    }

    [Fact]
    public void Note_sorts_without_before_with_and_no_profile_last_in_both_directions()
    {
        var rows = new[]
        {
            Row("with", profile: Profile(notes: "met at the market")),
            Row("no-profile"),
            Row("without", profile: Profile(notes: "")),
            Row("with-too", profile: Profile(notes: "friend of a friend")),
        };

        Assert.Equal("without,with,with-too,no-profile", SortedNames(rows, RadarColumn.Note, true));
        Assert.Equal("with,with-too,without,no-profile", SortedNames(rows, RadarColumn.Note, false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Language_is_not_sortable_so_the_input_order_stays(bool ascending)
    {
        var rows = new[] { Row("zed"), Row("amy"), Row("bob") };

        Assert.Equal("zed,amy,bob", SortedNames(rows, RadarColumn.Language, ascending));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Equal_values_fall_back_to_name_then_agent_id_whichever_way_the_column_runs(bool ascending)
    {
        var bravo = Row("bravo", id: 3, distance: 5f);
        var lower = Row("alpha", id: 2, distance: 5f);
        var upper = Row("Alpha", id: 1, distance: 5f);

        // Every permutation of the input must give the same output: nothing may depend on arrival order.
        var expected = "Alpha,alpha,bravo";
        Assert.Equal(expected, SortedNames(new[] { bravo, lower, upper }, RadarColumn.Range, ascending));
        Assert.Equal(expected, SortedNames(new[] { upper, lower, bravo }, RadarColumn.Range, ascending));
        Assert.Equal(expected, SortedNames(new[] { lower, bravo, upper }, RadarColumn.Range, ascending));
    }

    [Fact]
    public void Ties_in_a_flag_column_come_out_in_name_order()
    {
        var rows = new[] { Row("Zoe"), Row("amy"), Row("Mia") };

        Assert.Equal("amy,Mia,Zoe", SortedNames(rows, RadarColumn.InRegion, true));
        Assert.Equal("amy,Mia,Zoe", SortedNames(rows, RadarColumn.InRegion, false));
    }

    [Fact]
    public void Unknown_values_tie_among_themselves_by_name()
    {
        var rows = new[] { Row("zed"), Row("amy"), Row("mia") };

        Assert.Equal("amy,mia,zed", SortedNames(rows, RadarColumn.Voice, true));
        Assert.Equal("amy,mia,zed", SortedNames(rows, RadarColumn.Voice, false));
    }

    [Fact]
    public void Rows_identical_in_every_key_keep_their_input_order()
    {
        var first = Row("same", id: 7, distance: 5f) with { Position = new Vector3(1, 0, 0) };
        var second = Row("same", id: 7, distance: 5f) with { Position = new Vector3(2, 0, 0) };

        var sorted = RadarTable.Sort(new[] { first, second }, RadarColumn.Range, true, Now);

        Assert.Same(first, sorted[0]);
        Assert.Same(second, sorted[1]);
    }

    [Fact]
    public void Sorting_returns_a_copy_and_leaves_the_input_alone()
    {
        var rows = new List<RadarRow> { Row("far", distance: 90f), Row("near", distance: 3f) };

        var sorted = RadarTable.Sort(rows, RadarColumn.Range, true, Now);

        Assert.Equal("far,near", Names(rows));
        Assert.Equal("near,far", Names(sorted));
        Assert.NotSame(rows, sorted);
    }
}
