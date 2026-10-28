using Godot;

namespace SLNG.App.UI.Prefs;

/// <summary>
/// Every colour, font size, width and spacing the Preferences builder uses (FEAT-UI-79). One place, so that a
/// setting looks the same whichever page it comes from; no page sets its own colours.
/// </summary>
public static class PrefsStyle
{
    /// <summary>Width of the label column of a label-and-control row.</summary>
    public const int LabelColumnWidth = 200;

    /// <summary>Gap between the label column and the control, and between a slider and its value.</summary>
    public const int ColumnGap = 12;

    /// <summary>Vertical gap between two rows of a section.</summary>
    public const int RowSeparation = 8;

    /// <summary>Vertical gap between the line of a row and its hint.</summary>
    public const int HintSeparation = 2;

    /// <summary>Vertical gap between two sections of a category.</summary>
    public const int SectionSeparation = 24;

    /// <summary>Indent of a hint under a checkbox, so it lines up with the checkbox's text.</summary>
    public const int CheckHintIndent = 26;

    public const int HeadingFontSize = 15;
    public const int HintFontSize = 12;
    public const int BadgeFontSize = 11;

    /// <summary>Width reserved for a slider's value text ("100%", "12.5 m").</summary>
    public const int ValueLabelWidth = 52;

    /// <summary>The narrowest a slider is drawn.</summary>
    public const int SliderMinWidth = 64;

    /// <summary>Space between a category's rows and the scroll bar on its right.</summary>
    public const int PageGutter = 10;

    /// <summary>A width floor for an autowrap label: with none it is measured at about 0 px and makes its window tall.</summary>
    public const int WrapFloorWidth = 40;

    public static readonly Color HeadingColor = new(0.9f, 0.9f, 0.9f);
    public static readonly Color LabelColor = new(0.78f, 0.78f, 0.78f);
    public static readonly Color ValueColor = new(0.8f, 0.8f, 0.8f);
    public static readonly Color HintColor = new(0.6f, 0.6f, 0.6f);
    public static readonly Color AdvancedTitleColor = new(0.7f, 0.7f, 0.7f);
    public static readonly Color AdvancedTitleHoverColor = new(1f, 1f, 1f);
    public static readonly Color NoticeColor = new(0.95f, 0.72f, 0.3f);
    public static readonly Color SuccessColor = new(0.4f, 0.9f, 0.5f);
    public static readonly Color InfoColor = new(0.62f, 0.72f, 0.82f);
    public static readonly Color BadgeTextColor = new(0.95f, 0.8f, 0.5f);
    public static readonly Color BadgeFillColor = new(0.95f, 0.7f, 0.3f, 0.16f);
    public static readonly Color AdvancedFillColor = new(1f, 1f, 1f, 0.03f);

    /// <summary>The tint behind a row the search found. The blue of the selected category in the list.</summary>
    public static readonly Color SearchHitColor = new(0.3f, 0.6f, 0.9f, 0.22f);

    /// <summary>How far the search tint reaches past a row on each side (inside the page's gutters), and above and below it.</summary>
    public const int SearchHitPadX = 6;
    public const int SearchHitPadY = 3;

    /// <summary>The tint behind a found row: a rounded, translucent rectangle. A dimmed row (a disabled dependent) is drawn
    /// through its own half-transparent modulate, so it gets the doubled alpha to look the same.</summary>
    public static StyleBoxFlat SearchHitStyle(bool dimmedRow)
        => dimmedRow ? _dimmedSearchHit ??= MakeSearchHit(SearchHitColor.A * 2f) : _searchHit ??= MakeSearchHit(SearchHitColor.A);

    private static StyleBoxFlat? _searchHit;
    private static StyleBoxFlat? _dimmedSearchHit;

    private static StyleBoxFlat MakeSearchHit(float alpha) => new()
    {
        BgColor = new Color(SearchHitColor.R, SearchHitColor.G, SearchHitColor.B, alpha),
        CornerRadiusTopLeft = 6,
        CornerRadiusTopRight = 6,
        CornerRadiusBottomLeft = 6,
        CornerRadiusBottomRight = 6,
    };

    /// <summary>The "no setting matches" line shown instead of a page.</summary>
    public static Label MakeNoMatch(string text)
    {
        var label = new Label
        {
            Text = text,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
        };
        label.AddThemeColorOverride("font_color", HintColor);
        label.AddThemeFontSizeOverride("font_size", 14);
        label.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        return label;
    }

    /// <summary>A section heading: no trailing colon, a size up from the rows.</summary>
    public static Label MakeHeading(string text)
    {
        var label = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore };
        label.AddThemeColorOverride("font_color", HeadingColor);
        label.AddThemeFontSizeOverride("font_size", HeadingFontSize);
        return label;
    }

    /// <summary>The label of a label-and-control row. Wraps instead of widening the window.</summary>
    public static Label MakeLabel(string text)
    {
        var label = new Label
        {
            Text = text,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(WrapFloorWidth, 0),
        };
        label.AddThemeColorOverride("font_color", LabelColor);
        return label;
    }

    /// <summary>The one-sentence explanation under a row.</summary>
    public static Label MakeHint(string text)
    {
        var label = new Label
        {
            Text = text,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            CustomMinimumSize = new Vector2(WrapFloorWidth, 0),
        };
        label.AddThemeColorOverride("font_color", HintColor);
        label.AddThemeFontSizeOverride("font_size", HintFontSize);
        return label;
    }

    /// <summary>A line of text that changes while the window is open and needs attention (a pending change, a warning).
    /// Same shape as <see cref="MakeHint"/>, in the notice colour.</summary>
    public static Label MakeNotice(string text)
    {
        var label = MakeHint(text);
        label.AddThemeColorOverride("font_color", NoticeColor);
        return label;
    }

    /// <summary>A line of text that confirms something that was just done (a profile saved), in the success colour.</summary>
    public static Label MakeStatus(string text)
    {
        var label = MakeHint(text);
        label.AddThemeColorOverride("font_color", SuccessColor);
        return label;
    }

    /// <summary>A line of text that reports a live value (a detected scale), in the info colour.</summary>
    public static Label MakeInfo(string text)
    {
        var label = MakeHint(text);
        label.AddThemeColorOverride("font_color", InfoColor);
        return label;
    }

    /// <summary>The text right of a slider.</summary>
    public static Label MakeValueLabel(string text)
    {
        var label = new Label
        {
            Text = text,
            HorizontalAlignment = HorizontalAlignment.Right,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(ValueLabelWidth, 0),
        };
        label.AddThemeColorOverride("font_color", ValueColor);
        return label;
    }

    /// <summary>The small pill after a label that says the change does not apply at once.</summary>
    public static Label MakeBadge(string text)
    {
        var label = new Label
        {
            Text = text,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.AddThemeColorOverride("font_color", BadgeTextColor);
        label.AddThemeFontSizeOverride("font_size", BadgeFontSize);
        label.AddThemeStyleboxOverride("normal", new StyleBoxFlat
        {
            BgColor = BadgeFillColor,
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
            ContentMarginLeft = 7,
            ContentMarginRight = 7,
            ContentMarginTop = 1,
            ContentMarginBottom = 1,
        });
        return label;
    }

    /// <summary>The "Advanced" fold-out: a quiet title bar, no frame around the opened body.</summary>
    public static void StyleAdvancedFold(FoldableContainer fold)
    {
        var bar = new StyleBoxFlat
        {
            BgColor = AdvancedFillColor,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
            ContentMarginLeft = 8,
            ContentMarginRight = 8,
            ContentMarginTop = 4,
            ContentMarginBottom = 4,
        };
        var body = new StyleBoxFlat
        {
            BgColor = new Color(0, 0, 0, 0),
            ContentMarginLeft = 8,
            ContentMarginRight = 0,
            ContentMarginTop = 8,
            ContentMarginBottom = 0,
        };
        foreach (string name in new[] { "title_panel", "title_collapsed_panel", "title_hover_panel", "title_collapsed_hover_panel" })
        {
            fold.AddThemeStyleboxOverride(name, bar);
        }
        fold.AddThemeStyleboxOverride("panel", body);
        fold.AddThemeColorOverride("font_color", AdvancedTitleColor);
        fold.AddThemeColorOverride("collapsed_font_color", AdvancedTitleColor);
        fold.AddThemeColorOverride("hover_font_color", AdvancedTitleHoverColor);
    }
}
