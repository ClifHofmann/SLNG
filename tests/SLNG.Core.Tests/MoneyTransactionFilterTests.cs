using System;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>
/// Which MoneyBalanceReply messages are worth announcing. Both rules exist because the obvious
/// implementation — "tell the user about every reply" — is visibly wrong in two different ways.
/// </summary>
public class MoneyTransactionFilterTests
{
    private static readonly Guid Alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bob = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void AnnouncesARealTransaction()
    {
        var filter = new MoneyTransactionFilter();

        Assert.True(filter.ShouldAnnounce(Guid.NewGuid(), Alice, Bob));
    }

    [Fact]
    public void APureBalanceUpdateIsNotATransaction()
    {
        // The answer to MoneyBalanceRequest looks identical apart from having nobody at either
        // end. Announcing it would put "you were paid L$ 0" on screen at every login, because the
        // session asks for its balance as soon as it is in-world.
        var filter = new MoneyTransactionFilter();

        Assert.False(filter.ShouldAnnounce(Guid.NewGuid(), Guid.Empty, Guid.Empty));
    }

    [Fact]
    public void AGridThatFillsNoPartiesButSaysSomethingIsStillAnnounced()
    {
        // The reference viewer has a branch for exactly this -- it prints the reply's own
        // sentence verbatim (llviewermessage.cpp:4558) -- and without it such a grid would be
        // silent about money. Not Second Life, which fills the block properly (measured on Agni);
        // this is the safety net for one that does not.
        var filter = new MoneyTransactionFilter();

        Assert.True(filter.ShouldAnnounce(Guid.NewGuid(), Guid.Empty, Guid.Empty, hasDescription: true));
    }

    [Fact]
    public void OnlyOneEndIsEnoughToBeATransaction()
    {
        // A fee to the system has a payer and no payee -- still something the user paid.
        var filter = new MoneyTransactionFilter();

        Assert.True(filter.ShouldAnnounce(Guid.NewGuid(), Alice, Guid.Empty));
        Assert.True(filter.ShouldAnnounce(Guid.NewGuid(), Guid.Empty, Bob));
    }

    [Fact]
    public void TheSameTransactionIsAnnouncedOnce()
    {
        var filter = new MoneyTransactionFilter();
        var id = Guid.NewGuid();

        Assert.True(filter.ShouldAnnounce(id, Alice, Bob));
        Assert.False(filter.ShouldAnnounce(id, Alice, Bob));
        Assert.False(filter.ShouldAnnounce(id, Alice, Bob));
    }

    [Fact]
    public void DifferentTransactionsAreEachAnnounced()
    {
        var filter = new MoneyTransactionFilter();

        Assert.True(filter.ShouldAnnounce(Guid.NewGuid(), Alice, Bob));
        Assert.True(filter.ShouldAnnounce(Guid.NewGuid(), Alice, Bob));
    }

    [Fact]
    public void AnUnidentifiedTransactionIsAnnouncedRatherThanSwallowed()
    {
        // A zero id cannot be deduplicated. Repeating a payment is a smaller fault than silently
        // dropping one, so it is announced and simply not remembered.
        var filter = new MoneyTransactionFilter();

        Assert.True(filter.ShouldAnnounce(Guid.Empty, Alice, Bob));
        Assert.True(filter.ShouldAnnounce(Guid.Empty, Alice, Bob));
    }

    [Fact]
    public void TheLookbackDoesNotGrowWithoutBound()
    {
        // The list is trimmed, so a long session cannot accumulate one entry per transaction
        // forever. The cost of trimming is that a very old id could be announced again -- which
        // is the trade the reference viewer makes too.
        var filter = new MoneyTransactionFilter();

        for (int i = 0; i < 500; i++)
        {
            Assert.True(filter.ShouldAnnounce(Guid.NewGuid(), Alice, Bob));
        }

        // And the most recent one is still remembered after all that trimming.
        var latest = Guid.NewGuid();
        Assert.True(filter.ShouldAnnounce(latest, Alice, Bob));
        Assert.False(filter.ShouldAnnounce(latest, Alice, Bob));
    }
}
