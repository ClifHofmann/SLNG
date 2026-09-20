using Godot;
using System;
using SLNG.Net;

namespace SLNG.App.UI;

/// <summary>
/// FEAT-ECON-02: "Pay this object" — the vendor/tip-jar half of in-world money.
///
/// Paying is not buying, and the two are not interchangeable. A purchase echoes a price the
/// simulator advertised for an object that is for sale; a payment goes to a script, and the
/// script names its own terms with llSetPayPrice -- a default amount and up to four fixed
/// buttons. So the window ASKS (RequestPayPrice) and shows what the object charges, which is
/// what the reference viewer does and what "da bekomme ich eine feste Auswahl mit einem
/// vorgegebenen Preis" is.
///
/// Until that answer arrives -- and for an object whose script says nothing -- it falls back to
/// the viewer's own L$ 1 / 5 / 10 / 20 plus a free field. A script can take the free field away
/// entirely (PAY_PRICE_HIDE), and then its amounts are the only ones offered.
/// </summary>
public partial class PayObjectWindow : SLNGWindow
{
    // What to offer until the object's own script answers -- the viewer's fastpay 1/5/10/20.
    private static readonly int[] QuickAmounts = SLNG.Core.PayPrice.DefaultButtons;

    public event Action? Closed;

    /// <summary>Raised only when a payment was actually sent, with what it cost.</summary>
    public event Action<string, int>? Paid;

    private GridSession _session = null!;
    private Guid _objectId;
    private string _objectName = "";
    private bool _closing;
    private VBoxContainer _contentVBox = null!;
    private SpinBox _amount = null!;
    private Label? _shortLabel;
    private Button _payButton = null!;
    private readonly System.Collections.Generic.List<Button> _quickButtons = new();
    private Label _amountLabel = null!;

    public override void _Ready()
    {
        base._Ready();

        CustomMinimumSize = new Vector2(360, 0);
        Size = CustomMinimumSize;
        OnCloseRequested = () => Close(pay: false);

        var margin = new MarginContainer();
        ContentContainer.AddChild(margin);

        _contentVBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _contentVBox.AddThemeConstantOverride("separation", 8);
        margin.AddChild(_contentVBox);
    }

    public void Initialize(GridSession session, Guid objectId, string objectName)
    {
        _session = session;
        _objectId = objectId;
        _objectName = string.IsNullOrWhiteSpace(objectName) ? L10n.Tr("ui.buy.unnamed") : objectName;

        Title = L10n.Tr("ui.pay.title");

        var name = new Label
        {
            Text = _objectName,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        name.AddThemeFontSizeOverride("font_size", 14);
        _contentVBox.AddChild(name);

        if (session.HasBalance)
        {
            var balance = new Label { Text = L10n.TrFormat("ui.buy.balance", $"{session.Balance:N0}") };
            balance.AddThemeFontSizeOverride("font_size", 11);
            balance.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.6f));
            _contentVBox.AddChild(balance);
        }

        var quickRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        quickRow.AddThemeConstantOverride("separation", 6);
        for (int i = 0; i < SLNG.Core.PayPrice.MaxButtons; i++)
        {
            int fallback = i < QuickAmounts.Length ? QuickAmounts[i] : 0;
            var button = new Button
            {
                Text = $"L$ {fallback}",
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                Visible = fallback > 0,
            };
            int index = i;
            // Pays straight away, as the viewer's fastpay buttons do: the amount is the object's
            // own, so there is nothing left to confirm.
            button.Pressed += () => { _amount.Value = AmountOf(index); Close(pay: true); };
            _quickButtons.Add(button);
            quickRow.AddChild(button);
        }
        _contentVBox.AddChild(quickRow);

        _amountLabel = new Label { Text = L10n.Tr("ui.pay.other_amount") };
        _amountLabel.AddThemeFontSizeOverride("font_size", 11);
        _amountLabel.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.6f));
        _contentVBox.AddChild(_amountLabel);

        _amount = new SpinBox
        {
            MinValue = 1,
            // SL's own per-transaction ceiling. A typo of one digit too many is otherwise a real
            // amount of money, and the field is where that typo happens.
            MaxValue = 100000,
            Step = 1,
            Value = QuickAmounts[0],
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _amount.ValueChanged += _ => RefreshAffordable();
        _contentVBox.AddChild(_amount);

        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 8);

        _payButton = new Button { Text = L10n.Tr("ui.pay.confirm"), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _payButton.Pressed += () => Close(pay: true);
        row.AddChild(_payButton);

        var cancel = new Button { Text = L10n.Tr("ui.buy.cancel"), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        cancel.Pressed += () => Close(pay: false);
        row.AddChild(cancel);

        _contentVBox.AddChild(row);

        RefreshAffordable();
        PositionWindow();

        // What does this object actually charge? Its script may have said so with
        // llSetPayPrice, and the viewer asks before showing anything else. The answer arrives
        // off a network thread, hence the deferred hop.
        _session.PayPriceReceived += OnPayPriceReceived;
        _session.RequestPayPrice(objectId);
    }

    private void OnPayPriceReceived(object? sender, SLNG.Core.PayPriceEvent e)
    {
        if (e.ObjectId != _objectId) return; // another object's pay info
        CallDeferred(MethodName.ApplyPayPrice, e.DefaultPrice, e.ButtonPrices);
    }

    /// <summary>Applies what the object's script asked for.</summary>
    /// <remarks>
    /// The rules are the viewer's (LLFloaterPay::processPayPriceReply): a default price of HIDE
    /// takes the free field away entirely -- the script wants to be paid one of ITS amounts and
    /// nothing else -- DEFAULT leaves the field alone, and any other value pre-fills it. A button
    /// price is shown when it is positive and hidden otherwise, so a script offering two amounts
    /// shows two buttons rather than two of its own and two of ours.
    /// </remarks>
    private void ApplyPayPrice(int defaultPrice, int[] buttonPrices)
    {
        if (defaultPrice == SLNG.Core.PayPrice.Hide)
        {
            _amount.Visible = false;
            _amountLabel.Visible = false;
            _payButton.Visible = false;
        }
        else if (defaultPrice != SLNG.Core.PayPrice.Default && defaultPrice > 0)
        {
            _amount.Value = defaultPrice;
        }

        for (int i = 0; i < _quickButtons.Count; i++)
        {
            int price = i < buttonPrices.Length ? buttonPrices[i] : SLNG.Core.PayPrice.Hide;
            _quickButtons[i].Visible = price > 0;
            if (price > 0) _quickButtons[i].Text = $"L$ {price:N0}";
        }

        RefreshAffordable();
    }

    /// <summary>The amount a quick button stands for, read back from its own label so the
    /// script's figure and the button the user pressed cannot drift apart.</summary>
    private int AmountOf(int index)
    {
        var text = _quickButtons[index].Text.Replace("L$", "").Replace(".", "").Replace(",", "").Trim();
        return int.TryParse(text, out int amount) ? amount : 0;
    }

    /// <summary>Refuses an amount the balance cannot cover, with the shortfall named. Only when a
    /// balance is actually known -- see FEAT-ECON-01 on why "L$ 0" from a session that has not
    /// been told is not an answer.</summary>
    private void RefreshAffordable()
    {
        int amount = (int)_amount.Value;
        bool affordable = !_session.HasBalance || _session.Balance >= amount;

        _payButton.Disabled = !affordable;

        if (affordable)
        {
            if (_shortLabel != null) { _shortLabel.QueueFree(); _shortLabel = null; }
            return;
        }

        _shortLabel ??= CreateShortLabel();
        _shortLabel.Text = L10n.TrFormat("ui.buy.insufficient", $"{amount - _session.Balance:N0}");
    }

    private Label CreateShortLabel()
    {
        var label = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        label.AddThemeFontSizeOverride("font_size", 11);
        label.AddThemeColorOverride("font_color", new Color(1.0f, 0.55f, 0.45f));
        _contentVBox.AddChild(label);
        _contentVBox.MoveChild(label, _contentVBox.GetChildCount() - 2); // above the buttons
        return label;
    }

    private void PositionWindow()
    {
        var viewportSize = GetViewport()?.GetVisibleRect().Size ?? new Vector2(1280, 720);
        Position = new Vector2(
            Mathf.Max(0, (viewportSize.X - Size.X) / 2),
            Mathf.Max(0, (viewportSize.Y - Size.Y) / 3));
    }

    private void Close(bool pay)
    {
        if (_closing) return;
        _closing = true;
        _session.PayPriceReceived -= OnPayPriceReceived;

        int amount = (int)_amount.Value;
        if (pay && _session.PayObject(_objectId, amount, _objectName))
        {
            Paid?.Invoke(_objectName, amount);
        }

        Closed?.Invoke();
        QueueFree();
    }
}
