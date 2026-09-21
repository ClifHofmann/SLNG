using Godot;
using System;
using System.Collections.Generic;
using SLNG.Net;

namespace SLNG.App.UI;

/// <summary>
/// The shared half of paying: the amount, the quick buttons, the balance, the shortfall and the
/// confirmation. <see cref="PayObjectWindow"/> and <see cref="PayAvatarWindow"/> differ only in
/// who is being paid and whether that target can name its own price.
/// </summary>
/// <remarks>
/// One base rather than two windows because the rules must not be allowed to differ. The same
/// project already learned this the expensive way with a grey text colour that existed as a
/// literal in 31 places and drifted out of readability unnoticed — and a payment dialog gets the
/// stricter treatment, because the failure mode is somebody else's money rather than a hard-to-read
/// label.
/// </remarks>
public abstract partial class PayWindowBase : SLNGWindow
{
    /// <summary>What to offer before anything better is known — the viewer's fastpay 1/5/10/20.</summary>
    private static readonly int[] QuickAmounts = SLNG.Core.PayPrice.DefaultButtons;

    public event Action? Closed;

    /// <summary>Raised only when a payment was actually sent, with what it cost.</summary>
    public event Action<string, int>? Paid;

    protected GridSession Session = null!;
    protected string TargetName = "";

    private VBoxContainer _contentVBox = null!;
    private SpinBox _amount = null!;
    private Label _amountLabel = null!;
    private Label? _shortLabel;
    private LineEdit? _reason;
    private Button _payButton = null!;
    private HBoxContainer _buttonRow = null!;
    private HBoxContainer? _confirmRow;
    private Label? _confirmLabel;
    private readonly List<Button> _quickButtons = new();
    private bool _closing;

    /// <summary>Title for this window, as an L10n key.</summary>
    protected abstract string TitleKey { get; }

    /// <summary>A line under the name saying what kind of payment this is, as an L10n key.
    /// Null for none.</summary>
    protected virtual string? SubtitleKey => null;

    /// <summary>Whether to offer a free-text reason. Only worth it where somebody can read it:
    /// a person's client shows it beside the amount, an object's script never looks at it.</summary>
    protected virtual bool OffersReason => false;

    /// <summary>What the user typed as the reason, trimmed. Empty when not offered.</summary>
    protected string Reason => _reason?.Text?.Trim() ?? string.Empty;

    /// <summary>Hands the payment to the session. Returns whether it actually went out.</summary>
    protected abstract bool Send(int amount);

    /// <summary>Called once the window is built, for anything target-specific — an object asks
    /// what it charges here.</summary>
    protected virtual void AfterBuild() { }

    public override void _Ready()
    {
        base._Ready();

        CustomMinimumSize = new Vector2(360, 0);
        Size = CustomMinimumSize;
        OnCloseRequested = () => Close(pay: false, amount: 0);

        var margin = new MarginContainer();
        ContentContainer.AddChild(margin);

        _contentVBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _contentVBox.AddThemeConstantOverride("separation", 8);
        margin.AddChild(_contentVBox);
    }

    /// <summary>Builds the window around a named target. Subclasses call this from their own
    /// Initialize once they know who is being paid.</summary>
    protected void Build(GridSession session, string targetName)
    {
        Session = session;
        TargetName = string.IsNullOrWhiteSpace(targetName) ? L10n.Tr("ui.buy.unnamed") : targetName;

        Title = L10n.Tr(TitleKey);

        var name = new Label
        {
            Text = TargetName,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        name.AddThemeFontSizeOverride("font_size", 14);
        _contentVBox.AddChild(name);

        if (SubtitleKey is { } subtitleKey)
        {
            var subtitle = new Label { Text = L10n.Tr(subtitleKey) };
            subtitle.AddThemeFontSizeOverride("font_size", 11);
            subtitle.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
            _contentVBox.AddChild(subtitle);
        }

        if (session.HasBalance)
        {
            var balance = new Label { Text = L10n.TrFormat("ui.buy.balance", $"{session.Balance:N0}") };
            balance.AddThemeFontSizeOverride("font_size", 11);
            balance.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
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
            // Straight to the attempt, as the viewer's fastpay buttons do -- but through the same
            // AttemptPay as everything else, so a quick button above the confirmation threshold is
            // confirmed like any other amount (llfloaterpay.cpp's onGive runs the same check
            // whichever button was pressed).
            button.Pressed += () => AttemptPay(AmountOf(index));
            _quickButtons.Add(button);
            quickRow.AddChild(button);
        }
        _contentVBox.AddChild(quickRow);

        _amountLabel = new Label { Text = L10n.Tr("ui.pay.other_amount") };
        _amountLabel.AddThemeFontSizeOverride("font_size", 11);
        _amountLabel.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
        _contentVBox.AddChild(_amountLabel);

        _amount = new SpinBox
        {
            MinValue = 1,
            MaxValue = SLNG.Core.PaymentCheck.MaxAmount,
            Step = 1,
            Value = QuickAmounts[0],
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _amount.ValueChanged += _ => RefreshAffordable();
        _contentVBox.AddChild(_amount);

        if (OffersReason)
        {
            var reasonLabel = new Label { Text = L10n.Tr("ui.pay.reason") };
            reasonLabel.AddThemeFontSizeOverride("font_size", 11);
            reasonLabel.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
            _contentVBox.AddChild(reasonLabel);

            _reason = new LineEdit
            {
                PlaceholderText = L10n.Tr("ui.pay.reason_hint"),
                // The wire field is a length-prefixed string with one byte of length, so anything
                // past 254 would be truncated somewhere out of sight. Cut it here, where the
                // person typing can see it happen.
                MaxLength = 254,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            _contentVBox.AddChild(_reason);
        }

        _buttonRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _buttonRow.AddThemeConstantOverride("separation", 8);

        _payButton = new Button { Text = L10n.Tr("ui.pay.confirm"), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _payButton.Pressed += () => AttemptPay((int)_amount.Value);
        _buttonRow.AddChild(_payButton);

        var cancel = new Button { Text = L10n.Tr("ui.buy.cancel"), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        cancel.Pressed += () => Close(pay: false, amount: 0);
        _buttonRow.AddChild(cancel);

        _contentVBox.AddChild(_buttonRow);

        RefreshAffordable();
        PositionWindow();

        AfterBuild();
    }

    /// <summary>
    /// The single door every payment goes through, whichever control asked for it.
    /// </summary>
    /// <remarks>
    /// A large amount is asked about once before it is sent, which is what the reference viewer
    /// does (<c>PAY_AMOUNT_NOTIFICATION</c>, llfloaterpay.cpp:505 — and only ABOVE it, so ordinary
    /// tips and vendor prices are not made tedious). Confirming everything would train the click
    /// that lets the big one through; confirming nothing means a typed digit too many is simply
    /// gone, and a transfer to another person cannot be taken back.
    /// </remarks>
    private void AttemptPay(int amount)
    {
        if (amount <= 0) return;

        if (!SLNG.Core.PaymentCheck.CanAfford(Session.HasBalance, Session.Balance, amount))
        {
            _amount.Value = amount;
            RefreshAffordable();
            return;
        }

        if (SLNG.Core.PaymentCheck.NeedsConfirmation(amount))
        {
            ShowConfirmation(amount);
            return;
        }

        Close(pay: true, amount: amount);
    }

    /// <summary>Replaces the buttons with a named yes/no, rather than opening a second window on
    /// top of this one. The amount and the recipient are both in the question, because "are you
    /// sure?" on its own is the version people click through.</summary>
    private void ShowConfirmation(int amount)
    {
        if (_confirmRow != null) { _confirmRow.QueueFree(); _confirmRow = null; }
        if (_confirmLabel != null) { _confirmLabel.QueueFree(); _confirmLabel = null; }

        _buttonRow.Visible = false;

        _confirmLabel = new Label
        {
            Text = L10n.TrFormat("ui.pay.confirm_large", $"{amount:N0}", TargetName),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _confirmLabel.AddThemeFontSizeOverride("font_size", 12);
        _confirmLabel.AddThemeColorOverride("font_color", new Color(0.98f, 0.72f, 0.42f));
        _contentVBox.AddChild(_confirmLabel);

        _confirmRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _confirmRow.AddThemeConstantOverride("separation", 8);

        // Back out first and focused: the reversible answer is the easy one, same as the script
        // permission prompt.
        var back = new Button { Text = L10n.Tr("ui.buy.cancel"), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        back.Pressed += () => DismissConfirmation();
        _confirmRow.AddChild(back);

        var confirm = new Button
        {
            Text = L10n.TrFormat("ui.pay.confirm_large_button", $"{amount:N0}"),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        confirm.Pressed += () => Close(pay: true, amount: amount);
        _confirmRow.AddChild(confirm);

        _contentVBox.AddChild(_confirmRow);
        back.GrabFocus();
    }

    private void DismissConfirmation()
    {
        if (_confirmRow != null) { _confirmRow.QueueFree(); _confirmRow = null; }
        if (_confirmLabel != null) { _confirmLabel.QueueFree(); _confirmLabel = null; }
        _buttonRow.Visible = true;
    }

    /// <summary>The amount a quick button stands for, read back from its own label so a script's
    /// figure and the button the user pressed cannot drift apart.</summary>
    private int AmountOf(int index)
    {
        var text = _quickButtons[index].Text.Replace("L$", "").Replace(".", "").Replace(",", "").Trim();
        return int.TryParse(text, out int amount) ? amount : 0;
    }

    /// <summary>Refuses an amount the balance cannot cover, with the shortfall named. Only when a
    /// balance is actually known — an unknown balance refuses nothing.</summary>
    protected void RefreshAffordable()
    {
        int amount = (int)_amount.Value;
        int shortfall = SLNG.Core.PaymentCheck.Shortfall(Session.HasBalance, Session.Balance, amount);

        _payButton.Disabled = shortfall > 0;

        if (shortfall == 0)
        {
            if (_shortLabel != null) { _shortLabel.QueueFree(); _shortLabel = null; }
            return;
        }

        _shortLabel ??= CreateShortLabel();
        _shortLabel.Text = L10n.TrFormat("ui.buy.insufficient", $"{shortfall:N0}");
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

    // --- for subclasses that learn their terms after the window is already up ---

    /// <summary>Pre-fills the free-entry amount.</summary>
    protected void SetAmount(int amount) => _amount.Value = amount;

    /// <summary>Takes the free-entry field away entirely — a script that says PAY_PRICE_HIDE wants
    /// one of ITS amounts and nothing else.</summary>
    protected void HideFreeAmount()
    {
        _amount.Visible = false;
        _amountLabel.Visible = false;
        _payButton.Visible = false;
    }

    /// <summary>Shows or hides one quick button, and what it stands for.</summary>
    protected void SetQuickAmount(int index, int price)
    {
        if (index < 0 || index >= _quickButtons.Count) return;
        _quickButtons[index].Visible = price > 0;
        if (price > 0) _quickButtons[index].Text = $"L$ {price:N0}";
    }

    protected int QuickButtonCount => _quickButtons.Count;

    private void PositionWindow()
    {
        var viewportSize = GetViewport()?.GetVisibleRect().Size ?? new Vector2(1280, 720);
        Position = new Vector2(
            Mathf.Max(0, (viewportSize.X - Size.X) / 2),
            Mathf.Max(0, (viewportSize.Y - Size.Y) / 3));
    }

    /// <summary>Last chance to unsubscribe from anything the subclass hooked up.</summary>
    protected virtual void BeforeClose() { }

    private void Close(bool pay, int amount)
    {
        if (_closing) return;
        _closing = true;

        BeforeClose();

        if (pay && amount > 0 && Send(amount))
        {
            Paid?.Invoke(TargetName, amount);
        }

        Closed?.Invoke();
        QueueFree();
    }
}
