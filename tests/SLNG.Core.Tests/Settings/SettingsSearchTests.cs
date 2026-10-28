using SLNG.Core.Settings;
using Xunit;

namespace SLNG.Core.Tests.Settings;

public class SettingsSearchTests
{
    private static bool Hit(string? query, params string?[] texts) => SettingsSearch.Matches(query, texts);

    [Fact]
    public void Normalize_lowercases_folds_diacritics_and_collapses_white_space()
    {
        Assert.Equal("scharfe", SettingsSearch.Normalize("Schärfe"));
        Assert.Equal("strasse", SettingsSearch.Normalize("Straße"));
        Assert.Equal("strasse", SettingsSearch.Normalize("STRAẞE"));
        Assert.Equal("a b c", SettingsSearch.Normalize("  A \t B\n  c  "));
        Assert.Equal("", SettingsSearch.Normalize(null));
        Assert.Equal("", SettingsSearch.Normalize("   "));
    }

    [Fact]
    public void Normalize_gives_a_precomposed_and_a_decomposed_letter_the_same_text()
    {
        Assert.Equal(SettingsSearch.Normalize("ä"), SettingsSearch.Normalize("ä"));
    }

    [Fact]
    public void Case_and_diacritics_do_not_matter_in_either_direction()
    {
        Assert.True(Hit("scharfe", "Schärfe"));
        Assert.True(Hit("schärfe", "Schärfe"));
        Assert.True(Hit("SCHARFE", "Schärfe"));
        Assert.True(Hit("schärfe", "Scharfe")); // the accent on the query only
    }

    [Fact]
    public void Sharp_s_matches_ss()
    {
        Assert.True(Hit("strasse", "Straße"));
        Assert.True(Hit("straße", "Strasse"));
        Assert.True(Hit("STRAẞE", "strasse"));
    }

    [Fact]
    public void One_word_is_a_substring_of_any_text()
    {
        Assert.True(Hit("dow", "Shadows"));
        Assert.True(Hit("band", "Draw distance", "Max bandwidth"));
        Assert.False(Hit("xyz", "Shadows", "Max bandwidth"));
    }

    [Fact]
    public void Several_words_must_all_occur_in_any_order()
    {
        Assert.True(Hit("shadow distance", "Shadow distance"));
        Assert.True(Hit("distance shadow", "Shadow distance"));
        Assert.False(Hit("shadow resolution", "Shadow distance"));
    }

    [Fact]
    public void Several_words_may_be_found_in_different_texts_of_one_entry()
    {
        Assert.True(Hit("shadows simulator", "Shadows", "The most the simulator may send"));
        Assert.False(Hit("shadows camera", "Shadows", "The most the simulator may send"));
    }

    [Fact]
    public void Words_split_on_any_white_space_and_repeated_white_space_is_ignored()
    {
        Assert.True(Hit("  shadow \t distance  ", "Shadow distance"));
    }

    [Fact]
    public void An_empty_query_matches_everything_even_an_entry_without_texts()
    {
        Assert.True(Hit("", "Shadows"));
        Assert.True(Hit("   ", "Shadows"));
        Assert.True(Hit(null, "Shadows"));
        Assert.True(Hit(""));
        Assert.True(SettingsSearch.Parse("  ").IsEmpty);
    }

    [Fact]
    public void A_query_never_matches_an_entry_without_texts()
    {
        Assert.False(Hit("a"));
        Assert.False(Hit("a", null, "", "  "));
    }

    [Fact]
    public void An_english_text_matches_while_the_german_one_does_not()
    {
        var germanOnly = new SettingsSearchEntry(new[] { "Max. Bandbreite" });
        var germanAndEnglish = new SettingsSearchEntry(new[] { "Max. Bandbreite", "Maximum bandwidth" });
        var query = SettingsSearch.Parse("bandwidth");

        Assert.False(germanOnly.Matches(query));
        Assert.True(germanAndEnglish.Matches(query));
        Assert.True(germanAndEnglish.Matches(SettingsSearch.Parse("bandbreite")));
    }

    [Fact]
    public void A_keyword_that_is_no_label_is_found_by_a_part_of_it()
    {
        Assert.True(Hit("farclip", "Draw distance", "RenderFarClip"));
        Assert.True(Hit("fps", "Max frames", "MaxFPS"));
    }

    [Fact]
    public void An_entry_drops_empty_and_duplicate_texts()
    {
        var entry = new SettingsSearchEntry(new string?[] { "Schärfe", "scharfe", null, "", "  ", "Sharpness" });
        Assert.Equal(2, entry.TextCount);
    }

    [Fact]
    public void Parse_gives_the_normalized_words()
    {
        var query = SettingsSearch.Parse("  Schärfe   STRAßE ");
        Assert.Equal(new[] { "scharfe", "strasse" }, query.Words);
        Assert.False(query.IsEmpty);
    }
}
