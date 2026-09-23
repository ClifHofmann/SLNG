using System;

namespace SLNG.Net;

/// <summary>FEAT-ECON-01: the agent's L$ balance — the first piece of in-world economy.</summary>
public partial class GridSession
{
    private int _balance;

    /// <summary>The agent's L$ balance, as the simulator last reported it. Zero until
    /// <see cref="HasBalance"/> is true, which is not the same statement as "you have nothing":
    /// see it.</summary>
    public int Balance => _balance;

    /// <summary>Has the simulator told us a balance yet? A readout must be able to tell an
    /// unknown balance from a balance of zero — showing "L$ 0" to someone who has thousands,
    /// for the second between login and the first MoneyBalanceReply, is a worse lie than showing
    /// nothing.</summary>
    public bool HasBalance { get; private set; }

    /// <summary>Raised whenever the balance changes, carrying the new value.</summary>
    /// <remarks>
    /// Off a background thread, like every other event on this class: MoneyBalanceReply arrives
    /// on LibreMetaverse's packet thread. Marshal before touching a scene node.
    /// </remarks>
    public event EventHandler<int>? BalanceChanged;

    /// <summary>Asks the simulator for the current balance (MoneyBalanceRequest).</summary>
    /// <remarks>
    /// Called once the session is in-world rather than relying on the login response: the reply
    /// carries the balance whether or not the login payload happened to. The reference viewer
    /// does the same on connect.
    /// </remarks>
    public void RequestBalance()
    {
        if (!_client.Network.Connected) return;
        _client.Self.RequestBalance();
    }

    private void OnMoneyBalance(object? sender, LibreMetaverse.BalanceEventArgs e) => SetBalance(e.Balance);

    /// <summary>MVP5-2: money actually moved — somebody paid us, or we paid somebody.</summary>
    /// <remarks>
    /// Off a background thread like everything else here. Raised only for replies that describe a
    /// real transaction: see <see cref="SLNG.Core.MoneyTransactionFilter"/> for why a plain
    /// balance answer must not reach this.
    /// </remarks>
    public event EventHandler<SLNG.Core.MoneyTransactionEvent>? MoneyTransaction;

    private readonly SLNG.Core.MoneyTransactionFilter _transactionFilter = new();

    /// <summary>
    /// The half of MoneyBalanceReply the session used to throw away.
    /// </summary>
    /// <remarks>
    /// <c>MoneyBalance</c> carries the new figure and nothing else, so money arrived and the
    /// client said nothing — reported in-world as wanting to see "wieviel und warum" when
    /// somebody pays you. The reply's own TransactionInfo block has the other party, the amount,
    /// the transaction type and the description, which is what
    /// <c>process_money_balance_reply_extended</c> builds the viewer's PaymentReceived /
    /// PaymentSent notifications from.
    ///
    /// <para>Direction is decided the viewer's way: <c>source_id == gAgentID</c> means we paid.
    /// Not from the sign of the balance change, which says nothing when two transactions land in
    /// the same second.</para>
    /// </remarks>
    private void OnMoneyBalanceReply(object? sender, LibreMetaverse.MoneyBalanceReplyEventArgs e)
    {
        var info = e.TransactionInfo;
        System.Guid source = info?.SourceID.Guid ?? System.Guid.Empty;
        System.Guid dest = info?.DestID.Guid ?? System.Guid.Empty;

        // The reply carries a description in two places: the MoneyData one the simulator composes
        // into a sentence ("Clifton Howlett paid you L$1."), and the item description from the
        // transaction itself, which is what the payer actually wrote. Prefer the latter -- but
        // only when it says something: a gift with no reason arrives as the literal "Payment"
        // (measured on Agni), and appending "-- Payment" to every tip would bury the one case
        // that does carry a reason. MoneyReason.For applies the viewer's own test for that.
        string itemDescription = info != null
            ? SLNG.Core.MoneyReason.For(info.TransactionType, info.ItemDescription ?? string.Empty)
            : string.Empty;
        // The simulator's own sentence. Used ONLY when there are no parties -- it already reads
        // "Clifton Howlett paid you L$1.", so handing it to a Received/Paid line as the reason
        // would produce "Clifton Howlett hat dir L$ 1 gezahlt -- Clifton Howlett paid you L$1."
        string replyDescription = e.Description ?? string.Empty;

        // The gate is the GRID's sentence alone, exactly as the viewer gates on `desc`
        // (llviewermessage.cpp:4527). Not the item description: giving somebody an inventory item
        // produces a reply with both parties filled, amount 0 and type 3000, and announcing that
        // reads "Du hast X L$ 0 gezahlt" over a gift that cost nothing (BUG-ECON-01).
        bool gridDescribedIt = !string.IsNullOrWhiteSpace(replyDescription);

        if (SLNG.Core.Diag.Verbose)
        {
            Console.Error.WriteLine($"[Money] reply tx={e.TransactionID} ok={e.Success} " +
                $"balance={e.Balance} source={source} dest={dest} amount={info?.Amount ?? 0} " +
                $"type={info?.TransactionType ?? 0} item='{itemDescription}' desc='{replyDescription}'");
        }

        if (!_transactionFilter.ShouldAnnounce(e.TransactionID.Guid, source, dest, gridDescribedIt)) return;

        // No parties at all, but the grid said something: it does not fill TransactionInfo, and
        // its sentence is the whole story. The reference viewer prints it verbatim as a system
        // message rather than dropping it (llviewermessage.cpp:4558).
        if (source == System.Guid.Empty && dest == System.Guid.Empty)
        {
            MoneyTransaction?.Invoke(this, new SLNG.Core.MoneyTransactionEvent(
                e.TransactionID.Guid, SLNG.Core.MoneyDirection.Unknown,
                System.Guid.Empty, false, info?.Amount ?? 0, replyDescription,
                info?.TransactionType ?? 0, e.Success));
            return;
        }

        bool wePaid = source == _client.Self.AgentID.Guid;
        var direction = wePaid ? SLNG.Core.MoneyDirection.Paid : SLNG.Core.MoneyDirection.Received;

        MoneyTransaction?.Invoke(this, new SLNG.Core.MoneyTransactionEvent(
            e.TransactionID.Guid,
            direction,
            wePaid ? dest : source,
            wePaid ? (info?.IsDestGroup ?? false) : (info?.IsSourceGroup ?? false),
            info?.Amount ?? 0,
            itemDescription,
            info?.TransactionType ?? 0,
            e.Success));
    }

    /// <summary>FEAT-ECON-02: buys a for-sale object.</summary>
    /// <param name="localId">The object's root prim.</param>
    /// <param name="saleType">What it sells, as the SIMULATOR reported it.</param>
    /// <param name="price">Its price, likewise.</param>
    /// <remarks>
    /// The sale type and the price are the simulator's own figures, passed straight back. That is
    /// not a formality: the sim compares them against what it has and CANCELS the sale if they
    /// differ ("sale info is used for verification only, if it doesn't match region info then
    /// sale is canceled" -- llfloaterbuy.cpp). It is what stops a client from naming its own
    /// price, and it is why nothing here ever computes either number.
    ///
    /// GroupID is the buyer's ACTIVE group, not the object's -- the viewer sends
    /// gAgent.getGroupID() (LLSelectMgr::packAgentGroupAndCatID). CategoryID is where the
    /// delivery lands: the Objects folder for an object, the inventory root for contents, which
    /// is the split llfloaterbuy / llfloaterbuycontents make.
    /// </remarks>
    public bool BuyObject(ulong regionHandle, uint localId, SLNG.Core.PrimSaleType saleType, int price)
    {
        if (localId == 0 || saleType == SLNG.Core.PrimSaleType.NotForSale || price < 0) return false;

        // The object's region, not the agent's -- a purchase addressed to the wrong simulator
        // either vanishes or buys whatever carries that local id over there (BUG-NET-19).
        var sim = SimulatorFor(regionHandle, "buy", localId);
        if (sim == null) return false;

        var folderType = saleType == SLNG.Core.PrimSaleType.Contents
            ? LibreMetaverse.FolderType.Root
            : LibreMetaverse.FolderType.Object;
        var category = _client.Inventory.FindFolderForType(folderType);

        _client.Objects.BuyObject(sim, localId, (LibreMetaverse.SaleType)(byte)saleType, price,
            _client.Self.ActiveGroup, category);
        return true;
    }

    /// <summary>FEAT-ECON-02: what the object's own script says it charges, once it answers.
    /// Off a background thread like every other event here.</summary>
    public event EventHandler<SLNG.Core.PayPriceEvent>? PayPriceReceived;

    /// <summary>Asks an object what it wants to be paid (RequestPayPrice).</summary>
    /// <remarks>
    /// The reference viewer asks this the moment its pay floater opens, and shows the script's
    /// own amounts rather than a blank field -- llSetPayPrice is how a vendor states its price,
    /// and a viewer that ignores it makes the user guess a number the script will refuse.
    /// </remarks>
    public void RequestPayPrice(ulong regionHandle, uint localId, System.Guid objectId)
    {
        if (objectId == System.Guid.Empty) return;
        var sim = SimulatorFor(regionHandle, "pay price", localId);
        if (sim == null) return;
        _client.Objects.RequestPayPrice(sim, new LibreMetaverse.UUID(objectId));
    }

    private void OnPayPriceReply(object? sender, LibreMetaverse.PayPriceReplyEventArgs e)
    {
        PayPriceReceived?.Invoke(this, new SLNG.Core.PayPriceEvent(
            e.ObjectID.Guid, e.DefaultPrice, e.ButtonPrices ?? System.Array.Empty<int>()));
    }

    /// <summary>Pays an in-world object -- a vendor, a tip jar, a rental box.</summary>
    /// <param name="objectId">The object's UUID (not its local id: the money path is addressed by
    /// UUID like any other transfer).</param>
    /// <param name="amount">What the user chose. Unlike a purchase there is no price on the wire
    /// to echo back -- a paid object names its own terms in its description or on a prim face,
    /// and the script decides what to do with whatever arrives.</param>
    /// <param name="objectName">Shown in the simulator's own transaction record.</param>
    /// <remarks>
    /// Paying and buying are different transactions and the viewer keeps them apart: "Pay" is
    /// offered on the PrimFlags.Money bit (a script with a money() handler), "Buy" on the sale
    /// fields in ObjectProperties. A vendor is typically the former, which is why the two entries
    /// can appear on the same object -- or neither.
    /// </remarks>
    public bool PayObject(System.Guid objectId, int amount, string objectName)
    {
        if (!_client.Network.Connected || _client.Network.CurrentSim == null) return false;
        if (objectId == System.Guid.Empty || amount <= 0) return false;

        _client.Self.GiveObjectMoney(new LibreMetaverse.UUID(objectId), amount, objectName ?? string.Empty);
        return true;
    }

    /// <summary>The one place the balance changes, so "unknown" can only ever become "known"
    /// here and the event cannot fire without the property already agreeing with it.</summary>
    internal void SetBalance(int balance)
    {
        bool firstAnswer = !HasBalance;
        if (!firstAnswer && balance == _balance) return;

        _balance = balance;
        HasBalance = true;
        BalanceChanged?.Invoke(this, balance);
    }
}
