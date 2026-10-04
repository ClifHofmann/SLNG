using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

public sealed class PersonNameDisplayTests
{
    [Fact]
    public void A_display_name_is_shown_when_there_is_one()
        => Assert.Equal("Clif ☆", PersonNameDisplay.Choose("Clifton Howlett", "Clif ☆", useDisplayNames: true));

    [Fact]
    public void The_legacy_name_is_shown_when_display_names_are_off()
        => Assert.Equal("Clifton Howlett", PersonNameDisplay.Choose("Clifton Howlett", "Clif ☆", useDisplayNames: false));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_display_name_falls_back_to_the_legacy_name(string? display)
        => Assert.Equal("Clifton Howlett", PersonNameDisplay.Choose("Clifton Howlett", display, true));

    [Fact]
    public void A_display_name_that_only_repeats_the_legacy_name_is_the_legacy_name()
        => Assert.Equal("Clifton Howlett", PersonNameDisplay.Choose("Clifton Howlett", "clifton howlett", true));

    [Fact]
    public void A_display_name_is_better_than_no_name_at_all()
        => Assert.Equal("Clif", PersonNameDisplay.Choose("", "Clif", true));
}
