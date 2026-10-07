using Godot;
using System;
using SLNG.Core;

namespace SLNG.App.UI;

/// <summary>
/// The decision prompt for an incoming teleport offer or teleport request (BUG-NET-27).
///
/// <para><b>Offer</b> — somebody offers to teleport the agent to them: <i>Teleport</i> /
/// <i>Decline</i>. <b>Request</b> — somebody asks to be teleported to the agent: <i>Offer
/// teleport</i> / <i>Decline</i>. Nothing about this window moves the agent by itself; the two
/// answers are delegates supplied by the owner, which is also what lets the selftest build and
/// drive it without a <c>GridSession</c>.</para>
///
/// <para>Closing with the title-bar × sends nothing and leaves the question open, as the
/// reference viewer does with an unanswered notification — the owner keeps the notification entry
/// actionable for exactly that case (BUG-UI-12). Only an answer that was actually sent raises
/// <see cref="Answered"/>; one that could not be sent (no connection, a teleport already running)
/// keeps the window open and says so.</para>
///
/// <para>Modelled on <see cref="GroupInvitationWindow"/>: the same kind of thing, an unsolicited
/// prompt from the network that needs exactly one decision.</para>
/// </summary>
public partial class TeleportOfferWindow : SLNGWindow
{
    /// <summary>Fired once this window has closed and freed itself, so the owner can drop it.</summary>
    public event Action? Closed;

    /// <summary>Fired with the decision (true = the positive answer) once an answer was SENT. Not
    /// fired for the ×, which sends nothing.</summary>
    public event Action<bool>? Answered;

    /// <summary>Set by the owner before <see cref="Initialize"/> so several prompts cascade
    /// instead of stacking exactly on top of each other.</summary>
    public int CascadeIndex { get; set; }

    private Func<bool> _primary = () => false;
    private Func<bool> _secondary = () => false;
    private bool _closing;
    private VBoxContainer _contentVBox = null!;
    private Button _primaryButton = null!;
    private Button _secondaryButton = null!;
    private Label _status = null!;

    public override void _Ready()
    {
        base._Ready();

        CustomMinimumSize = new Vector2(360, 0); // height 0 -> shrink-wraps its contents
        Size = CustomMinimumSize;

        // No reply at all when dismissed via the title bar -- see the class doc.
        OnCloseRequested = () => Close(answered: null);

        var margin = new MarginContainer();
        ContentContainer.AddChild(margin);

        _contentVBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _contentVBox.AddThemeConstantOverride("separation", 8);
        margin.AddChild(_contentVBox);
    }

    /// <param name="accept">The positive answer: for an offer, take it (teleport); for a request,
    /// send the asker a teleport. Returns whether it was sent.</param>
    /// <param name="decline">The negative answer. Returns whether it was sent.</param>
    public void Initialize(TeleportOfferEvent e, Func<bool> accept, Func<bool> decline)
    {
        _primary = accept;
        _secondary = decline;
        bool isOffer = e.Kind == TeleportOfferKind.Offer;

        Title = L10n.Tr(isOffer ? "ui.teleport_offer.title_offer" : "ui.teleport_offer.title_request");

        var from = new Label
        {
            Text = L10n.TrFormat(isOffer ? "ui.teleport_offer.from_offer" : "ui.teleport_offer.from_request", e.FromName),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(220, 0), // a width floor: without it the wrapped height is measured at ~0 px and the window grows to hundreds of px
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        from.AddThemeFontSizeOverride("font_size", 12);
        _contentVBox.AddChild(from);

        // Their own words. For an offer the viewer's text ends in the destination's SLURL on a
        // second line, which is shown as it came.
        if (!string.IsNullOrWhiteSpace(e.Message))
        {
            var scroll = new ScrollContainer
            {
                CustomMinimumSize = new Vector2(0, 60),
                SizeFlagsVertical = SizeFlags.ExpandFill,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            };
            var message = new Label
            {
                Text = e.Message,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(220, 0), // a width floor: without it the wrapped height is measured at ~0 px and the window grows to hundreds of px
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            message.AddThemeFontSizeOverride("font_size", 12);
            message.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
            scroll.AddChild(message);
            _contentVBox.AddChild(scroll);
        }

        if (e.Maturity is { } rating)
        {
            var maturity = new Label { Text = L10n.TrFormat("ui.teleport_offer.destination", RatingName(rating)) };
            maturity.AddThemeFontSizeOverride("font_size", 11);
            maturity.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
            _contentVBox.AddChild(maturity);
        }

        if (e.Godlike)
        {
            var godlike = new Label { Text = L10n.Tr("ui.teleport_offer.godlike") };
            godlike.AddThemeFontSizeOverride("font_size", 11);
            godlike.AddThemeColorOverride("font_color", new Color(0.95f, 0.75f, 0.35f));
            _contentVBox.AddChild(godlike);
        }

        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 8);

        _primaryButton = new Button
        {
            Text = L10n.Tr(isOffer ? "ui.teleport_offer.teleport" : "ui.teleport_offer.offer_teleport"),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _primaryButton.Pressed += ChoosePrimary;
        row.AddChild(_primaryButton);

        _secondaryButton = new Button { Text = L10n.Tr("ui.teleport_offer.decline"), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _secondaryButton.Pressed += ChooseSecondary;
        row.AddChild(_secondaryButton);

        _contentVBox.AddChild(row);

        _status = new Label
        {
            Visible = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(220, 0), // a width floor: without it the wrapped height is measured at ~0 px and the window grows to hundreds of px
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _status.AddThemeFontSizeOverride("font_size", 11);
        _status.AddThemeColorOverride("font_color", new Color(0.95f, 0.45f, 0.40f));
        _contentVBox.AddChild(_status);

        // Not persisted: PersistId would make every prompt reopen at the last one's spot, which
        // for a transient prompt is worse than cascading from centre.
        PositionWindow();
    }

    private static string RatingName(MaturityLevel level) => L10n.Tr(level switch
    {
        MaturityLevel.Adult => "ui.teleport_offer.rating_adult",
        MaturityLevel.Moderate => "ui.teleport_offer.rating_moderate",
        _ => "ui.teleport_offer.rating_general",
    });

    /// <summary>The button labels and the two answers, reachable without a click. Selftest only —
    /// it cannot press a button, and these are what a press runs.</summary>
    internal string PrimaryText => _primaryButton.Text;
    internal string SecondaryText => _secondaryButton.Text;
    internal bool StatusVisible => _status.Visible;
    internal void ChoosePrimary() => Answer(_primary, positive: true);
    internal void ChooseSecondary() => Answer(_secondary, positive: false);

    private void Answer(Func<bool> send, bool positive)
    {
        if (_closing) return;

        bool sent;
        try { sent = send(); }
        catch (Exception ex)
        {
            GD.PrintErr($"[TeleportOffer] answer failed: {ex.Message}");
            sent = false;
        }

        if (!sent)
        {
            // Not answered, so not closed: the entry stays actionable and so does this window.
            _status.Text = L10n.Tr("ui.teleport_offer.send_failed");
            _status.Visible = true;
            return;
        }

        Close(positive);
    }

    private void PositionWindow()
    {
        var viewportSize = GetViewport()?.GetVisibleRect().Size ?? new Vector2(1280, 720);
        Position = new Vector2(
            Mathf.Max(0, (viewportSize.X - Size.X) / 2) + CascadeIndex * 28,
            Mathf.Max(TopInset + 10f, (viewportSize.Y - Size.Y) / 3) + CascadeIndex * 28);
        ClampToViewport();
    }

    /// <param name="answered">The decision that was sent, or null when the window is only being
    /// dismissed.</param>
    private void Close(bool? answered)
    {
        if (_closing) return;
        _closing = true;

        if (answered is { } decision) Answered?.Invoke(decision);

        Closed?.Invoke();
        QueueFree();
    }
}
