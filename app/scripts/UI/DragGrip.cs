using Godot;

namespace SLNG.App.UI;

/// <summary>
/// The six-dot grip that says "this can be picked up and moved" (the handle a sortable row has in most list UIs).
/// Drawn, not a font glyph, so it looks the same everywhere. It takes no mouse input: put it inside whatever is
/// being dragged (a <see cref="DragSortButton"/>), which carries the drag; it only has to be seen.
/// </summary>
public partial class DragGrip : Control
{
    private static readonly Color DotColor = new(0.78f, 0.82f, 0.9f, 0.75f);

    public DragGrip()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
    }

    public override void _Draw()
    {
        float cx = Size.X / 2f, cy = Size.Y / 2f;
        foreach (float dx in new[] { -2.5f, 2.5f })
            foreach (float dy in new[] { -4.5f, 0f, 4.5f })
                DrawCircle(new Vector2(cx + dx, cy + dy), 1.5f, DotColor);
    }
}
