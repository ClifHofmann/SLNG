using SLNG.Core.Input;
using Xunit;

namespace SLNG.Core.Tests;

public class KeyChordTests
{
    [Theory]
    [InlineData("Ctrl+Shift+I", "Ctrl+Shift+I")]
    [InlineData("shift+ctrl+i", "Ctrl+Shift+I")]
    [InlineData("Control+Alt+Shift+Left", "Ctrl+Alt+Shift+Left")]
    [InlineData("Alt+Left", "Alt+Left")]
    [InlineData("F3", "F3")]
    [InlineData("esc", "Escape")]
    [InlineData("Ctrl+PgUp", "Ctrl+PageUp")]
    [InlineData("Ctrl+,", "Ctrl+Comma")]
    [InlineData("Ctrl+\\", "Ctrl+Backslash")]
    [InlineData("Ctrl+Alt+Shift+=", "Ctrl+Alt+Shift+Equal")]
    [InlineData("Ctrl++", "Ctrl+Plus")]
    [InlineData("+", "Plus")]
    [InlineData("  Ctrl + O ", "Ctrl+O")]
    public void Parse_gives_the_canonical_form(string text, string canonical)
    {
        Assert.True(KeyChord.TryParse(text, out var chord));
        Assert.Equal(canonical, chord.ToString());
    }

    [Fact]
    public void Every_bindable_key_round_trips_with_every_modifier_combination()
    {
        foreach (var key in KeyNames.All)
        {
            for (int m = 0; m < 8; m++)
            {
                var chord = new KeyChord(key, (KeyMods)m);
                Assert.True(KeyChord.TryParse(chord.ToString(), out var back), chord.ToString());
                Assert.Equal(chord, back);
            }
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl")]              // a modifier alone is not a chord
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Ctrl+A")]       // repeated modifier
    [InlineData("Foo+A")]             // unknown modifier
    [InlineData("Ctrl+NoSuchKey")]
    [InlineData("Ctrl++A")]
    [InlineData("Win+A")]             // Meta is out of scope
    public void Parse_rejects_what_is_not_a_chord(string text)
    {
        Assert.False(KeyChord.TryParse(text, out _));
    }

    [Fact]
    public void Constructing_a_chord_from_an_unknown_key_throws()
    {
        Assert.Throws<ArgumentException>(() => new KeyChord("Ctrl"));
    }

    [Fact]
    public void Parse_throws_on_garbage_but_TryParse_never_does()
    {
        Assert.Throws<FormatException>(() => KeyChord.Parse("???"));
        Assert.False(KeyChord.TryParse(null, out _));
    }

    [Fact]
    public void A_list_round_trips_and_drops_the_unparsable_and_the_duplicates()
    {
        var list = KeyChord.ParseList("Ctrl+I;Alt+Left;Ctrl+I;;bogus+X;Ctrl+Comma", out int skipped);
        Assert.Equal(1, skipped);
        Assert.Equal(new[] { "Ctrl+I", "Alt+Left", "Ctrl+Comma" }, list.Select(c => c.ToString()));
        Assert.Equal("Ctrl+I;Alt+Left;Ctrl+Comma", KeyChord.FormatList(list));
        Assert.Empty(KeyChord.ParseList("", out _));
    }

    [Fact]
    public void Text_fields_keep_their_editing_chords_and_nothing_else()
    {
        // typing, caret movement, the clipboard, undo
        foreach (var owned in new[] { "A", "Shift+A", "Space", "Backspace", "Left", "Shift+End", "Ctrl+C", "Ctrl+V", "Ctrl+X", "Ctrl+A", "Ctrl+Z", "Ctrl+Y", "Ctrl+Shift+Z", "Ctrl+Left", "Enter" })
            Assert.True(TextEditChords.Owns(KeyChord.Parse(owned)), owned);

        // commands: menu shortcuts and F-keys still fire from a text field
        foreach (var command in new[] { "Ctrl+I", "Ctrl+Shift+1", "F3", "Alt+Shift+N", "Ctrl+Alt+R", "Ctrl+Shift+S", "Ctrl+W" })
            Assert.False(TextEditChords.Owns(KeyChord.Parse(command)), command);
    }
}
