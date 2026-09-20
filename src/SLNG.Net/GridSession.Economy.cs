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
