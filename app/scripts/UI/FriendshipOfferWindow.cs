using Godot;
using System;
using SLNG.Core;

namespace SLNG.App.UI;

/// <summary>
/// The decision prompt for an incoming friendship offer (BUG-NET-28): who is offering, their own
/// words, and <i>Accept</i> / <i>Decline</i>. The two answers are delegates supplied by the owner,
/// which is also what lets the selftest build and drive it without a <c>GridSession</c>.
///
/// <para>Closing with the title-bar × sends nothing and leaves the question open, as the reference
/// viewer does with an unanswered notification — the owner keeps the notification entry actionable
/// for exactly that case (BUG-UI-12). Only an answer that was actually sent raises
/// <see cref="Answered"/>; one that could not be sent (no connection, nothing to answer with) keeps
/// the window open and says so.</para>
///
/// <para>Modelled on <see cref="TeleportOfferWindow"/>: the same kind of thing, an unsolicited
/// prompt from the network that needs exactly one decision.</para>
/// </summary>
public partial class FriendshipOfferWindow : SLNGWindow
{
    /// <summary>Fired once this window has closed and freed itself, so the owner can drop it.</summary>
    public event Action? Closed;

    /// <summary>Fired with the decision (true = accepted) once an answer was SENT. Not fired for
    /// the ×, which sends nothing.</summary>
    public event Action<bool>? Answered;

    /// <summary>Set by the owner before <see cref="Initialize"/> so several prompts cascade
    /// instead of stacking exactly on top of each other.</summary>
    public int CascadeIndex { get; set; }

    private Func<bool> _accept = () => false;
    private Func<bool> _decline = () => false;
    private bool _closing;
    private VBoxContainer _contentVBox = null!;
    private Button _acceptButton = null!;
    private Button _declineButton = null!;
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

    /// <param name="accept">Takes the offer. Returns whether the answer was sent.</param>
    /// <param name="decline">Turns it down. Returns whether the answer was sent.</param>
    public void Initialize(FriendshipOfferEvent e, Func<bool> accept, Func<bool> decline)
    {
        _accept = accept;
        _decline = decline;

        Title = L10n.Tr("ui.friendship_offer.title");

        var from = new Label
        {
            Text = L10n.TrFormat("ui.friendship_offer.from", e.FromName),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        from.AddThemeFontSizeOverride("font_size", 12);
        _contentVBox.AddChild(from);

        // Their own words. An offer from a client older than July 2008 has none, which is still an
        // offer (llimprocessing.cpp:1478), so the box is simply left out.
        if (!string.IsNullOrWhiteSpace(e.Message))
        {
            var scroll = new ScrollContainer
            {
                CustomMinimumSize = new Vector2(0, 48),
                SizeFlagsVertical = SizeFlags.ExpandFill,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            };
            var message = new Label
            {
                Text = e.Message,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            message.AddThemeFontSizeOverride("font_size", 12);
            message.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
            scroll.AddChild(message);
            _contentVBox.AddChild(scroll);
        }

        // The viewer's own footnote on this prompt: what accepting gives them by default.
        var rights = new Label
        {
            Text = L10n.Tr("ui.friendship_offer.default_rights"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        rights.AddThemeFontSizeOverride("font_size", 11);
        rights.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
        _contentVBox.AddChild(rights);

        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 8);

        _acceptButton = new Button { Text = L10n.Tr("ui.friendship_offer.accept"), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _acceptButton.Pressed += ChooseAccept;
        row.AddChild(_acceptButton);

        _declineButton = new Button { Text = L10n.Tr("ui.friendship_offer.decline"), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _declineButton.Pressed += ChooseDecline;
        row.AddChild(_declineButton);

        _contentVBox.AddChild(row);

        _status = new Label
        {
            Visible = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _status.AddThemeFontSizeOverride("font_size", 11);
        _status.AddThemeColorOverride("font_color", new Color(0.95f, 0.45f, 0.40f));
        _contentVBox.AddChild(_status);

        // Not persisted: PersistId would make every prompt reopen at the last one's spot, which
        // for a transient prompt is worse than cascading from centre.
        PositionWindow();
    }

    /// <summary>The button labels and the two answers, reachable without a click. Selftest only —
    /// it cannot press a button, and these are what a press runs.</summary>
    internal string AcceptText => _acceptButton.Text;
    internal string DeclineText => _declineButton.Text;
    internal bool StatusVisible => _status.Visible;
    internal void ChooseAccept() => Answer(_accept, accepted: true);
    internal void ChooseDecline() => Answer(_decline, accepted: false);

    private void Answer(Func<bool> send, bool accepted)
    {
        if (_closing) return;

        bool sent;
        try { sent = send(); }
        catch (Exception ex)
        {
            GD.PrintErr($"[FriendshipOffer] answer failed: {ex.Message}");
            sent = false;
        }

        if (!sent)
        {
            // Not answered, so not closed: the entry stays actionable and so does this window.
            _status.Text = L10n.Tr("ui.friendship_offer.send_failed");
            _status.Visible = true;
            return;
        }

        Close(accepted);
    }

    private void PositionWindow()
    {
        var viewportSize = GetViewport()?.GetVisibleRect().Size ?? new Vector2(1280, 720);
        Position = new Vector2(
            Mathf.Max(0, (viewportSize.X - Size.X) / 2) + CascadeIndex * 28,
            Mathf.Max(0, (viewportSize.Y - Size.Y) / 3) + CascadeIndex * 28);
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
