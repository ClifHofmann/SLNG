using System;
using Godot;

namespace SLNG.App.UI;

/// <summary>
/// A button that is also a handle for sorting a list by hand: grab it, drop it on another one. Used by the
/// conversation list in <see cref="ChatWindow"/> and by the category headers of <see cref="FriendsPanel"/>.
/// <para>What is dragged is a string the owner chooses (<see cref="DragData"/>); what may be dropped here and what a
/// drop does is the owner's too (<see cref="CanDrop"/>, <see cref="Dropped"/>). A string, and one with the owner's own
/// prefix, so a drag from somewhere else -- an inventory item -- is never mistaken for one of these.</para>
/// </summary>
public partial class DragSortButton : Button
{
    /// <summary>What a drag from this button carries; <c>null</c> means this one cannot be dragged.</summary>
    public string? DragData;

    /// <summary>What the cursor shows while dragging, when that should not be the button's own text (a header's text
    /// carries a fold arrow and a count). Defaults to <see cref="Button.Text"/>.</summary>
    public string? DragPreviewText;

    /// <summary>Whether the string being dragged may be dropped on this button.</summary>
    public Func<string, bool>? CanDrop;

    /// <summary>The string that was dropped on this button.</summary>
    public Action<string>? Dropped;

    /// <summary>Called with the dragged string while something that may be dropped here is held over this button, and
    /// with <c>null</c> when it leaves, is dropped or the drag ends -- so the owner can show where a drop would land.</summary>
    public Action<string?>? DropHover;

    private string? _hoveredDrag;

    public override Variant _GetDragData(Vector2 atPosition)
    {
        if (DragData == null) return default;

        // A small card in the accent colour, set beside the cursor rather than under it: bare text floating over the
        // list does not read as "you are carrying something".
        var card = new PanelContainer();
        card.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.17f, 0.38f, 0.62f, 0.94f),
            BorderColor = new Color(0.55f, 0.8f, 1f, 0.8f),
            BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
            CornerRadiusTopLeft = 5, CornerRadiusTopRight = 5, CornerRadiusBottomLeft = 5, CornerRadiusBottomRight = 5,
            ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 4, ContentMarginBottom = 4,
        });
        card.AddChild(new Label { Text = DragPreviewText ?? Text });

        var root = new Control();
        root.AddChild(card);
        card.Position = new Vector2(12, -12);
        SetDragPreview(root);
        return DragData;
    }

    public override bool _CanDropData(Vector2 atPosition, Variant data)
    {
        string? dragged = data.VariantType == Variant.Type.String ? data.AsString() : null;
        bool accepts = dragged != null && CanDrop?.Invoke(dragged) == true;
        SetDropHover(accepts ? dragged : null);
        return accepts;
    }

    public override void _DropData(Vector2 atPosition, Variant data)
    {
        SetDropHover(null);
        if (data.VariantType == Variant.Type.String) Dropped?.Invoke(data.AsString());
    }

    public override void _Notification(int what)
    {
        if (what == NotificationMouseExit || what == NotificationDragEnd) SetDropHover(null);
    }

    private void SetDropHover(string? dragged)
    {
        if (_hoveredDrag == dragged) return;
        _hoveredDrag = dragged;
        DropHover?.Invoke(dragged);
    }
}
