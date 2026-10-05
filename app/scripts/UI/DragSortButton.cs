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

    public override Variant _GetDragData(Vector2 atPosition)
    {
        if (DragData == null) return default;

        var preview = new Label { Text = DragPreviewText ?? Text };
        preview.AddThemeColorOverride("font_color", new Color(1, 1, 1, 0.9f));
        SetDragPreview(preview);
        return DragData;
    }

    public override bool _CanDropData(Vector2 atPosition, Variant data)
        => data.VariantType == Variant.Type.String && CanDrop?.Invoke(data.AsString()) == true;

    public override void _DropData(Vector2 atPosition, Variant data)
    {
        if (data.VariantType == Variant.Type.String) Dropped?.Invoke(data.AsString());
    }
}
