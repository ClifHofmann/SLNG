using Godot;
using System;
using SLNG.Core;
using SLNG.Net;

namespace SLNG.App.UI;

/// <summary>
/// FEAT-ECON-02: "Buy this object?" — what it is, what buying it actually does, what it costs,
/// and what you have.
///
/// Spending money is the one action in this viewer that cannot be undone from inside it, so the
/// confirmation names the three things a buyer needs to consent: the object, the PRICE, and which
/// of the three sales it is. They are not interchangeable — buying the original takes the object
/// standing in front of you, a copy leaves it there and delivers a duplicate, and contents sells
/// what is inside it and leaves the object alone.
///
/// Every figure here is the simulator's. Nothing is computed, and the same numbers go back on the
/// wire: the sim cancels the sale if they disagree with its own, which is what stops a client
/// naming its own price.
/// </summary>
public partial class BuyObjectWindow : SLNGWindow
{
    /// <summary>Fired once the window has closed and freed itself.</summary>
    public event Action? Closed;

    /// <summary>Raised only when the purchase was actually sent, with what it cost — the owner
    /// puts a line in chat, since the object itself may arrive silently in inventory.</summary>
    public event Action<string, int>? Bought;

    private GridSession _session = null!;
    private uint _localId;
    private PrimSaleType _saleType;
    private int _price;
    private string _objectName = "";
    private bool _closing;
    private VBoxContainer _contentVBox = null!;

    public override void _Ready()
    {
        base._Ready();

        CustomMinimumSize = new Vector2(380, 0); // shrink-wraps its contents
        Size = CustomMinimumSize;
        OnCloseRequested = () => Close(buy: false);

        var margin = new MarginContainer();
        ContentContainer.AddChild(margin);

        _contentVBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _contentVBox.AddThemeConstantOverride("separation", 8);
        margin.AddChild(_contentVBox);
    }

    public void Initialize(GridSession session, uint localId, string objectName, PrimSaleType saleType, int price)
    {
        _session = session;
        _localId = localId;
        _objectName = string.IsNullOrWhiteSpace(objectName) ? L10n.Tr("ui.buy.unnamed") : objectName;
        _saleType = saleType;
        _price = price;

        Title = L10n.Tr("ui.buy.title");

        var name = new Label
        {
            Text = _objectName,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        name.AddThemeFontSizeOverride("font_size", 14);
        _contentVBox.AddChild(name);

        // WHAT is being bought, in words. "L$ 250" alone does not distinguish taking the object
        // away from being handed a copy of it.
        var what = new Label
        {
            Text = L10n.Tr(saleType switch
            {
                PrimSaleType.Original => "ui.buy.what_original",
                PrimSaleType.Copy => "ui.buy.what_copy",
                PrimSaleType.Contents => "ui.buy.what_contents",
                _ => "ui.buy.what_unknown",
            }),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        what.AddThemeFontSizeOverride("font_size", 11);
        what.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.6f));
        _contentVBox.AddChild(what);

        var priceLabel = new Label { Text = L10n.TrFormat("ui.buy.price", $"{price:N0}") };
        priceLabel.AddThemeFontSizeOverride("font_size", 14);
        priceLabel.AddThemeColorOverride("font_color", new Color(0.95f, 0.85f, 0.45f));
        _contentVBox.AddChild(priceLabel);

        // The balance, and only when it is known: "L$ 0" from a session that has not been told
        // yet would read as "you cannot afford this" (FEAT-ECON-01).
        bool knownBalance = session.HasBalance;
        bool affordable = !knownBalance || session.Balance >= price;

        if (knownBalance)
        {
            var balance = new Label { Text = L10n.TrFormat("ui.buy.balance", $"{session.Balance:N0}") };
            balance.AddThemeFontSizeOverride("font_size", 11);
            balance.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.6f));
            _contentVBox.AddChild(balance);
        }

        if (!affordable)
        {
            var short_ = new Label
            {
                Text = L10n.TrFormat("ui.buy.insufficient", $"{price - session.Balance:N0}"),
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            short_.AddThemeFontSizeOverride("font_size", 11);
            short_.AddThemeColorOverride("font_color", new Color(1.0f, 0.55f, 0.45f));
            _contentVBox.AddChild(short_);
        }

        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 8);

        var buy = new Button
        {
            Text = L10n.TrFormat("ui.buy.confirm", $"{price:N0}"),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            // Refused here rather than sent and rejected: the simulator's answer to an
            // unaffordable purchase is a bare alert, and the user would have to guess why.
            Disabled = !affordable,
        };
        buy.Pressed += () => Close(buy: true);
        row.AddChild(buy);

        var cancel = new Button
        {
            Text = L10n.Tr("ui.buy.cancel"),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        cancel.Pressed += () => Close(buy: false);
        row.AddChild(cancel);

        _contentVBox.AddChild(row);

        PositionWindow();
    }

    private void PositionWindow()
    {
        var viewportSize = GetViewport()?.GetVisibleRect().Size ?? new Vector2(1280, 720);
        Position = new Vector2(
            Mathf.Max(0, (viewportSize.X - Size.X) / 2),
            Mathf.Max(0, (viewportSize.Y - Size.Y) / 3));
    }

    private void Close(bool buy)
    {
        if (_closing) return;
        _closing = true;

        if (buy && _session.BuyObject(_localId, _saleType, _price))
        {
            Bought?.Invoke(_objectName, _price);
        }

        Closed?.Invoke();
        QueueFree();
    }
}
