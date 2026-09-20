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
    public bool BuyObject(uint localId, SLNG.Core.PrimSaleType saleType, int price)
    {
        var sim = _client.Network.CurrentSim;
        if (!_client.Network.Connected || sim == null) return false;
        if (localId == 0 || saleType == SLNG.Core.PrimSaleType.NotForSale || price < 0) return false;

        var folderType = saleType == SLNG.Core.PrimSaleType.Contents
            ? LibreMetaverse.FolderType.Root
            : LibreMetaverse.FolderType.Object;
        var category = _client.Inventory.FindFolderForType(folderType);

        _client.Objects.BuyObject(sim, localId, (LibreMetaverse.SaleType)(byte)saleType, price,
            _client.Self.ActiveGroup, category);
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
