using Godot;
using System;
using SLNG.Net;

namespace SLNG.App.UI;

/// <summary>
/// FEAT-ECON-02: "Pay this object" — the vendor/tip-jar half of in-world money.
///
/// Paying is not buying, and the two are not interchangeable. A purchase echoes a price the
/// simulator advertised; a payment names its own amount, because a paid object states its terms
/// on a prim face or in its description and its script decides what to do with whatever arrives.
/// That is precisely why this window cannot pre-fill a price and why the amount stays the user's
/// decision.
///
/// The quick amounts are the reference viewer's own (L$ 1 / 5 / 10 / 20) plus a free field, so the
/// common case is one click and the uncommon one is still possible.
/// </summary>
public partial class PayObjectWindow : SLNGWindow
{
    private static readonly int[] QuickAmounts = { 1, 5, 10, 20 };

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
        foreach (int quick in QuickAmounts)
        {
            var button = new Button { Text = $"L$ {quick}", SizeFlagsHorizontal = SizeFlags.ExpandFill };
            button.Pressed += () => _amount.Value = quick;
            quickRow.AddChild(button);
        }
        _contentVBox.AddChild(quickRow);

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

        int amount = (int)_amount.Value;
        if (pay && _session.PayObject(_objectId, amount, _objectName))
        {
            Paid?.Invoke(_objectName, amount);
        }

        Closed?.Invoke();
        QueueFree();
    }
}
