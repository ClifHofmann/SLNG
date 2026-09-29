using System;
using System.Linq;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

public class DisplayNameCacheTests
{
    private static readonly DateTime Now = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void UnknownAgent_IsAMiss()
    {
        var cache = new DisplayNameCache();

        Assert.Equal(DisplayNameCache.Freshness.Miss, cache.Lookup(Guid.NewGuid(), Now, out var name));
        Assert.Equal(string.Empty, name);
    }

    [Fact]
    public void RecentAnswer_IsFresh()
    {
        var cache = new DisplayNameCache();
        var id = Guid.NewGuid();
        cache.Set(id, "CrazyShiva", Now);

        var state = cache.Lookup(id, Now + TimeSpan.FromHours(23), out var name);

        Assert.Equal(DisplayNameCache.Freshness.Fresh, state);
        Assert.Equal("CrazyShiva", name);
    }

    [Fact]
    public void OldAnswer_IsStale_AndStillCarriesTheName()
    {
        // Stale means "show it, and ask again" -- so the name must still come back.
        var cache = new DisplayNameCache();
        var id = Guid.NewGuid();
        cache.Set(id, "CrazyShiva", Now);

        var state = cache.Lookup(id, Now + TimeSpan.FromHours(25), out var name);

        Assert.Equal(DisplayNameCache.Freshness.Stale, state);
        Assert.Equal("CrazyShiva", name);
    }

    [Fact]
    public void NoDisplayName_IsRemembered_AsAnEmptyName()
    {
        // Most residents never set one; not asking again for them is most of the saving.
        var cache = new DisplayNameCache();
        var id = Guid.NewGuid();
        cache.Set(id, null, Now);

        var state = cache.Lookup(id, Now, out var name);

        Assert.Equal(DisplayNameCache.Freshness.Fresh, state);
        Assert.Equal(string.Empty, name);
    }

    [Fact]
    public void NewerAnswer_ReplacesTheOlder()
    {
        var cache = new DisplayNameCache();
        var id = Guid.NewGuid();
        cache.Set(id, "Old", Now);
        cache.Set(id, "New", Now + TimeSpan.FromMinutes(5));

        cache.Lookup(id, Now + TimeSpan.FromMinutes(5), out var name);

        Assert.Equal("New", name);
    }

    [Fact]
    public void EmptyAgentId_IsNeverStored()
    {
        var cache = new DisplayNameCache();
        cache.Set(Guid.Empty, "Nobody", Now);

        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void RoundTrip_KeepsNamesAndTimes()
    {
        var cache = new DisplayNameCache();
        var named = Guid.NewGuid();
        var plain = Guid.NewGuid();
        cache.Set(named, "CrazyShiva", Now);
        cache.Set(plain, null, Now);

        var back = DisplayNameCache.FromJson(cache.ToJson());

        Assert.Equal(2, back.Count);
        Assert.Equal(DisplayNameCache.Freshness.Fresh, back.Lookup(named, Now, out var name));
        Assert.Equal("CrazyShiva", name);
        Assert.Equal(DisplayNameCache.Freshness.Fresh, back.Lookup(plain, Now, out var none));
        Assert.Equal(string.Empty, none);
        // The time survives too: 25 hours later it is stale, not fresh again.
        Assert.Equal(DisplayNameCache.Freshness.Stale, back.Lookup(named, Now + TimeSpan.FromHours(25), out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"Version\":1,\"Entries\":[{\"Id\":\"cut off")]        // truncated by a crash
    [InlineData("{\"Version\":99,\"Entries\":[]}")]                        // a newer format
    [InlineData("{\"Version\":1,\"Entries\":null}")]
    [InlineData("[]")]
    public void DamagedFile_GivesAnEmptyCache_NotAnException(string? json)
    {
        var cache = DisplayNameCache.FromJson(json);

        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Saving_KeepsTheNewestEntries_WhenOverTheCap()
    {
        var cache = new DisplayNameCache();
        var oldest = Guid.NewGuid();
        cache.Set(oldest, "Oldest", Now);
        for (int i = 1; i <= DisplayNameCache.MaxEntries; i++)
            cache.Set(Guid.NewGuid(), "n" + i, Now + TimeSpan.FromSeconds(i));

        var back = DisplayNameCache.FromJson(cache.ToJson());

        Assert.Equal(DisplayNameCache.MaxEntries, back.Count);
        Assert.Equal(DisplayNameCache.Freshness.Miss, back.Lookup(oldest, Now, out _));
    }

    [Fact]
    public void Absorb_KeepsTheNewerAnswerPerAgent()
    {
        var id = Guid.NewGuid();
        var fromDisk = new DisplayNameCache();
        fromDisk.Set(id, "OnDisk", Now);

        var live = new DisplayNameCache();
        live.Set(id, "Live", Now + TimeSpan.FromMinutes(10)); // learned this session, newer than the file

        live.Absorb(fromDisk);
        live.Lookup(id, Now + TimeSpan.FromMinutes(10), out var kept);
        Assert.Equal("Live", kept);

        // And the other way round: the file has the newer answer.
        var live2 = new DisplayNameCache();
        live2.Set(id, "Older", Now - TimeSpan.FromHours(1));
        live2.Absorb(fromDisk);
        live2.Lookup(id, Now, out var taken);
        Assert.Equal("OnDisk", taken);
    }

    [Fact]
    public void NamedEntries_ListsOnlyRealDisplayNames()
    {
        var cache = new DisplayNameCache();
        var named = Guid.NewGuid();
        cache.Set(named, "CrazyShiva", Now);
        cache.Set(Guid.NewGuid(), null, Now);

        var list = cache.NamedEntries();

        Assert.Single(list);
        Assert.Equal((named, "CrazyShiva"), list.Single());
    }
}
