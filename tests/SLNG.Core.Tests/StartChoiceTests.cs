using SLNG.Core.Login;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>FEAT-UI-72: the remembered "Start at" choice and its one-line text form in logins.cfg.</summary>
public class StartChoiceTests
{
    [Fact]
    public void Every_kind_round_trips()
    {
        var id = Guid.NewGuid();
        var all = new[]
        {
            StartChoice.Last,
            StartChoice.Home,
            StartChoice.Favorite(id),
            StartChoice.Region("Ahern/1/2/3"),
            StartChoice.Region("Ahern Park"),
        };

        foreach (var choice in all)
            Assert.Equal(choice, StartChoice.Parse(choice.Serialize()));
    }

    [Fact]
    public void The_text_form_is_the_documented_one()
    {
        var id = Guid.Parse("0123456789abcdef0123456789abcdef");
        Assert.Equal("last", StartChoice.Last.Serialize());
        Assert.Equal("home", StartChoice.Home.Serialize());
        Assert.Equal("fav:0123456789abcdef0123456789abcdef", StartChoice.Favorite(id).Serialize());
        Assert.Equal("region:Ahern/1/2/3", StartChoice.Region("Ahern/1/2/3").Serialize());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("fav:not-a-guid")]
    [InlineData("fav:")]
    [InlineData("fav:00000000000000000000000000000000")]
    [InlineData("region:")]
    [InlineData("region:   ")]
    [InlineData("LAST ")]
    public void Anything_unreadable_is_last(string? text) =>
        Assert.Equal(StartChoice.Last, StartChoice.Parse(text));

    [Fact]
    public void A_region_containing_a_colon_survives()
    {
        var c = StartChoice.Parse("region:host:8002:Some Region/1/2/3");
        Assert.Equal(StartKind.Region, c.Kind);
        Assert.Equal("host:8002:Some Region/1/2/3", c.Key);
    }

    [Fact]
    public void An_absurdly_long_value_is_last()
    {
        Assert.Equal(StartChoice.Last, StartChoice.Parse("region:" + new string('a', 1_000_000)));
        Assert.Equal(StartChoice.Last, StartChoice.Parse("region:" + new string('a', StartChoice.MaxSerializedLength)));
    }

    [Fact]
    public void A_favorite_is_keyed_by_its_asset_in_N_format_and_reads_back_in_any_guid_format()
    {
        var id = Guid.NewGuid();
        var viaN = StartChoice.Parse("fav:" + id.ToString("N"));
        var viaDashes = StartChoice.Parse("fav:" + id);

        Assert.Equal(StartChoice.Favorite(id), viaN);
        Assert.Equal(viaN, viaDashes);
        Assert.Equal(id, viaN.FavoriteAssetId);
        Assert.Equal(Guid.Empty, StartChoice.Home.FavoriteAssetId);
    }
}
