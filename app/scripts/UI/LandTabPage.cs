using Godot;

namespace SLNG.App.UI;

/// <summary>Scaffolding shared by the read-only Land-Info tabs added in FEAT-LAND-02 (Options, Media,
/// Sound): the scrolling page with the same margins and column as the General tab, the key / value grid,
/// and the value widgets. Static helpers, not a base class, so each tab stays "one class implementing
/// <see cref="ILandInfoTab"/>".</summary>
internal static class LandTabPage
{
    /// <summary>Makes <paramref name="page"/> a vertically scrolling page and returns the column to fill.</summary>
    internal static VBoxContainer Prepare(ScrollContainer page)
    {
        // ShowNever, not Disabled: Disabled would make the page demand the width of its widest line
        // and push the whole window wider (see PreferencesWindow.AddTab).
        page.HorizontalScrollMode = ScrollContainer.ScrollMode.ShowNever;
        page.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        page.SizeFlagsVertical = Control.SizeFlags.ExpandFill;

        var margin = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        foreach (string side in new[] { "left", "right", "top", "bottom" })
            margin.AddThemeConstantOverride("margin_" + side, 6);
        page.AddChild(margin);

        var column = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        column.AddThemeConstantOverride("separation", 10);
        margin.AddChild(column);
        return column;
    }

    internal static GridContainer NewGrid(Container parent)
    {
        var grid = new GridContainer { Columns = 2, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 12);
        grid.AddThemeConstantOverride("v_separation", 6);
        parent.AddChild(grid);
        return grid;
    }

    /// <summary>Adds a "key | value" row and returns the key label.</summary>
    internal static Label AddRow(GridContainer grid, string key, Control value)
    {
        var label = new Label
        {
            Text = key,
            CustomMinimumSize = new Vector2(120, 0),
            SizeFlagsVertical = Control.SizeFlags.ShrinkBegin,
        };
        label.AddThemeFontSizeOverride("font_size", 12);
        label.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
        grid.AddChild(label);
        grid.AddChild(value);
        return label;
    }

    internal static Label ValueLabel() => new()
    {
        AutowrapMode = TextServer.AutowrapMode.WordSmart,
        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
    };

    /// <summary>Read-only but selectable: an id or URL is something to copy.</summary>
    internal static LineEdit SelectableValue() => new()
    {
        Editable = false,
        SelectingEnabled = true,
        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
    };

    /// <summary>A secondary-coloured line, for a section heading or a note.</summary>
    internal static Label Note(string text)
    {
        var label = new Label
        {
            Text = text,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        label.AddThemeFontSizeOverride("font_size", 12);
        label.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
        return label;
    }

    /// <summary>A row of read-only boxes side by side (an Everyone / Group pair).</summary>
    internal static HBoxContainer Pair(params Control[] controls)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 18);
        foreach (var c in controls) row.AddChild(c);
        return row;
    }

    internal static string Text(Control c) => c switch
    {
        Label l => l.Text,
        LineEdit e => e.Text,
        _ => string.Empty,
    };

    internal static void SetText(Control c, string text)
    {
        switch (c)
        {
            case Label l: l.Text = text; break;
            case LineEdit e: e.Text = text; break;
        }
    }
}
