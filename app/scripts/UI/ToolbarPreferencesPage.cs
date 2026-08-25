using System;
using System.Collections.Generic;
using Godot;

namespace SLNG.App.UI;

/// <summary>
/// "Toolbar" tab content for <see cref="PreferencesWindow"/>: one checkbox per registered
/// <see cref="ToolbarItemDefinition"/>, bound to <see cref="ToolbarSettings"/>. Kept as its own
/// reusable Control component (rather than inline code in PreferencesWindow) so the window
/// itself stays generic -- see PreferencesWindow.AddTab.
/// </summary>
public partial class ToolbarPreferencesPage : VBoxContainer
{
    private IReadOnlyList<ToolbarItemDefinition> _items = Array.Empty<ToolbarItemDefinition>();
    private ToolbarSettings _settings = null!;

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 10);
    }

    /// <summary>Builds one checkbox per registered item. Call once, right after this page has
    /// been added via PreferencesWindow.AddTab (mirrors ItemPropertiesWindow's
    /// construct-then-Initialize pattern used elsewhere in this codebase).</summary>
    public void Initialize(IReadOnlyList<ToolbarItemDefinition> items, ToolbarSettings settings)
    {
        _items = items;
        _settings = settings;

        var heading = new Label { Text = L10n.Tr("ui.preferences.toolbar_heading") };
        heading.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.8f));
        AddChild(heading);

        var grid = new GridContainer
        {
            Columns = 2,
        };
        grid.AddThemeConstantOverride("h_separation", 20);
        grid.AddThemeConstantOverride("v_separation", 4);
        AddChild(grid);

        foreach (var def in _items)
        {
            var check = new CheckBox
            {
                Text = def.Label,
                ButtonPressed = _settings.IsEnabled(def.Id),
                FocusMode = Control.FocusModeEnum.None,
            };
            check.Toggled += pressed => _settings.SetEnabled(def.Id, pressed);
            grid.AddChild(check);

            var option = new OptionButton();
            option.FocusMode = Control.FocusModeEnum.None;
            option.AddItem("Bottom", (int)ToolbarDockPosition.Bottom);
            option.AddItem("Top", (int)ToolbarDockPosition.Top);
            option.AddItem("Left", (int)ToolbarDockPosition.Left);
            option.AddItem("Right", (int)ToolbarDockPosition.Right);
            option.Selected = option.GetItemIndex((int)_settings.GetDockPosition(def.Id));
            
            option.ItemSelected += (idx) => {
                _settings.SetDockPosition(def.Id, (ToolbarDockPosition)option.GetItemId((int)idx));
            };
            grid.AddChild(option);
        }
    }
}
