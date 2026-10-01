using System;
using System.Linq;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

public class RadarColumnSettingsTests
{
    private static string Ids(System.Collections.Generic.IEnumerable<RadarColumn> columns) =>
        string.Join(",", columns.Select(c => RadarColumns.Info(c).Id));

    // ---- Registry --------------------------------------------------------------------------

    [Fact]
    public void The_registry_lists_every_column_in_display_order()
    {
        var all = RadarColumns.All;

        Assert.Equal(Enum.GetValues<RadarColumn>().Length, all.Count);
        for (var i = 0; i < all.Count; i++)
        {
            // Info() is an array lookup by enum value, so position must equal the enum value.
            Assert.Equal((RadarColumn)i, all[i].Column);
            Assert.Same(all[i], RadarColumns.Info((RadarColumn)i));
        }

        Assert.Equal(
            "name,voice,in_region,typing,sitting,payment,note,age,seen,range,language",
            string.Join(",", all.Select(c => c.Id)));
    }

    [Fact]
    public void Ids_are_unique_lowercase_and_the_i18n_keys_follow_them()
    {
        foreach (var info in RadarColumns.All)
        {
            Assert.Equal(info.Id.ToLowerInvariant(), info.Id);
            Assert.Equal("ui.radar.col." + info.Id, info.TitleKey);
            Assert.Equal("ui.radar.col." + info.Id + ".tip", info.TooltipKey);
        }

        Assert.Equal(RadarColumns.All.Count, RadarColumns.All.Select(c => c.Id).Distinct().Count());
    }

    [Fact]
    public void Name_is_always_visible_sortable_and_offered()
    {
        var name = RadarColumns.Info(RadarColumn.Name);

        Assert.True(name.AlwaysVisible);
        Assert.True(name.Sortable);
        Assert.True(name.DefaultVisible);
        Assert.True(name.Offered);
    }

    [Theory]
    [InlineData(RadarColumn.Typing)]
    [InlineData(RadarColumn.Language)]
    public void A_column_without_a_data_source_is_not_offered_and_not_on_by_default(RadarColumn column)
    {
        var info = RadarColumns.Info(column);

        Assert.False(info.Offered);
        Assert.False(info.DefaultVisible);
        Assert.False(info.AlwaysVisible);
    }

    [Fact]
    public void Voice_is_on_by_default_even_though_it_has_no_data_yet()
    {
        var voice = RadarColumns.Info(RadarColumn.Voice);

        Assert.True(voice.Offered);
        Assert.True(voice.DefaultVisible);
        Assert.True(voice.Sortable);
        Assert.False(voice.AlwaysVisible);
    }

    [Fact]
    public void Only_language_cannot_be_sorted()
    {
        Assert.Equal("language", string.Join(",", RadarColumns.All.Where(c => !c.Sortable).Select(c => c.Id)));
    }

    [Theory]
    [InlineData("name", true, RadarColumn.Name)]
    [InlineData("in_region", true, RadarColumn.InRegion)]
    [InlineData("  Range ", true, RadarColumn.Range)]
    [InlineData("SEEN", true, RadarColumn.Seen)]
    [InlineData("bogus", false, default(RadarColumn))]
    [InlineData("", false, default(RadarColumn))]
    [InlineData(null, false, default(RadarColumn))]
    public void An_id_is_read_back_trimmed_and_case_insensitively(string? id, bool ok, RadarColumn expected)
    {
        Assert.Equal(ok, RadarColumns.TryParseId(id, out var column));
        Assert.Equal(expected, column);
    }

    // ---- Defaults and SetVisible -----------------------------------------------------------

    [Fact]
    public void The_defaults_show_every_offered_column_and_sort_by_range_ascending()
    {
        var s = RadarColumnSettings.CreateDefault();

        Assert.Equal("name,voice,in_region,sitting,payment,note,age,seen,range", Ids(s.VisibleColumns));
        Assert.Equal(RadarColumn.Range, s.SortColumn);
        Assert.True(s.SortAscending);
        Assert.Equal("range", s.SortId);
        Assert.Equal("", s.SerializeShown());
        Assert.Equal("", s.SerializeHidden());
    }

    [Fact]
    public void Hiding_a_column_removes_it_and_showing_it_again_restores_display_order()
    {
        var s = RadarColumnSettings.CreateDefault();

        s.SetVisible(RadarColumn.Age, false);
        s.SetVisible(RadarColumn.Seen, false);

        Assert.False(s.IsVisible(RadarColumn.Age));
        Assert.Equal("name,voice,in_region,sitting,payment,note,range", Ids(s.VisibleColumns));

        s.SetVisible(RadarColumn.Seen, true);
        s.SetVisible(RadarColumn.Age, true);

        Assert.Equal("name,voice,in_region,sitting,payment,note,age,seen,range", Ids(s.VisibleColumns));
    }

    [Fact]
    public void The_name_column_cannot_be_hidden()
    {
        var s = RadarColumnSettings.CreateDefault();

        s.SetVisible(RadarColumn.Name, false);

        Assert.True(s.IsVisible(RadarColumn.Name));
        Assert.Contains(RadarColumn.Name, s.VisibleColumns);
        Assert.Equal("", s.SerializeHidden());
    }

    [Theory]
    [InlineData(RadarColumn.Typing)]
    [InlineData(RadarColumn.Language)]
    public void A_column_that_is_not_offered_cannot_be_shown(RadarColumn column)
    {
        var s = RadarColumnSettings.CreateDefault();

        s.SetVisible(column, true);

        Assert.False(s.IsVisible(column));
        Assert.DoesNotContain(column, s.VisibleColumns);
        Assert.Equal("", s.SerializeShown());
    }

    [Fact]
    public void Reset_columns_goes_back_to_the_defaults_and_keeps_the_sort()
    {
        var s = RadarColumnSettings.CreateDefault();
        s.SetVisible(RadarColumn.Age, false);
        s.SetVisible(RadarColumn.Voice, false);
        s.SortColumn = RadarColumn.Seen;
        s.SortAscending = false;

        s.ResetColumns();

        Assert.Equal("name,voice,in_region,sitting,payment,note,age,seen,range", Ids(s.VisibleColumns));
        Assert.Equal("", s.SerializeShown());
        Assert.Equal("", s.SerializeHidden());
        Assert.Equal(RadarColumn.Seen, s.SortColumn);
        Assert.False(s.SortAscending);
    }

    // ---- Serialize -------------------------------------------------------------------------

    [Fact]
    public void Only_explicit_choices_are_written_each_list_in_display_order()
    {
        var s = RadarColumnSettings.CreateDefault();
        s.SetVisible(RadarColumn.Range, false);
        s.SetVisible(RadarColumn.Voice, false);
        s.SetVisible(RadarColumn.Note, true);
        s.SetVisible(RadarColumn.Age, true);

        Assert.Equal("voice,range", s.SerializeHidden());
        Assert.Equal("note,age", s.SerializeShown());
    }

    [Fact]
    public void A_choice_changed_back_is_written_as_the_new_choice_only()
    {
        var s = RadarColumnSettings.CreateDefault();
        s.SetVisible(RadarColumn.Seen, false);
        s.SetVisible(RadarColumn.Seen, true);

        Assert.Equal("seen", s.SerializeShown());
        Assert.Equal("", s.SerializeHidden());
    }

    [Fact]
    public void Settings_survive_a_round_trip()
    {
        var s = RadarColumnSettings.CreateDefault();
        s.SetVisible(RadarColumn.Voice, false);
        s.SetVisible(RadarColumn.Seen, false);
        s.SetVisible(RadarColumn.Note, true);
        s.SortColumn = RadarColumn.Age;
        s.SortAscending = false;

        var back = RadarColumnSettings.Parse(s.SerializeShown(), s.SerializeHidden(), s.SortId, s.SortAscending);

        Assert.Equal(Ids(s.VisibleColumns), Ids(back.VisibleColumns));
        Assert.Equal(s.SerializeShown(), back.SerializeShown());
        Assert.Equal(s.SerializeHidden(), back.SerializeHidden());
        Assert.Equal(RadarColumn.Age, back.SortColumn);
        Assert.False(back.SortAscending);
    }

    // ---- Parse -----------------------------------------------------------------------------

    [Fact]
    public void Nothing_saved_gives_the_defaults()
    {
        var s = RadarColumnSettings.Parse(null, null, null, null);

        Assert.Equal("name,voice,in_region,sitting,payment,note,age,seen,range", Ids(s.VisibleColumns));
        Assert.Equal(RadarColumn.Range, s.SortColumn);
        Assert.True(s.SortAscending);
    }

    [Fact]
    public void Unknown_ids_blanks_and_whitespace_are_ignored()
    {
        var s = RadarColumnSettings.Parse(" note , ,bogus,,  AGE ", ", ,  seen ,nonsense,", null, null);

        Assert.True(s.IsVisible(RadarColumn.Note));
        Assert.True(s.IsVisible(RadarColumn.Age));
        Assert.False(s.IsVisible(RadarColumn.Seen));
        Assert.Equal("note,age", s.SerializeShown());
        Assert.Equal("seen", s.SerializeHidden());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",,,")]
    public void A_blank_list_changes_nothing(string blank)
    {
        var s = RadarColumnSettings.Parse(blank, blank, null, null);

        Assert.Equal("name,voice,in_region,sitting,payment,note,age,seen,range", Ids(s.VisibleColumns));
    }

    [Fact]
    public void A_column_in_both_lists_counts_as_shown()
    {
        var s = RadarColumnSettings.Parse("age,seen", "seen,range", null, null);

        Assert.True(s.IsVisible(RadarColumn.Seen));
        Assert.False(s.IsVisible(RadarColumn.Range));
        Assert.Equal("age,seen", s.SerializeShown());
        Assert.Equal("range", s.SerializeHidden());
    }

    [Fact]
    public void The_name_column_stays_even_if_a_file_hides_it()
    {
        var s = RadarColumnSettings.Parse(null, "name,age", null, null);

        Assert.True(s.IsVisible(RadarColumn.Name));
        Assert.False(s.IsVisible(RadarColumn.Age));
    }

    [Fact]
    public void A_column_that_is_not_offered_stays_hidden_even_if_a_file_shows_it()
    {
        var s = RadarColumnSettings.Parse("typing,language,age", null, null, null);

        Assert.False(s.IsVisible(RadarColumn.Typing));
        Assert.False(s.IsVisible(RadarColumn.Language));
        Assert.Equal("age", s.SerializeShown());
    }

    [Theory]
    [InlineData("seen", RadarColumn.Seen)]
    [InlineData("  AGE  ", RadarColumn.Age)]
    [InlineData("name", RadarColumn.Name)]
    public void A_valid_sort_id_is_kept(string id, RadarColumn expected)
    {
        Assert.Equal(expected, RadarColumnSettings.Parse(null, null, id, null).SortColumn);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bogus")]
    [InlineData("language")] // not sortable, not offered
    [InlineData("typing")] // sortable in principle, but not offered
    public void A_missing_unknown_or_unusable_sort_falls_back_to_range(string? id)
    {
        Assert.Equal(RadarColumn.Range, RadarColumnSettings.Parse(null, null, id, false).SortColumn);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void The_sort_direction_defaults_to_ascending(bool? saved, bool expected)
    {
        Assert.Equal(expected, RadarColumnSettings.Parse(null, null, null, saved).SortAscending);
    }

    // ---- Forward compatibility -------------------------------------------------------------

    [Fact]
    public void A_column_added_later_shows_up_for_a_user_whose_saved_lists_never_mention_it()
    {
        // A file written before "seen" existed: it knows nothing about that column.
        var s = RadarColumnSettings.Parse("name,voice", "range", "age", true);

        Assert.True(s.IsVisible(RadarColumn.Seen), "an untouched default-on column must follow its default");
        Assert.True(s.IsVisible(RadarColumn.Age));
        Assert.False(s.IsVisible(RadarColumn.Range));
        Assert.Equal("name,voice,in_region,sitting,payment,note,age,seen", Ids(s.VisibleColumns));
    }

    [Fact]
    public void A_column_that_is_off_by_default_stays_off_for_a_user_who_never_chose()
    {
        var s = RadarColumnSettings.Parse("voice", "range", null, null);

        Assert.False(s.IsVisible(RadarColumn.Typing));
        Assert.False(s.IsVisible(RadarColumn.Language));
    }

    [Fact]
    public void A_user_choice_is_kept_even_when_it_matches_the_default()
    {
        // "voice" is on by default, but this user clicked it on: it is recorded, so it survives a
        // later change of the default.
        var s = RadarColumnSettings.CreateDefault();
        s.SetVisible(RadarColumn.Voice, true);

        Assert.Equal("voice", s.SerializeShown());
    }
}
