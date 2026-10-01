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
    private const int TooltipFontSize = 12;

    public override void _Ready() => ApplyStyle();

    public override GodotObject _MakeCustomTooltip(string forText)
    {
        // Nothing to say: hand back null so the engine shows no tooltip at all.
        if (string.IsNullOrEmpty(forText)) return null!;

        var font = GetThemeDefaultFont();
        float textWidth = font.GetStringSize(forText, HorizontalAlignment.Left, -1, TooltipFontSize).X;

        var label = new Label
        {
            Text = forText,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(Mathf.Min(MaxTooltipWidth, textWidth + 2f), 0f),
        };
        label.AddThemeFontSizeOverride("font_size", TooltipFontSize);

        // The engine wraps this in its own tooltip popup, which already has a background, so the
        // panel only adds the padding.
        var panel = new PanelContainer();
        var padding = new StyleBoxEmpty();
        padding.ContentMarginLeft = padding.ContentMarginRight = 6f;
        padding.ContentMarginTop = padding.ContentMarginBottom = 4f;
        panel.AddThemeStyleboxOverride("panel", padding);
        panel.AddChild(label);
        return panel;
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
