using System;
using Godot;

namespace SLNG.App.UI;

/// <summary>
/// The thing an inventory item is dropped ON when it is dropped on the 3D world (FEAT-INV-15).
/// </summary>
/// <remarks>
/// Godot only calls <c>_CanDropData</c> / <c>_DropData</c> on a control the drop search can find, and the
/// search skips every control whose mouse filter is Ignore. The 3D view is not a control, and
/// <c>Boot</c> itself is Ignore (it has to be: a world click has to fall through to
/// <c>_UnhandledInput</c>), so with nothing else under the cursor a drop on the world was never offered to
/// anyone and releasing the button did nothing.
///
/// <para>This control covers the whole screen but is Ignore -- invisible to the mouse -- for everything except
/// the length of an inventory-item drag. Then, and only then, it turns Stop and becomes the thing under the
/// cursor wherever no window, HUD or menu is. It is Boot's first child, so everything drawn above it wins
/// the hit test; a drop onto a window is that window's business, not the world's.</para>
/// </remarks>
public partial class WorldDropTarget : Control
{
    /// <summary>The prefix of the drag data an inventory row carries (see <c>InventoryPanel</c>).</summary>
    public const string InventoryItemPrefix = "slng_item|";

    private readonly Func<Vector2, Variant, bool> _canDrop;
    private readonly Action<Vector2, Variant> _drop;

    /// <param name="canDrop">Whether a drop at that position could be accepted; the answer picks the cursor.</param>
    /// <param name="drop">Takes the drop.</param>
    public WorldDropTarget(Func<Vector2, Variant, bool> canDrop, Action<Vector2, Variant> drop)
    {
        _canDrop = canDrop;
        _drop = drop;
        Name = nameof(WorldDropTarget);
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Process(double delta)
    {
        var viewport = GetViewport();
        bool wanted = viewport != null && viewport.GuiIsDragging() && IsInventoryItem(viewport.GuiGetDragData());
        var filter = wanted ? MouseFilterEnum.Stop : MouseFilterEnum.Ignore;
        if (MouseFilter != filter) MouseFilter = filter;
    }

    public override bool _CanDropData(Vector2 atPosition, Variant data) => _canDrop(atPosition, data);

    public override void _DropData(Vector2 atPosition, Variant data) => _drop(atPosition, data);

    private static bool IsInventoryItem(Variant data)
        => data.VariantType == Variant.Type.String && data.AsString().StartsWith(InventoryItemPrefix, StringComparison.Ordinal);
}
