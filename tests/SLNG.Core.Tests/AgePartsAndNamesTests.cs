using System;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

public class AgePartsTests
{
    private static DateTime D(int y, int m, int d, int hour = 0) => new(y, m, d, hour, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_same_day_is_zero()
    {
        Assert.Equal(new AgeParts(0, 0, 0), AgeParts.Between(D(2026, 10, 1), D(2026, 10, 1, 23)));
    }

    [Fact]
    public void Years_months_and_days_are_calendar_based()
    {
        // 14 May 2007 to 1 Oct 2026: 19 years, then 4 months (to 14 Sep), then 17 days.
        Assert.Equal(new AgeParts(19, 4, 17), AgeParts.Between(D(2007, 5, 14), D(2026, 10, 1)));
    }

    [Fact]
    public void The_day_before_an_anniversary_is_still_the_year_before()
    {
        Assert.Equal(new AgeParts(0, 11, 29), AgeParts.Between(D(2025, 10, 1), D(2026, 9, 30)));
        Assert.Equal(new AgeParts(1, 0, 0), AgeParts.Between(D(2025, 10, 1), D(2026, 10, 1)));
    }

    [Fact]
    public void A_leap_day_birthday_is_a_real_date_in_other_years()
    {
        // 29 Feb 2020 -> the anniversary in 2021 clamps to 28 Feb.
        Assert.Equal(new AgeParts(1, 0, 0), AgeParts.Between(D(2020, 2, 29), D(2021, 2, 28)));
        // ...so on 29 Jan 2021 eleven months are complete and no day is left over.
        Assert.Equal(new AgeParts(0, 11, 0), AgeParts.Between(D(2020, 2, 29), D(2021, 1, 29)));
    }

    [Fact]
    public void A_month_end_start_does_not_overshoot()
    {
        // 31 Jan + 1 month clamps to 28 Feb; 1 Mar is then one day after that month, not two months.
        Assert.Equal(new AgeParts(0, 1, 1), AgeParts.Between(D(2026, 1, 31), D(2026, 3, 1)));
    }

    [Fact]
    public void A_clock_behind_the_creation_date_is_zero_not_negative()
    {
        Assert.Equal(new AgeParts(0, 0, 0), AgeParts.Between(D(2026, 10, 5), D(2026, 10, 1)));
    }

    [Fact]
    public void The_profile_asks_for_its_own_age()
    {
        var profile = new AvatarBriefProfile(Guid.NewGuid(), BornOnUtc: D(2007, 5, 14));

        Assert.Equal(new AgeParts(19, 4, 17), profile.AgePartsAt(D(2026, 10, 1)));
        Assert.Null(new AvatarBriefProfile(Guid.NewGuid()).AgePartsAt(D(2026, 10, 1)));
        Assert.Null(new AvatarBriefProfile(Guid.NewGuid(), BornOnUtc: D(2007, 5, 14), AgeHidden: true).AgePartsAt(D(2026, 10, 1)));
    }
}

public class AvatarNamesTests
{
    [Theory]
    [InlineData("", "Yakety", "Resident", "Yakety")]
    [InlineData("YaketyHush Resident", "YaketyHush", "Resident", "YaketyHush")]   // default display name = legacy name
    [InlineData("Yakety Hush", "YaketyHush", "Resident", "Yakety Hush")]           // a chosen one is kept
    [InlineData("", "Sidney", "Trezuguet", "Sidney Trezuguet")]
    [InlineData("Sid", "Sidney", "Trezuguet", "Sid")]
    [InlineData("Sidney Trezuguet", "Sidney", "Trezuguet", "Sidney Trezuguet")]
    [InlineData(null, "Oz", "Resident", "Oz")]
    [InlineData("", "Oz", "", "Oz")]
    [InlineData("Local Resident", "Bob", "Resident", "Local Resident")]            // chosen, even if it ends that way
    public void ForList_drops_the_default_last_name_only(string? display, string first, string last, string expected)
    {
        Assert.Equal(expected, AvatarNames.ForList(display, first, last));
    }

    [Fact]
    public void ForList_is_empty_when_nothing_is_known()
    {
        Assert.Equal("", AvatarNames.ForList(null, null, null));
        Assert.Equal("", AvatarNames.ForList("  ", "", ""));
    }

    [Theory]
    [InlineData("Oz Resident", "Oz")]
    [InlineData("Sidney Trezuguet", "Sidney Trezuguet")]
    [InlineData("Local Resident Person", "Local Resident Person")]
    [InlineData("Resident", "Resident")]
    [InlineData(" Resident", " Resident")]
    [InlineData("", "")]
    public void A_cached_name_loses_a_trailing_Resident_only_in_the_two_word_form(string name, string expected)
    {
        Assert.Equal(expected, AvatarNames.WithoutDefaultLastName(name));
    }
}
