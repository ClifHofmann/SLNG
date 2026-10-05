using System.Reflection;
using SLNG.Core;
using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>
/// BUG-UI-28: a presence toast waits for the grid's answer about a friend's Display Name. <c>TryGetDisplayName</c> cannot
/// say "none of their own" from "not asked yet" (both are false), so the toast needs <c>HasDisplayNameAnswer</c>.
/// </summary>
public class DisplayNameAnswerTests
{
    private static DisplayNameCache CacheOf(GridSession session) =>
        (DisplayNameCache)typeof(GridSession).GetField("_displayNameCache", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(session)!;

    [Fact]
    public void BeforeAnyAnswer_ThereIsNone()
    {
        using var session = new GridSession();
        var id = Guid.NewGuid();

        Assert.False(session.HasDisplayNameAnswer(id));
        Assert.False(session.TryGetDisplayName(id, out _));
    }

    [Fact]
    public void AnAnswerWithADisplayName_IsAnAnswer_AndTheNameIsThere()
    {
        using var session = new GridSession();
        var id = Guid.NewGuid();
        CacheOf(session).Set(id, "Zora", DateTime.UtcNow);

        Assert.True(session.HasDisplayNameAnswer(id));
        Assert.True(session.TryGetDisplayName(id, out var name));
        Assert.Equal("Zora", name);
    }

    [Fact]
    public void AnAnswerOfNone_IsAnAnswerToo_ThoughThereIsNoName()
    {
        using var session = new GridSession();
        var id = Guid.NewGuid();
        CacheOf(session).Set(id, null, DateTime.UtcNow); // the resident never set one

        Assert.True(session.HasDisplayNameAnswer(id));
        Assert.False(session.TryGetDisplayName(id, out _));
    }

    [Fact]
    public void AnOldAnswer_StillCountsAsAnAnswer()
    {
        using var session = new GridSession();
        var id = Guid.NewGuid();
        CacheOf(session).Set(id, "Zora", DateTime.UtcNow.AddDays(-400));

        Assert.True(session.HasDisplayNameAnswer(id));
    }

    [Fact]
    public void AnswersAreKeptPerAgent()
    {
        using var session = new GridSession();
        var answered = Guid.NewGuid();
        CacheOf(session).Set(answered, "Zora", DateTime.UtcNow);

        Assert.False(session.HasDisplayNameAnswer(Guid.NewGuid()));
    }
}
