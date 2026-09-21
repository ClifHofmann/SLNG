using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>
/// Whether a payment's item description is a reason worth showing. Measured on Agni: a gift with
/// no reason arrives as the literal "Payment", so printing it unconditionally would append
/// "— Payment" to every tip and bury the one payment that does carry a reason.
/// </summary>
public class MoneyReasonTests
{
    [Fact]
    public void AGiftWithNoReasonSaysNothing()
    {
        // The reference viewer makes exactly this comparison, against exactly this literal:
        // "Simulator returns 'Payment' if no custom description has been entered"
        // (reason_from_transaction_type, llviewermessage.cpp).
        Assert.Equal("", MoneyReason.For(MoneyTransactionType.Gift, "Payment"));
    }

    [Fact]
    public void AGiftWithARealReasonKeepsIt()
    {
        Assert.Equal("Miete", MoneyReason.For(MoneyTransactionType.Gift, "Miete"));
    }

    [Fact]
    public void TheComparisonIsExact()
    {
        // Only the simulator's own literal counts. Somebody who actually types "payment" as their
        // reason meant to say something, and it is not this client's place to swallow it.
        Assert.Equal("payment", MoneyReason.For(MoneyTransactionType.Gift, "payment"));
        Assert.Equal("Payment for the hair", MoneyReason.For(MoneyTransactionType.Gift, "Payment for the hair"));
    }

    [Fact]
    public void OtherTransactionTypesKeepPaymentAsWritten()
    {
        // The rule is scoped to a gift, as it is in the viewer: another transaction type has no
        // reason to use that placeholder, so a literal "Payment" there is real content.
        Assert.Equal("Payment", MoneyReason.For(5008, "Payment"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void NothingIsNothing(string? description)
    {
        Assert.Equal("", MoneyReason.For(MoneyTransactionType.Gift, description!));
    }
}
