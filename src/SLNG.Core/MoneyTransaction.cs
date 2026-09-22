using System;
using System.Collections.Generic;

namespace SLNG.Core;

/// <summary>Which way the money went, from this agent's point of view.</summary>
public enum MoneyDirection
{
    /// <summary>Somebody paid us.</summary>
    Received,

    /// <summary>We paid somebody.</summary>
    Paid,

    /// <summary>
    /// Money moved and the simulator described it only in words.
    /// </summary>
    /// <remarks>
    /// Not a defensive default: a grid that fills no TransactionInfo block still fills the
    /// reply's plain Description, and the reference viewer prints exactly that as a system
    /// message rather than dropping it ("Only old dev grids will not supply the TransactionInfo
    /// block, so we can just use the hard-coded English string" — llviewermessage.cpp:4558).
    ///
    /// <para>NOT the case on Second Life, contrary to what an earlier version of this comment
    /// claimed: measured on Agni, the block arrives complete — source, dest, amount, type 5001,
    /// item 'Payment'. So this is the fallback it was meant to be and not the ordinary path; a
    /// payment there takes <see cref="Received"/> or <see cref="Paid"/> and keeps the clickable
    /// name that comes with knowing who the other party was.</para>
    /// </remarks>
    Unknown,
}

/// <summary>
/// One completed money movement, as the simulator reported it.
/// </summary>
/// <param name="OtherPartyId">Whoever is at the other end — the payer when
/// <see cref="MoneyDirection.Received"/>, the payee when <see cref="MoneyDirection.Paid"/>.</param>
/// <param name="Description">What the transaction was for, in the simulator's own words. Often
/// empty: a plain gift between two people carries no reason unless the sender supplied one.</param>
/// <param name="TransactionType">The simulator's transaction-type code (lltransactiontypes.h),
/// passed through rather than interpreted — it is what tells a gift from an object sale from a
/// group fee.</param>
public readonly record struct MoneyTransactionEvent(
    Guid TransactionId,
    MoneyDirection Direction,
    Guid OtherPartyId,
    bool OtherPartyIsGroup,
    int Amount,
    string Description,
    int TransactionType,
    bool Success);

/// <summary>SL transaction types this client distinguishes (lltransactiontypes.h).</summary>
public static class MoneyTransactionType
{
    /// <summary>One person handing another money, with no object involved.</summary>
    public const int Gift = 5001;
}

/// <summary>Turns a transaction's own fields into the "for ..." clause, or nothing.</summary>
public static class MoneyReason
{
    /// <summary>
    /// What the simulator puts in ItemDescription when the payer gave NO reason.
    /// </summary>
    /// <remarks>
    /// The literal English string, matched literally — which is parity rather than a hack: the
    /// reference viewer does exactly the same comparison, and comments it "Simulator returns
    /// 'Payment' if no custom description has been entered"
    /// (<c>reason_from_transaction_type</c>, llviewermessage.cpp). It comes from the server, not
    /// from a translation, so it does not change with the user's language.
    /// </remarks>
    public const string NoReasonGiven = "Payment";

    /// <summary>
    /// The reason to show, or empty when there is none worth showing.
    /// </summary>
    /// <remarks>
    /// Measured on Agni: a plain gift with no reason arrives as <c>item='Payment'</c>, so
    /// printing the item description unconditionally would append "— Payment" to every tip and
    /// make the one case that DOES carry a reason harder to spot, not easier.
    /// </remarks>
    public static string For(int transactionType, string itemDescription)
    {
        if (string.IsNullOrWhiteSpace(itemDescription)) return string.Empty;

        if (transactionType == MoneyTransactionType.Gift
            && string.Equals(itemDescription, NoReasonGiven, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return itemDescription;
    }
}

/// <summary>
/// Decides which <c>MoneyBalanceReply</c> messages are worth telling the user about.
/// </summary>
/// <remarks>
/// Both rules are the reference viewer's, and both matter in practice
/// (<c>process_money_balance_reply</c> / <c>_extended</c>, llviewermessage.cpp):
///
/// <para><b>A reply the grid did not put into words is not shown at all.</b> This is the
/// viewer's first gate and it is unconditional: <c>if (desc.empty() …) return; // ...nothing to
/// display</c> (llviewermessage.cpp:4527-4532), before it looks at parties, amount or type. It is
/// what keeps the silent replies silent — a pure balance answer on connect, and, found in-world
/// 2026-09-22, the reply the simulator sends when you <b>give somebody an inventory item</b>:
/// source you, destination them, <c>amount = 0</c>, <c>type = 3000</c> (TRANS_GIVE_INVENTORY),
/// description empty. Announced, that reads „Du hast Clifton Howlett L$ 0 gezahlt." — a payment
/// that never happened, over a gift that did.</para>
///
/// <para><b>The same transaction arrives more than once.</b> The viewer keeps a lookback of
/// recent transaction ids for exactly this and drops repeats; without it one payment can be
/// announced twice.</para>
/// </remarks>
public sealed class MoneyTransactionFilter
{
    // The viewer's own numbers: remember up to MAX_LOOKBACK ids, and drop the oldest
    // POP_FRONT_SIZE when it overflows, so the list never grows without bound.
    private const int MaxLookback = 30;
    private const int PopFrontSize = 12;

    private readonly List<Guid> _recent = new();

    /// <summary>
    /// Whether this reply describes a real transaction that has not already been announced.
    /// </summary>
    /// <remarks>
    /// Stateful on purpose — asking twice about the same id answers true then false, which is the
    /// whole point. Call it once per reply.
    /// </remarks>
    /// <param name="gridDescribedIt">Whether the reply's own <c>MoneyData.Description</c> said
    /// anything — the simulator's sentence, e.g. "Clifton Howlett paid you L$1.", NOT the item
    /// description out of TransactionInfo. The viewer gates on exactly this field, so a reply the
    /// grid left wordless is one it did not mean to be announced.</param>
    public bool ShouldAnnounce(Guid transactionId, Guid sourceId, Guid destId, bool gridDescribedIt)
    {
        // The viewer's own first gate, and the whole of it. Parties and amount are deliberately
        // NOT consulted: a give-inventory reply has both parties filled and is still not a
        // payment, and a grid that fills no parties but says something in words is still worth
        // repeating verbatim (llviewermessage.cpp:4558). The wordless reply is the one to drop,
        // and it is the only one.
        if (!gridDescribedIt) return false;

        // A zero transaction id cannot be deduplicated, so it is never remembered — but it is
        // still a transaction, and suppressing it would be worse than repeating it.
        if (transactionId != Guid.Empty)
        {
            if (_recent.Contains(transactionId)) return false;

            if (_recent.Count > MaxLookback) _recent.RemoveRange(0, PopFrontSize);
            _recent.Add(transactionId);
        }

        return true;
    }
}
