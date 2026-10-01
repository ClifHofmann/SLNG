using System;
using System.Net;
using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

// BUG-ASSET-02: on a busy region ONE animation was asked for 124 times in a few seconds -- once per
// avatar that played it -- and refused (HTTP 403) every time, twice over (our own request, then
// LibreMetaverse's), each refusal a warning on the console. A refusal has to be remembered.
public class AssetRefusalsTests
{
    private static readonly Guid Anim = Guid.Parse("fd037134-85d4-f241-72c6-4f42164fedee");
    private const ulong Region = 741070837455616;

    private sealed class Clock
    {
        public DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        public DateTime Read() => Now;
    }

    private static (AssetRefusals Refusals, Clock Clock) Make(TimeSpan? lifetime = null)
    {
        var clock = new Clock();
        return (new AssetRefusals(lifetime ?? TimeSpan.FromMinutes(10), clock.Read), clock);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    public void Forbidden_not_found_and_gone_are_refusals(HttpStatusCode status)
    {
        Assert.True(AssetRefusals.IsRefusal(status));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public void A_timeout_a_rate_limit_or_a_server_error_is_not_a_decision(HttpStatusCode status)
    {
        Assert.False(AssetRefusals.IsRefusal(status));
    }

    [Fact]
    public void Nothing_is_refused_until_a_refusal_is_remembered()
    {
        var (refusals, _) = Make();

        Assert.False(refusals.IsRefused(Region, Anim));
    }

    [Fact]
    public void A_remembered_refusal_holds_for_its_lifetime_and_then_ends()
    {
        var (refusals, clock) = Make(TimeSpan.FromMinutes(10));
        refusals.Remember(Region, Anim);

        clock.Now += TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(59);
        Assert.True(refusals.IsRefused(Region, Anim));

        clock.Now += TimeSpan.FromSeconds(2);
        Assert.False(refusals.IsRefused(Region, Anim));
    }

    [Fact]
    public void A_refusal_is_news_once_and_not_again_while_it_holds()
    {
        var (refusals, clock) = Make(TimeSpan.FromMinutes(10));

        Assert.True(refusals.Remember(Region, Anim));    // the first time: log it
        Assert.False(refusals.Remember(Region, Anim));   // the crowd after it: do not

        clock.Now += TimeSpan.FromMinutes(11);
        Assert.True(refusals.Remember(Region, Anim));    // refused again after it lapsed: news again
    }

    [Fact]
    public void Another_region_may_answer_differently()
    {
        var (refusals, _) = Make();
        refusals.Remember(Region, Anim);

        Assert.False(refusals.IsRefused(Region + 1, Anim));
        Assert.True(refusals.IsRefused(Region, Anim));
    }

    [Fact]
    public void Another_asset_is_not_refused_by_it()
    {
        var (refusals, _) = Make();
        refusals.Remember(Region, Anim);

        Assert.False(refusals.IsRefused(Region, Guid.NewGuid()));
    }

    [Fact]
    public void Renewing_a_refusal_extends_it()
    {
        var (refusals, clock) = Make(TimeSpan.FromMinutes(10));
        refusals.Remember(Region, Anim);
        clock.Now += TimeSpan.FromMinutes(8);
        refusals.Remember(Region, Anim);

        clock.Now += TimeSpan.FromMinutes(8);   // 16 minutes in: the first would have lapsed, the renewal has not
        Assert.True(refusals.IsRefused(Region, Anim));
    }

    [Fact]
    public void The_table_does_not_grow_for_good_when_a_region_refuses_everything()
    {
        var (refusals, clock) = Make(TimeSpan.FromMinutes(1));
        for (int i = 0; i < 2048; i++) refusals.Remember(Region, Guid.NewGuid());

        clock.Now += TimeSpan.FromMinutes(2);          // all of them have lapsed
        refusals.Remember(Region, Guid.NewGuid());     // the next one sweeps the lapsed ones out

        Assert.True(refusals.Count < 10, $"the lapsed entries were swept (held {refusals.Count})");
    }

    [Fact]
    public void The_default_lifetime_is_ten_minutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), AssetRefusals.DefaultLifetime);
        Assert.Equal(AssetRefusals.DefaultLifetime, new AssetRefusals().Lifetime);
    }
}
