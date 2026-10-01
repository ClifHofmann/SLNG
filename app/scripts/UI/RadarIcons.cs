using Godot;
using SLNG.Core;

namespace SLNG.App.UI;

/// <summary>
/// The glyphs and colours of the nearby-people table and its map (FEAT-UI-39), in one place so a
/// real icon set can replace them without touching the table code. Text glyphs, like
/// <see cref="InventoryIcons"/>: the table is a <c>Tree</c> whose cells are strings, a glyph scales
/// with the font and there is no atlas to import. The relation colours are shared by the dot in
/// front of a name and the dot on the map, so row and dot read as one thing.
/// </summary>
internal static class RadarIcons
{
    // Header glyphs for the icon-only columns. Every column's full name is still in its tooltip
    // and in the column chooser, so a glyph here is a shorthand and never the only label.
    public const string Voice = "🎤";
    public const string InRegion = "📍";
    public const string Typing = "💬";
    public const string Sitting = "🪑";
    public const string Note = "📝";
    public const string Language = "🌐";
    public const string Payment = "$";

    /// <summary>A voice cell while the avatar is speaking; empty otherwise.</summary>
    public const string Speaking = "🔊";

    public const string Gear = "⚙";
    public const string SortMenu = "⇅";
    public const string SortUp = "▲";
    public const string SortDown = "▼";

    /// <summary>Every glyph an icon cell can show: what the table measures its icon columns by, so a
    /// column is never narrower than the widest of them.</summary>
    public static readonly string[] CellGlyphs =
    {
        Speaking, InRegion, Typing, Sitting, Note, RadarTable.FormatPayment(PaymentInfo.Used),
    };

    public static readonly Color Friend = new(0.31f, 0.76f, 0.42f);
    public static readonly Color Other = new(0.89f, 0.29f, 0.29f);
    public static readonly Color Muted = new(0.55f, 0.55f, 0.55f);

    private static ImageTexture? _dot;

    /// <summary>The coloured dot in front of a name: white, so a cell's icon modulate sets its colour
    /// and one texture serves every relation.</summary>
    public static ImageTexture Dot => _dot ??= BuildDot();

    /// <summary>The short header text of an icon column, or null for a column whose title is its
    /// (localized) name.</summary>
    public static string? HeaderGlyph(RadarColumn column) => column switch
    {
        RadarColumn.Voice => Voice,
        RadarColumn.InRegion => InRegion,
        RadarColumn.Typing => Typing,
        RadarColumn.Sitting => Sitting,
        RadarColumn.Payment => Payment,
        RadarColumn.Note => Note,
        RadarColumn.Language => Language,
        _ => null,
    };

    public static Color RelationColor(RadarRelation relation) => relation switch
    {
        RadarRelation.Friend => Friend,
        RadarRelation.Muted => Muted,
        _ => Other,
    };

    /// <summary>A white disc with a one-pixel soft edge, drawn at 16 px and scaled down by the cell's
    /// icon max width so it stays smooth.</summary>
    private static ImageTexture BuildDot()
    {
        const int size = 16;
        const float radius = 7f;
        var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        img.Fill(new Color(1, 1, 1, 0));
        var centre = new Vector2(size * 0.5f, size * 0.5f);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float d = (new Vector2(x + 0.5f, y + 0.5f) - centre).Length();
                float coverage = Mathf.Clamp(radius - d + 0.5f, 0f, 1f);
                if (coverage > 0f) img.SetPixel(x, y, new Color(1, 1, 1, coverage));
            }
        }

        return ImageTexture.CreateFromImage(img);
    }
}
