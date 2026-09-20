namespace SLNG.Core;

/// <summary>FEAT-ECON-02: what an object wants to be paid, as its own script defines it.</summary>
/// <remarks>
/// A paid object is not a shop with a price tag: <c>llSetPayPrice</c> lets its script name a
/// default amount and up to four fixed buttons, and the viewer asks for them before offering to
/// pay (RequestPayPrice / PayPriceReply). That is why a vendor in the reference viewer shows
/// exactly the amounts it charges rather than a free field -- and why a free field alone, which
/// is what SLNG first offered, makes the user guess a number the script will reject.
/// </remarks>
public record PayPriceEvent(System.Guid ObjectId, int DefaultPrice, int[] ButtonPrices);

/// <summary>The special values <c>llSetPayPrice</c> can carry, and the viewer's own fallbacks.
/// From the viewer's lllslconstants.h, which is where the numbers are defined.</summary>
public static class PayPrice
{
    /// <summary>"Do not offer this at all" -- for the default price it hides the free field, for
    /// a button it hides that button.</summary>
    public const int Hide = -1;

    /// <summary>"No opinion" -- the viewer's own default applies.</summary>
    public const int Default = -2;

    public const int MaxButtons = 4;

    /// <summary>What the viewer offers before a script says otherwise (fastpay 1/5/10/20).</summary>
    public static readonly int[] DefaultButtons = { 1, 5, 10, 20 };
}
