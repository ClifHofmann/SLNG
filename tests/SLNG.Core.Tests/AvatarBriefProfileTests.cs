using System;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

public class AvatarBriefProfileTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Nothing_has_arrived_yet_means_unknown_not_empty()
    {
        var p = new AvatarBriefProfile(Guid.NewGuid());

        Assert.Null(p.Notes);
        Assert.False(p.HasNote);
        Assert.Equal(PaymentInfo.Unknown, p.Payment);
        Assert.Null(p.AgeInDays(Now));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("met at the market", true)]
    public void A_note_shows_only_when_it_is_not_empty(string? notes, bool expected)
    {
        Assert.Equal(expected, new AvatarBriefProfile(Guid.NewGuid(), notes).HasNote);
    }

    [Theory]
    [InlineData(false, false, PaymentInfo.None)]
    [InlineData(true, false, PaymentInfo.OnFile)]
    [InlineData(false, true, PaymentInfo.Used)]
    [InlineData(true, true, PaymentInfo.Used)]
    public void Used_beats_on_file(bool identified, bool transacted, PaymentInfo expected)
    {
        Assert.Equal(expected, AvatarBriefProfile.PaymentFrom(identified, transacted));
    }

    [Fact]
    public void Age_is_whole_days_since_the_account_was_made()
    {
        var p = new AvatarBriefProfile(Guid.NewGuid(), BornOnUtc: Now.AddDays(-91).AddHours(-5));

        Assert.Equal(91, p.AgeInDays(Now));
    }

    [Fact]
    public void A_hidden_age_is_not_a_number()
    {
        var p = new AvatarBriefProfile(Guid.NewGuid(), BornOnUtc: Now.AddDays(-400), AgeHidden: true);

        Assert.Null(p.AgeInDays(Now));
    }

    [Fact]
    public void A_creation_date_in_the_future_is_age_zero_not_negative()
    {
        var p = new AvatarBriefProfile(Guid.NewGuid(), BornOnUtc: Now.AddDays(3));

        Assert.Equal(0, p.AgeInDays(Now));
    }

    [Theory]
    [InlineData("2007-05-14", 2007, 5, 14)]
    [InlineData("05/14/2007", 2007, 5, 14)]
    [InlineData("  2007-05-14  ", 2007, 5, 14)]
    public void The_legacy_creation_date_parses(string text, int year, int month, int day)
    {
        var parsed = AvatarBriefProfile.ParseBornOn(text);

        Assert.Equal(new DateTime(year, month, day), parsed!.Value.Date);
        Assert.Equal(DateTimeKind.Utc, parsed.Value.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a date")]
    public void Anything_else_is_unknown(string? text)
    {
        Assert.Null(AvatarBriefProfile.ParseBornOn(text));
    }
}
