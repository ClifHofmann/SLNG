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

/// <summary>
/// Decides which <c>MoneyBalanceReply</c> messages are worth telling the user about.
/// </summary>
/// <remarks>
/// Both rules are the reference viewer's, and both matter in practice
/// (<c>process_money_balance_reply</c> / <c>_extended</c>, llviewermessage.cpp):
///
/// <para><b>A pure balance update is not a transaction.</b> The simulator sends
/// MoneyBalanceReply for the answer to MoneyBalanceRequest too — on connect, and whenever
/// anything asks. Those carry no source and no destination, and announcing them would put "you
/// were paid L$ 0" on screen at every login.</para>
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
    public bool ShouldAnnounce(Guid transactionId, Guid sourceId, Guid destId)
    {
        // Nobody on either end: an answer to "what is my balance", not a payment.
        if (sourceId == Guid.Empty && destId == Guid.Empty) return false;

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
