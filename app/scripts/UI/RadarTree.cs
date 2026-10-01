using Godot;

namespace SLNG.App.UI;

/// <summary>
/// The nearby-people table's <see cref="Tree"/> (FEAT-UI-39): the stock control plus the two things
/// it cannot do on its own. Its tooltip wraps -- a note can be a paragraph, and Godot's stock
/// tooltip is one unbroken line that runs off the screen -- and it is dressed in the flat, dark look
/// of the other SLNG windows (no panel of its own, a hairline under the header, a soft white wash for
/// hover and selection) through theme overrides, so every Tree feature stays available.
/// </summary>
public partial class RadarTree : Tree
{
    /// <summary>A tooltip wraps beyond this width; a short one stays as narrow as its text.</summary>
    private const float MaxTooltipWidth = 320f;

    public override void _Ready() => ApplyStyle();

    public override GodotObject _MakeCustomTooltip(string forText)
    {
        // Nothing to say: hand back null so the engine shows no tooltip at all.
        if (string.IsNullOrEmpty(forText)) return null!;

        // A "TooltipLabel" like the engine's own, so the font, colour and line spacing are the app-wide
        // tooltip style (UiTheme.ApplyTooltipStyle); the engine's tooltip popup supplies the box and
        // its padding, so nothing is added here.
        var label = new Label
        {
            Text = forText,
            ThemeTypeVariation = "TooltipLabel",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };

        // As wide as the WIDEST LINE needs, up to the cap. (Measuring the whole text as one line made
        // a two-line tooltip as wide as both lines side by side, with a wide empty strip on its right.)
        var font = label.GetThemeFont("font");
        int fontSize = label.GetThemeFontSize("font_size");
        float widest = 0f;
        foreach (var line in forText.Split('\n'))
            widest = Mathf.Max(widest, font.GetStringSize(line, HorizontalAlignment.Left, -1, fontSize).X);
        label.CustomMinimumSize = new Vector2(Mathf.Min(MaxTooltipWidth, widest + 2f), 0f);
        return label;
    }

    private void ApplyStyle()
    {
        var none = new StyleBoxEmpty();
        AddThemeStyleboxOverride("panel", none);
        AddThemeStyleboxOverride("focus", none);
        AddThemeStyleboxOverride("cursor", none);
        AddThemeStyleboxOverride("cursor_unfocused", none);

        var selected = Wash(0.14f);
        AddThemeStyleboxOverride("selected", selected);
        AddThemeStyleboxOverride("selected_focus", selected);
        AddThemeStyleboxOverride("hovered", Wash(0.06f));
        var hoveredSelected = Wash(0.18f);
        AddThemeStyleboxOverride("hovered_selected", hoveredSelected);
        AddThemeStyleboxOverride("hovered_selected_focus", hoveredSelected);

        // Header: transparent with a hairline under it, a little wash while hovered or pressed.
        AddThemeStyleboxOverride("title_button_normal", Header(0f));
        AddThemeStyleboxOverride("title_button_hover", Header(0.07f));
        AddThemeStyleboxOverride("title_button_pressed", Header(0.12f));

        AddThemeColorOverride("font_color", new Color(0.92f, 0.92f, 0.92f));
        AddThemeColorOverride("font_selected_color", Colors.White);
        AddThemeColorOverride("title_button_color", UiTheme.SecondaryText);
        AddThemeFontSizeOverride("font_size", 12);
        AddThemeFontSizeOverride("title_button_font_size", 11);

        AddThemeConstantOverride("h_separation", 6);
        // Air between a cell's text and the edge of its row's highlight: without it the first and
        // last cells' text sat flush against the highlight, which read as "too close to the edge".
        AddThemeConstantOverride("inner_item_margin_left", 3);
        AddThemeConstantOverride("inner_item_margin_right", 5);
        AddThemeConstantOverride("v_separation", 5);
        AddThemeConstantOverride("draw_guides", 0);
        AddThemeConstantOverride("draw_relationship_lines", 0);
    }

    private static StyleBoxFlat Wash(float alpha) => new()
    {
        BgColor = new Color(1, 1, 1, alpha),
        CornerRadiusTopLeft = 4,
        CornerRadiusTopRight = 4,
        CornerRadiusBottomLeft = 4,
        CornerRadiusBottomRight = 4,
    };

    private static StyleBoxFlat Header(float washAlpha) => new()
    {
        BgColor = new Color(1, 1, 1, washAlpha),
        BorderWidthBottom = 1,
        BorderColor = new Color(1, 1, 1, 0.14f),
        ContentMarginLeft = 4f,
        ContentMarginRight = 4f,
        ContentMarginTop = 4f,
        ContentMarginBottom = 4f,
    };
}
