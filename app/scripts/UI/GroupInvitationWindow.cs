using Godot;
using System;
using SLNG.Core;
using SLNG.Net;

namespace SLNG.App.UI;

/// <summary>
/// "Join group?" prompt for an incoming group invitation (M5-3 follow-up). Shows the server's own
/// invitation text — which already names the group and its charter — the membership fee, and Join
/// / Decline.
///
/// Both answers are sent: declining is a real reply, not a dismissal. Closing the window with the
/// title-bar × sends nothing at all, which matches the real viewer leaving the notification
/// unanswered, and is why the fee is spelled out before either button is pressed.
///
/// Modelled on <see cref="ScriptDialogWindow"/> (same shrink-wrap layout, cascade and Closed
/// callback), since it is the same kind of thing: an unsolicited prompt that arrives from the
/// network and needs one decision.
/// </summary>
public partial class GroupInvitationWindow : SLNGWindow
{
    /// <summary>Fired once this window has closed and freed itself, so the owner can drop it —
    /// same pattern as <see cref="ScriptDialogWindow.Closed"/>.</summary>
    public event Action? Closed;

    /// <summary>Set by the owner before Initialize so several invitations cascade instead of
    /// stacking exactly on top of each other.</summary>
    public int CascadeIndex { get; set; }

    private GridSession _session = null!;
    private Guid _groupId;
    private Guid _sessionId;
    private bool _closing;
    private VBoxContainer _contentVBox = null!;

    public override void _Ready()
    {
        base._Ready();

        CustomMinimumSize = new Vector2(360, 0); // height 0 -> shrink-wraps its contents
        Size = CustomMinimumSize;

        // No reply at all when dismissed via the title bar -- see the class doc.
        OnCloseRequested = () => Close(respond: false, accept: false);

        // Inset comes from SLNGWindow.ContentContainer now (FEAT-UI-26); this container is kept
        // only because the layout below hangs off it.
        var margin = new MarginContainer();
        ContentContainer.AddChild(margin);

        _contentVBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _contentVBox.AddThemeConstantOverride("separation", 8);
        margin.AddChild(_contentVBox);
    }

    public void Initialize(GridSession session, GroupInvitationEvent e)
    {
        _session = session;
        _groupId = e.GroupId;
        _sessionId = e.SessionId;

        Title = L10n.Tr("ui.group_invite.title");

        if (!string.IsNullOrWhiteSpace(e.FromName))
        {
            var from = new Label { Text = L10n.TrFormat("ui.group_invite.from", e.FromName) };
            from.AddThemeFontSizeOverride("font_size", 11);
            from.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.6f));
            _contentVBox.AddChild(from);
        }

        // The server composes this text (group name + charter + fee), so it is shown as-is rather
        // than reassembled from fields we do not have.
        var scroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(0, 110),
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
        scroll.AddChild(message);
        _contentVBox.AddChild(scroll);

        // Spelled out separately: joining can cost money, and the server text is not guaranteed to
        // make that prominent.
        var fee = new Label
        {
            Text = e.MembershipFee > 0
                ? L10n.TrFormat("ui.group_invite.fee", e.MembershipFee)
                : L10n.Tr("ui.group_invite.fee_free"),
        };
        fee.AddThemeFontSizeOverride("font_size", 12);
        fee.AddThemeColorOverride("font_color",
            e.MembershipFee > 0 ? new Color(0.95f, 0.75f, 0.35f) : new Color(0.6f, 0.6f, 0.6f));
        _contentVBox.AddChild(fee);

        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 8);

        var join = new Button { Text = L10n.Tr("ui.group_invite.join"), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        join.Pressed += () => Close(respond: true, accept: true);
        row.AddChild(join);

        var decline = new Button { Text = L10n.Tr("ui.group_invite.decline"), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        decline.Pressed += () => Close(respond: true, accept: false);
        row.AddChild(decline);

        _contentVBox.AddChild(row);

        // Not persisted: PersistId would make every invitation reopen at the last one's spot,
        // which for a transient prompt is worse than cascading from centre.
        PositionWindow();
    }

    private void PositionWindow()
    {
        var viewportSize = GetViewport()?.GetVisibleRect().Size ?? new Vector2(1280, 720);
        Position = new Vector2(
            Mathf.Max(0, (viewportSize.X - Size.X) / 2) + CascadeIndex * 28,
            Mathf.Max(0, (viewportSize.Y - Size.Y) / 3) + CascadeIndex * 28);
    }

    private void Close(bool respond, bool accept)
    {
        if (_closing) return;
        _closing = true;

        if (respond) _session.RespondToGroupInvitation(_groupId, _sessionId, accept);

        Closed?.Invoke();
        QueueFree();
    }
}
