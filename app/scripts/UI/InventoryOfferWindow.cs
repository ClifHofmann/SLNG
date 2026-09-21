using Godot;
using System;
using SLNG.Core;
using SLNG.Net;

namespace SLNG.App.UI;

/// <summary>
/// "X is offering you an item" prompt for an incoming inventory offer (BUG-INV-04). Shows who is
/// giving, what, and Accept / Decline.
///
/// Both answers are sent: declining is a real reply that files the item into Trash server-side,
/// not a dismissal. Closing the window with the title-bar × sends nothing at all — the simulator
/// then holds the offer open, which is what the real viewer does with an unanswered notification,
/// and was the whole of BUG-INV-04 when SLNG never showed a window in the first place.
///
/// Modelled on <see cref="GroupInvitationWindow"/>, which is the same kind of thing: an
/// unsolicited network prompt that needs exactly one decision.
/// </summary>
public partial class InventoryOfferWindow : SLNGWindow
{
    /// <summary>Fired once this window has closed and freed itself, so the owner can drop it —
    /// same pattern as <see cref="GroupInvitationWindow.Closed"/>.</summary>
    public event Action? Closed;

    /// <summary>Raised once an offer has actually been answered — never when the window is only
    /// dismissed. Carries the user's decision and, for an accept, the folder the item was filed
    /// into (null when the grid gave us no folder): the owner refreshes that folder so the item
    /// shows up without a relog.</summary>
    public event Action<bool, Guid?>? Answered;

    /// <summary>Set by the owner before Initialize so several offers cascade instead of stacking
    /// exactly on top of each other.</summary>
    public int CascadeIndex { get; set; }

    private GridSession _session = null!;
    private InventoryOfferEvent _offer = null!;
    private bool _closing;
    private VBoxContainer _contentVBox = null!;

    public override void _Ready()
    {
        base._Ready();

        CustomMinimumSize = new Vector2(360, 0); // height 0 -> shrink-wraps its contents
        Size = CustomMinimumSize;

        // No reply at all when dismissed via the title bar -- see the class doc.
        OnCloseRequested = () => Close(respond: false, accept: false);

        var margin = new MarginContainer();
        ContentContainer.AddChild(margin);

        _contentVBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _contentVBox.AddThemeConstantOverride("separation", 8);
        margin.AddChild(_contentVBox);
    }

    public void Initialize(GridSession session, InventoryOfferEvent e)
    {
        _session = session;
        _offer = e;

        Title = L10n.Tr("ui.inventory_offer.title");

        var from = new Label
        {
            Text = L10n.TrFormat(
                e.FromTask ? "ui.inventory_offer.from_object" : "ui.inventory_offer.from_agent",
                e.FromName),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        from.AddThemeFontSizeOverride("font_size", 11);
        from.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
        _contentVBox.AddChild(from);

        // The item name comes from the simulator's own message body, shown as-is: it is the only
        // description of the item we have before accepting.
        var item = new Label
        {
            Text = $"{InventoryIcons.ForAssetType(e.AssetType)}  {e.ItemName}",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        item.AddThemeFontSizeOverride("font_size", 13);
        _contentVBox.AddChild(item);

        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 8);

        var accept = new Button
        {
            Text = L10n.Tr("ui.inventory_offer.accept"),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        accept.Pressed += () => Close(respond: true, accept: true);
        row.AddChild(accept);

        var decline = new Button
        {
            Text = L10n.Tr("ui.inventory_offer.decline"),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        decline.Pressed += () => Close(respond: true, accept: false);
        row.AddChild(decline);

        _contentVBox.AddChild(row);

        // Not persisted: a transient prompt reopening at the last one's spot is worse than
        // cascading from centre (same reasoning as GroupInvitationWindow).
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

        if (respond)
        {
            var folderId = _session.RespondToInventoryOffer(
                _offer.OfferId, _offer.FromId, _offer.AssetType, _offer.ItemId, _offer.FromTask, accept);
            Answered?.Invoke(accept, folderId);
        }

        Closed?.Invoke();
        QueueFree();
    }
}
