using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>
/// The guards that stand between a typed number and somebody else's account. Both pay paths — an
/// object and a person — go through these, so a difference between the two dialogs can only be a
/// difference in what they show, never in what they allow.
/// </summary>
public class PaymentCheckTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(199, false)]
    [InlineData(200, false)]   // the viewer's test is `> 200`, not `>=`
    [InlineData(201, true)]
    [InlineData(5000, true)]
    public void ConfirmsOnlyAboveTheViewersThreshold(int amount, bool expected)
    {
        Assert.Equal(expected, PaymentCheck.NeedsConfirmation(amount));
    }

    [Fact]
    public void ThresholdIsTheViewersOwnNumber()
    {
        // llfloaterpay.cpp:125 -- PAY_AMOUNT_NOTIFICATION. Pinned so a later "let's make it 500"
        // has to be a deliberate departure from the reference viewer rather than a quiet drift.
        Assert.Equal(200, PaymentCheck.ConfirmAboveAmount);
    }

    [Fact]
    public void AnAffordablePaymentHasNoShortfall()
    {
        Assert.Equal(0, PaymentCheck.Shortfall(hasBalance: true, balance: 1000, amount: 250));
    }

    [Fact]
    public void ExactlyTheWholeBalanceIsAffordable()
    {
        Assert.Equal(0, PaymentCheck.Shortfall(hasBalance: true, balance: 250, amount: 250));
        Assert.True(PaymentCheck.CanAfford(hasBalance: true, balance: 250, amount: 250));
    }

    [Fact]
    public void ShortfallIsWhatIsMissing()
    {
        Assert.Equal(50, PaymentCheck.Shortfall(hasBalance: true, balance: 200, amount: 250));
        Assert.False(PaymentCheck.CanAfford(hasBalance: true, balance: 200, amount: 250));
    }

    [Fact]
    public void AnUnknownBalanceRefusesNothing()
    {
        // "The simulator has not told us yet" is not "you have nothing". Blocking a payment on it
        // would present a guess as a fact -- and the first MoneyBalanceReply can land a second
        // after login.
        Assert.Equal(0, PaymentCheck.Shortfall(hasBalance: false, balance: 0, amount: 9999));
        Assert.True(PaymentCheck.CanAfford(hasBalance: false, balance: 0, amount: 9999));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-500)]
    public void NothingOrLessIsNotAPayment(int amount)
    {
        Assert.False(PaymentCheck.CanAfford(hasBalance: true, balance: 1000, amount));
    }

    [Fact]
    public void RefusesPastSlsPerTransactionCeiling()
    {
        Assert.True(PaymentCheck.CanAfford(hasBalance: false, balance: 0, amount: PaymentCheck.MaxAmount));
        Assert.False(PaymentCheck.CanAfford(hasBalance: false, balance: 0, amount: PaymentCheck.MaxAmount + 1));
    }
}
