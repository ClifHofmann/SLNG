using Godot;

namespace SLNG.App.UI;

/// <summary>
/// The minimize / close button in an <see cref="SLNGWindow"/> title bar. The glyph is drawn as
/// lines instead of typed as a font character, so its size does not depend on the font's line
/// height -- that is what lets the symbol grow while the bar stays slim.
/// </summary>
internal partial class HeaderGlyphButton : Button
{
    internal enum GlyphKind { Minimize, Close }

    private static readonly Color RestColor = new(0.62f, 0.62f, 0.62f);

    private const float GlyphSize = 12f;
    private const float Thickness = 2f;

    private readonly GlyphKind _kind;
    private readonly Color _hoverColor;

    public HeaderGlyphButton(GlyphKind kind, Color hoverColor)
    {
        _kind = kind;
        _hoverColor = hoverColor;
        Flat = true;
        FocusMode = FocusModeEnum.None;
        CustomMinimumSize = new Vector2(24, 18);
        SizeFlagsVertical = SizeFlags.ShrinkCenter;

        // No theme padding: the bar's height is the button's minimum size, nothing more.
        var empty = new StyleBoxEmpty();
        foreach (var state in new[] { "normal", "hover", "pressed", "focus", "disabled" })
            AddThemeStyleboxOverride(state, empty);
    }

    public override void _Ready()
    {
        MouseEntered += QueueRedraw;
        MouseExited += QueueRedraw;
    }

    public override void _Draw()
    {
        var color = IsHovered() ? _hoverColor : RestColor;
        var c = Size / 2f;
        float h = GlyphSize / 2f;
        if (_kind == GlyphKind.Minimize)
        {
            // On the lower edge of the close cross (its arms reach c.Y + d), not above it.
            float lineY = c.Y + h * 0.8f;
            DrawLine(new Vector2(c.X - h, lineY), new Vector2(c.X + h, lineY), color, Thickness, true);
            return;
        }
        float d = h * 0.8f;
        DrawLine(new Vector2(c.X - d, c.Y - d), new Vector2(c.X + d, c.Y + d), color, Thickness, true);
        DrawLine(new Vector2(c.X - d, c.Y + d), new Vector2(c.X + d, c.Y - d), color, Thickness, true);
    }
}
