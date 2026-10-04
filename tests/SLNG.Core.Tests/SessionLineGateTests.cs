using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

public sealed class SessionLineGateTests
{
    private static readonly Guid S = Guid.NewGuid();
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();

    [Fact]
    public void The_same_line_twice_in_a_moment_is_one_line()
    {
        var g = new SessionLineGate();
        Assert.False(g.IsDuplicate(S, A, ":D", 0));
        Assert.True(g.IsDuplicate(S, A, ":D", TimeSpan.FromMilliseconds(200).Ticks));
    }

    [Fact]
    public void The_same_line_later_is_a_new_line()
    {
        var g = new SessionLineGate();
        g.IsDuplicate(S, A, ":D", 0);
        Assert.False(g.IsDuplicate(S, A, ":D", SessionLineGate.Window.Ticks + 1));
    }

    [Fact]
    public void Another_sender_or_another_text_is_not_a_duplicate()
    {
        var g = new SessionLineGate();
        g.IsDuplicate(S, A, ":D", 0);
        Assert.False(g.IsDuplicate(S, B, ":D", 10));
        Assert.False(g.IsDuplicate(S, A, ":)", 20));
        Assert.False(g.IsDuplicate(Guid.NewGuid(), A, ":)", 30));
    }
}
