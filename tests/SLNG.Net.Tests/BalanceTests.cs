using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>FEAT-ECON-01. The balance is one number, so what is worth pinning is not arithmetic
/// but the three decisions around it: that "not told yet" is distinguishable from "zero", that the
/// event and the property can never disagree, and that an unchanged figure is not an event.</summary>
public class BalanceTests
{
    [Fact]
    public void AFreshSessionDoesNotClaimToKnowTheBalance()
    {
        using var session = new GridSession();

        Assert.False(session.HasBalance);
        Assert.Equal(0, session.Balance);
    }

    /// <summary>Zero is a real balance, and the readout has to be able to show it. Only the flag
    /// separates "you have nothing" from "the simulator has not answered yet" -- showing L$ 0 to
    /// someone with thousands is not a blank readout, it is a wrong one.</summary>
    [Fact]
    public void ZeroIsAnAnswerLikeAnyOther()
    {
        using var session = new GridSession();
        int raised = -1;
        session.BalanceChanged += (_, balance) => raised = balance;

        session.SetBalance(0);

        Assert.True(session.HasBalance);
        Assert.Equal(0, session.Balance);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void AChangeUpdatesThePropertyBeforeItIsAnnounced()
    {
        using var session = new GridSession();
        int seenByHandler = -1;
        session.BalanceChanged += (s, _) => seenByHandler = ((GridSession)s!).Balance;

        session.SetBalance(2500);

        Assert.Equal(2500, session.Balance);
        Assert.Equal(2500, seenByHandler);
    }

    /// <summary>The simulator re-sends the balance on every region entry, so an unchanged figure
    /// arrives regularly. It must not ripple through the UI each time.</summary>
    [Fact]
    public void TheSameFigureTwiceIsAnnouncedOnce()
    {
        using var session = new GridSession();
        int announcements = 0;
        session.BalanceChanged += (_, _) => announcements++;

        session.SetBalance(1000);
        session.SetBalance(1000);
        session.SetBalance(1000);

        Assert.Equal(1, announcements);
        Assert.Equal(1000, session.Balance);
    }

    [Fact]
    public void SpendingAndEarningBothAnnounce()
    {
        using var session = new GridSession();
        var seen = new System.Collections.Generic.List<int>();
        session.BalanceChanged += (_, balance) => seen.Add(balance);

        session.SetBalance(1000);
        session.SetBalance(750);   // spent
        session.SetBalance(1750);  // paid

        Assert.Equal(new[] { 1000, 750, 1750 }, seen);
    }

    /// <summary>Asking while disconnected is a no-op, not a throw: the readout is wired up before
    /// login and the region-entry path calls this on every arrival.</summary>
    [Fact]
    public void RequestingWhileDisconnectedDoesNotThrow()
    {
        using var session = new GridSession();

        var exception = Record.Exception(() => session.RequestBalance());

        Assert.Null(exception);
    }
}
