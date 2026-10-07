using Godot;
using System;

namespace SLNG.App.UI;

/// <summary>
/// The prompt that comes BEFORE a friendship offer is sent: who it goes to and the message that travels
/// with it, which the person writes themselves. The reference viewer asks the same thing, pre-filled with
/// a default line; the recipient sees the text in their own offer prompt.
///
/// The send itself is a delegate supplied by the owner (it returns whether anything was sent), which is
/// also what lets the selftest build and drive the window without a <c>GridSession</c>. Cancelling, or the
/// title-bar ×, sends nothing.
/// </summary>
public partial class FriendshipRequestWindow : SLNGWindow
{
    /// <summary>An offer's text is carried in an instant message, which the grid caps; stay under it.</summary>
    public const int MaxMessageChars = 1000;

    /// <summary>Fired once this window has closed and freed itself.</summary>
    public event Action? Closed;

    /// <summary>Fired once the offer was SENT (not for Cancel or ×).</summary>
    public event Action? Sent;

    /// <summary>Cascades several prompts instead of stacking them.</summary>
    public int CascadeIndex { get; set; }

    private Func<string, bool> _send = _ => false;
    private bool _closing;
    private VBoxContainer _vbox = null!;
    private TextEdit _messageEdit = null!;
    private Button _sendButton = null!;
    private Label _status = null!;

    public override void _Ready()
    {
        base._Ready();

        CustomMinimumSize = new Vector2(360, 0); // height 0 -> shrink-wraps its contents
        Size = CustomMinimumSize;
        OnCloseRequested = () => Close(sent: false);

        // No margin of its own: SLNGWindow already insets its content.
        _vbox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _vbox.AddThemeConstantOverride("separation", 8);
        ContentContainer.AddChild(_vbox);
    }

    /// <param name="targetName">Who the offer goes to, for the heading.</param>
    /// <param name="send">Sends the offer with this text. Returns whether it was sent.</param>
    public void Initialize(string targetName, Func<string, bool> send)
    {
        _send = send;
        Title = L10n.Tr("ui.friendship_request.title");

        // A width floor on every wrapped label: without it the wrapped height is measured at ~0 px and the
        // window grows to hundreds of px.
        var heading = new Label
        {
            Text = L10n.TrFormat("ui.friendship_request.heading", string.IsNullOrWhiteSpace(targetName) ? "?" : targetName),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(220, 0),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        heading.AddThemeFontSizeOverride("font_size", 12);
        _vbox.AddChild(heading);

        // The viewer's own explanation of what a friendship gives (notifications.xml, AddFriendWithMessage).
        var explain = new Label
        {
            Text = L10n.Tr("ui.friendship_request.explain"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(220, 0),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        explain.AddThemeFontSizeOverride("font_size", 11);
        explain.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
        _vbox.AddChild(explain);

        var caption = new Label { Text = L10n.Tr("ui.friendship_request.message") };
        caption.AddThemeFontSizeOverride("font_size", 11);
        caption.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
        _vbox.AddChild(caption);

        _messageEdit = new TextEdit
        {
            Text = L10n.Tr("ui.friendship_request.default_message"),
            WrapMode = TextEdit.LineWrappingMode.Boundary,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(220, 76),
        };
        _messageEdit.AddThemeFontSizeOverride("font_size", 12);
        _messageEdit.TextChanged += () => _status.Visible = false;
        _vbox.AddChild(_messageEdit);

        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 8);
        _sendButton = new Button { Text = L10n.Tr("ui.friendship_request.send"), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _sendButton.Pressed += Send;
        row.AddChild(_sendButton);
        var cancel = new Button { Text = L10n.Tr("ui.friendship_request.cancel"), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        cancel.Pressed += () => Close(sent: false);
        row.AddChild(cancel);
        _vbox.AddChild(row);

        _status = new Label
        {
            Visible = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(220, 0),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _status.AddThemeFontSizeOverride("font_size", 11);
        _status.AddThemeColorOverride("font_color", new Color(0.95f, 0.45f, 0.40f));
        _vbox.AddChild(_status);

        PositionWindow();
        _messageEdit.GrabFocus();
        _messageEdit.SelectAll(); // typing replaces the default line; clicking keeps it to edit
    }

    // ---- selftest seams -------------------------------------------------------------------------
    internal string MessageText { get => _messageEdit.Text; set => _messageEdit.Text = value; }
    internal bool StatusVisible => _status.Visible;
    internal void PressSend() => Send();

    private void Send()
    {
        if (_closing) return;

        string text = _messageEdit.Text.Trim();
        if (text.Length > MaxMessageChars) text = text[..MaxMessageChars];

        bool sent;
        try { sent = _send(text); }
        catch (Exception ex)
        {
            GD.PrintErr($"[FriendshipRequest] send failed: {ex.Message}");
            sent = false;
        }

        if (!sent)
        {
            // Nothing went out, so the window stays and the text stays.
            _status.Text = L10n.Tr("ui.friendship_request.send_failed");
            _status.Visible = true;
            return;
        }

        Close(sent: true);
    }

    private void PositionWindow()
    {
        var viewportSize = GetViewport()?.GetVisibleRect().Size ?? new Vector2(1280, 720);
        Position = new Vector2(
            Mathf.Max(0, (viewportSize.X - Size.X) / 2) + CascadeIndex * 28,
            Mathf.Max(TopInset + 10f, (viewportSize.Y - Size.Y) / 3) + CascadeIndex * 28);
        ClampToViewport();
    }

    private void Close(bool sent)
    {
        if (_closing) return;
        _closing = true;
        if (sent) Sent?.Invoke();
        Closed?.Invoke();
        QueueFree();
    }
}
