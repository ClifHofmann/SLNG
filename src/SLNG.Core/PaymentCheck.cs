namespace SLNG.Core;

/// <summary>
/// The two questions to answer before any L$ leaves the account: can it be afforded, and is it
/// large enough to be worth asking twice.
/// </summary>
/// <remarks>
/// Here rather than in a window because both pay paths — an object and a person — must answer
/// them identically, and because "how much is too much" is the kind of number that quietly drifts
/// apart when two dialogs each carry their own copy.
/// </remarks>
public static class PaymentCheck
{
    /// <summary>
    /// Above this, a payment is confirmed before it is sent. The reference viewer's
    /// <c>PAY_AMOUNT_NOTIFICATION</c> (llfloaterpay.cpp:125), used there as
    /// <c>if (amount &gt; PAY_AMOUNT_NOTIFICATION)</c> — so exactly 200 still goes straight
    /// through, and the comparison here is the same one.
    /// </summary>
    /// <remarks>
    /// The threshold exists because the alternative is worse in both directions. Confirming every
    /// payment trains people to click through the confirmation, which is how a large one slips
    /// past; confirming none means a typed digit too many is simply gone, and a transfer to
    /// another person cannot be taken back.
    /// </remarks>
    public const int ConfirmAboveAmount = 200;

    /// <summary>SL's own per-transaction ceiling. A typo of one digit too many is otherwise a real
    /// amount of money, and the free-entry field is where that typo happens.</summary>
    public const int MaxAmount = 100000;

    /// <summary>Whether <paramref name="amount"/> is large enough to ask about first.</summary>
    public static bool NeedsConfirmation(int amount) => amount > ConfirmAboveAmount;

    /// <summary>
    /// What the balance falls short by, or 0 when the payment is affordable.
    /// </summary>
    /// <param name="hasBalance">Whether the simulator has actually told us a balance yet. False
    /// means unknown, which is NOT the same as zero — see <c>GridSession.HasBalance</c>. An
    /// unknown balance refuses nothing: blocking a payment because the first MoneyBalanceReply has
    /// not arrived yet would be a guess presented as a fact.</param>
    public static int Shortfall(bool hasBalance, int balance, int amount)
    {
        if (!hasBalance) return 0;
        if (amount <= balance) return 0;
        return amount - balance;
    }

    /// <summary>Whether the payment can go ahead at all: a positive amount, within SL's ceiling,
    /// and covered by whatever balance is known.</summary>
    public static bool CanAfford(bool hasBalance, int balance, int amount) =>
        amount > 0 && amount <= MaxAmount && Shortfall(hasBalance, balance, amount) == 0;
}
