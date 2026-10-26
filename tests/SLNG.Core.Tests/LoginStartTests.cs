using SLNG.Core.Login;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>
/// FEAT-UI-72: the typed-region parser, the choice-to-login-string decision and the viewer's rules for judging
/// whether the grid honoured the request. Every row of the spec's table is here, because the string built by
/// <see cref="StartTarget.ToLoginString"/> is what the grid's login service receives.
/// </summary>
public class LoginStartTests
{
    private const string Invalid = "invalid";

    private static string Parse(string? text, int maxXY = 256) =>
        LoginStart.TryParseTyped(text, maxXY, out var t) ? t.ToLoginString() : Invalid;

    [Theory]
    [InlineData("Ahern", "uri:Ahern&128&128&0")]
    [InlineData("Ahern/10/20", "uri:Ahern&10&20&0")]
    [InlineData("Ahern/10/20/30", "uri:Ahern&10&20&30")]
    [InlineData("Ahern Park/10/20/30", "uri:Ahern Park&10&20&30")]
    [InlineData("Ahern%20Park/10/20/30", "uri:Ahern Park&10&20&30")]
    [InlineData("/Ahern/1/2/3", "uri:Ahern&1&2&3")]
    [InlineData("  Ahern/1/2/3  ", "uri:Ahern&1&2&3")]
    [InlineData("secondlife://Ahern%20Park/10/20/30/", "uri:Ahern Park&10&20&30")]
    [InlineData("SecondLife://Ahern/1/2/3", "uri:Ahern&1&2&3")]
    [InlineData("https://maps.secondlife.com/secondlife/Ahern%20Park/10/20/30", "uri:Ahern Park&10&20&30")]
    [InlineData("http://example.org/region/Ahern/5/6", "uri:Ahern&5&6&0")]
    [InlineData("uri:Ahern&1&2&3", "uri:Ahern&1&2&3")]
    [InlineData("uri:Ahern Park&10&20&30", "uri:Ahern Park&10&20&30")]
    [InlineData("Ahern/10.6/20.4/30.5", "uri:Ahern&11&20&31")]
    [InlineData("Ahern/256/256/4096", "uri:Ahern&256&256&4096")]
    [InlineData("Ahern/0/0/0", "uri:Ahern&0&0&0")]
    [InlineData("Ahern\u0007Park/1/2/3", "uri:AhernPark&1&2&3")]
    public void Typed_text_becomes_the_canonical_login_string(string input, string expected) =>
        Assert.Equal(expected, Parse(input));

    [Theory]
    [InlineData("Ahern/10")]                 // one lone coordinate
    [InlineData("Ahern/-1/0/0")]             // negative
    [InlineData("Ahern/abc/1/2")]            // not a number
    [InlineData("Ahern/1/2/3/4")]            // more than four parts
    [InlineData("A&B/1/2/3")]                // '&' cannot be expressed in the uri: form
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ahern/NaN/1/2")]
    [InlineData("Ahern/Infinity/1/2")]
    [InlineData("Ahern/1e2/1/2")]            // no exponents
    [InlineData("Ahern//1/2")]               // empty coordinate
    [InlineData("Ahern/257/0/0")]            // out of range at 256
    [InlineData("Ahern/0/0/4097")]
    [InlineData("/")]
    [InlineData("http://example.org/somewhere/else")]
    [InlineData("uri:Ahern&1&2")]            // uri: needs all four parts
    [InlineData("uri:Ahern&a&2&3")]
    [InlineData("uri:&1&2&3")]
    public void Text_that_is_not_a_location_is_invalid(string input) =>
        Assert.Equal(Invalid, Parse(input));

    [Fact]
    public void Null_is_invalid() => Assert.Equal(Invalid, Parse(null));

    [Fact]
    public void The_coordinate_limit_is_the_callers_choice()
    {
        Assert.Equal(Invalid, Parse("Ahern/257/0/0", maxXY: 256));
        Assert.Equal("uri:Ahern&257&0&0", Parse("Ahern/257/0/0", maxXY: 4096));
        Assert.Equal(Invalid, Parse("Ahern/4097/0/0", maxXY: 4096));
    }

    [Fact]
    public void A_region_name_of_255_characters_is_accepted_and_256_is_not()
    {
        Assert.NotEqual(Invalid, Parse(new string('a', 255) + "/1/2/3"));
        Assert.Equal(Invalid, Parse(new string('a', 256) + "/1/2/3"));
    }

    [Fact]
    public void The_login_string_has_a_raw_ampersand_and_integers()
    {
        Assert.True(StartTarget.TryCreate("Ahern Park", 10.2, 20.7, 30, 256, out var t));
        // Raw '&', never '&amp;': LibreMetaverse's serializer escapes it on the wire.
        Assert.Equal("uri:Ahern Park&10&21&30", t!.ToLoginString());
        Assert.DoesNotContain("&amp;", t.ToLoginString());
    }

    [Theory]
    [InlineData(-0.4, 0)]   // rounds to zero, which is in range
    [InlineData(0.5, 1)]    // half away from zero, the viewer's ll_round
    [InlineData(1.5, 2)]
    [InlineData(255.5, 256)]
    public void Coordinates_round_half_away_from_zero(double input, int expected)
    {
        Assert.True(StartTarget.TryCreate("R", input, input, 0, 256, out var t));
        Assert.Equal(expected, t!.X);
    }

    [Fact]
    public void A_target_is_refused_for_a_region_the_login_form_cannot_express()
    {
        Assert.False(StartTarget.TryCreate(null, 1, 1, 1, 256, out _));
        Assert.False(StartTarget.TryCreate("  ", 1, 1, 1, 256, out _));
        Assert.False(StartTarget.TryCreate("\u0001\u0002", 1, 1, 1, 256, out _));
        Assert.False(StartTarget.TryCreate("A&B", 1, 1, 1, 256, out _));
        Assert.False(StartTarget.TryCreate("R", double.NaN, 1, 1, 256, out _));
        Assert.False(StartTarget.TryCreate("R", 1, double.PositiveInfinity, 1, 256, out _));
    }

    // ---- Resolve ----

    private static LoginFavorite Fav(string name, string region, int x, int y, int z, Guid? asset = null)
    {
        Assert.True(StartTarget.TryCreate(region, x, y, z, 4096, out var t));
        return new LoginFavorite(asset ?? Guid.NewGuid(), Guid.NewGuid(), name, t!);
    }

    [Fact]
    public void Last_and_home_pass_through()
    {
        var last = LoginStart.Resolve(StartChoice.Last, Array.Empty<LoginFavorite>(), 256);
        Assert.Equal("last", last.LoginString);
        Assert.Equal(StartFallback.None, last.Fallback);
        Assert.Null(last.Target);

        var home = LoginStart.Resolve(StartChoice.Home, Array.Empty<LoginFavorite>(), 256);
        Assert.Equal("home", home.LoginString);
        Assert.Equal(StartKind.Home, home.Effective.Kind);
    }

    [Fact]
    public void A_favorite_in_the_list_becomes_its_uri()
    {
        var a = Fav("Puris Town", "Ahern", 128, 64, 30);
        var b = Fav("Sunset Beach", "Sunset", 10, 20, 5);

        var r = LoginStart.Resolve(StartChoice.Favorite(b.AssetId), new[] { a, b }, 256);

        Assert.Equal("uri:Sunset&10&20&5", r.LoginString);
        Assert.Equal(StartFallback.None, r.Fallback);
        Assert.Equal(b.Target, r.Target);
        Assert.Equal(StartChoice.Favorite(b.AssetId), r.Effective);
    }

    [Fact]
    public void A_favorite_that_is_gone_falls_back_to_last()
    {
        var a = Fav("Puris Town", "Ahern", 128, 64, 30);

        var r = LoginStart.Resolve(StartChoice.Favorite(Guid.NewGuid()), new[] { a }, 256);

        Assert.Equal("last", r.LoginString);
        Assert.Equal(StartFallback.FavoriteMissing, r.Fallback);
        Assert.Equal(StartChoice.Last, r.Effective);
        Assert.Null(r.Target);
    }

    [Fact]
    public void A_favorites_position_is_not_held_to_the_typed_text_limit()
    {
        // A landmark on a var-region can lie beyond 256 m; the limit is for typed text on a Linden grid.
        var far = Fav("Far", "BigRegion", 1000, 2000, 30);
        var r = LoginStart.Resolve(StartChoice.Favorite(far.AssetId), new[] { far }, 256);
        Assert.Equal("uri:BigRegion&1000&2000&30", r.LoginString);
    }

    [Fact]
    public void A_typed_region_is_parsed_or_falls_back()
    {
        var ok = LoginStart.Resolve(StartChoice.Region("Ahern/1/2/3"), Array.Empty<LoginFavorite>(), 256);
        Assert.Equal("uri:Ahern&1&2&3", ok.LoginString);
        Assert.Equal(StartFallback.None, ok.Fallback);
        Assert.Equal(StartKind.Region, ok.Effective.Kind);

        var bad = LoginStart.Resolve(StartChoice.Region("nope/x"), Array.Empty<LoginFavorite>(), 256);
        Assert.Equal("last", bad.LoginString);
        Assert.Equal(StartFallback.RegionInvalid, bad.Fallback);
        Assert.Equal(StartChoice.Last, bad.Effective);
    }

    // ---- the viewer's table: did the grid honour it? ----

    [Theory]
    [InlineData(StartKind.Favorite, "url", true)]
    [InlineData(StartKind.Region, "url", true)]
    [InlineData(StartKind.Favorite, "home", false)]
    [InlineData(StartKind.Region, "last", false)]
    [InlineData(StartKind.Region, "safe", false)]
    [InlineData(StartKind.Last, "last", true)]
    [InlineData(StartKind.Last, "url", false)]
    [InlineData(StartKind.Last, "safe", false)]
    [InlineData(StartKind.Home, "home", true)]
    [InlineData(StartKind.Home, "last", false)]
    [InlineData(StartKind.Home, " HOME ", true)]
    public void HonoursStart_follows_the_viewers_table(StartKind asked, string reply, bool expected) =>
        Assert.Equal(expected, LoginStart.HonoursStart(asked, reply));

    [Theory]
    [InlineData(StartKind.Last)]
    [InlineData(StartKind.Home)]
    [InlineData(StartKind.Favorite)]
    [InlineData(StartKind.Region)]
    public void An_empty_reply_is_unknown_not_a_refusal(StartKind asked)
    {
        Assert.Null(LoginStart.HonoursStart(asked, null));
        Assert.Null(LoginStart.HonoursStart(asked, ""));
        Assert.Null(LoginStart.HonoursStart(asked, "  "));
    }

    [Fact]
    public void NeedsMove_uses_the_two_metre_rule_on_x_and_y_only()
    {
        Assert.True(StartTarget.TryCreate("Ahern", 100, 100, 30, 256, out var t));

        Assert.False(LoginStart.NeedsMove(t!, "Ahern", 100, 100));
        Assert.False(LoginStart.NeedsMove(t!, "ahern", 101.5, 98.5));    // same region (any case), within 2 m
        Assert.False(LoginStart.NeedsMove(t!, "Ahern", 102.0, 100));     // exactly 2 m is still within
        Assert.True(LoginStart.NeedsMove(t!, "Ahern", 102.1, 100));      // dx = 2.1
        Assert.True(LoginStart.NeedsMove(t!, "Ahern", 100, 97.5));       // dy = 2.5
        Assert.True(LoginStart.NeedsMove(t!, "Elsewhere", 100, 100));    // different region
        Assert.True(LoginStart.NeedsMove(t!, null, 100, 100));
        // Z is not looked at at all: NeedsMove has no Z parameter, which is the rule (the grid may adjust height).
    }

    // ---- JudgeLanding: when does the grid owe the person a notice? ----

    private static StartTarget Place(string region = "Ahern", int x = 100, int y = 100, int z = 30)
    {
        Assert.True(StartTarget.TryCreate(region, x, y, z, 256, out var t));
        return t!;
    }

    [Fact]
    public void A_first_login_is_never_reported()
    {
        Assert.Equal(LandingVerdict.Fine, LoginStart.JudgeLanding(StartKind.Home, null, "safe", true, "Elsewhere", 1, 1));
        Assert.Equal(LandingVerdict.Fine, LoginStart.JudgeLanding(StartKind.Region, Place(), "safe", true, "Elsewhere", 1, 1));
    }

    [Theory]
    [InlineData(StartKind.Last, "last", LandingVerdict.Fine)]
    [InlineData(StartKind.Last, "safe", LandingVerdict.MovedFromLast)]
    [InlineData(StartKind.Last, "home", LandingVerdict.MovedFromLast)]
    [InlineData(StartKind.Last, null, LandingVerdict.Fine)]            // unknown is not a refusal
    [InlineData(StartKind.Home, "home", LandingVerdict.Fine)]
    [InlineData(StartKind.Home, "last", LandingVerdict.MovedFromHome)]
    [InlineData(StartKind.Home, "", LandingVerdict.Fine)]
    public void Last_and_home_are_judged_by_the_reply_alone(StartKind asked, string? reply, LandingVerdict expected) =>
        Assert.Equal(expected, LoginStart.JudgeLanding(asked, null, reply, false, "Anywhere", 5, 5));

    [Theory]
    [InlineData(StartKind.Favorite)]
    [InlineData(StartKind.Region)]
    public void A_place_the_grid_says_it_honoured_is_fine_wherever_the_agent_stands(StartKind asked) =>
        Assert.Equal(LandingVerdict.Fine, LoginStart.JudgeLanding(asked, Place(), "url", false, "Elsewhere", 1, 1));

    [Fact]
    public void A_refused_place_in_another_region_is_reported()
    {
        Assert.Equal(LandingVerdict.MovedFromPlace,
            LoginStart.JudgeLanding(StartKind.Region, Place(), "safe", false, "Elsewhere", 100, 100));
        Assert.Equal(LandingVerdict.MovedFromPlace,
            LoginStart.JudgeLanding(StartKind.Favorite, Place(), "last", false, "Elsewhere", 100, 100));
    }

    [Fact]
    public void A_refused_place_in_the_same_region_is_reported_only_beyond_two_metres()
    {
        var p = Place("Ahern", 100, 100);

        Assert.Equal(LandingVerdict.Fine, LoginStart.JudgeLanding(StartKind.Region, p, "last", false, "ahern", 101, 99));
        Assert.Equal(LandingVerdict.MovedFromPlace, LoginStart.JudgeLanding(StartKind.Region, p, "last", false, "Ahern", 103, 100));
        // Same region but the position is not known yet: nothing can be said.
        Assert.Equal(LandingVerdict.Fine, LoginStart.JudgeLanding(StartKind.Region, p, "last", false, "Ahern", null, null));
    }

    [Fact]
    public void Without_a_reply_a_place_is_judged_by_the_region_name_only()
    {
        var p = Place("Ahern", 100, 100);

        Assert.Equal(LandingVerdict.MovedFromPlace, LoginStart.JudgeLanding(StartKind.Region, p, null, false, "Default Region", 100, 100));
        Assert.Equal(LandingVerdict.Fine, LoginStart.JudgeLanding(StartKind.Region, p, "", false, "Ahern", 5, 5));   // same region, far away: not judged
        Assert.Equal(LandingVerdict.Fine, LoginStart.JudgeLanding(StartKind.Region, p, null, false, "AHERN", null, null));
    }

    [Fact]
    public void A_place_without_a_target_is_never_reported()
    {
        Assert.Equal(LandingVerdict.Fine, LoginStart.JudgeLanding(StartKind.Region, null, "safe", false, "Elsewhere", 1, 1));
    }
}
