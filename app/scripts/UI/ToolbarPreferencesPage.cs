using System.Collections.Generic;
using System.Linq;
using Godot;
using SLNG.App.UI.Prefs;

namespace SLNG.App.UI;

/// <summary>
/// The "Bottom bar" section of the General category (FEAT-UI-79): one checkbox per registered
/// <see cref="ToolbarItemDefinition"/>, bound to <see cref="ToolbarSettings"/>. The items' labels are not L10n keys (they
/// are names handed over by Boot.SetupHud), so the checkboxes are one block rather than one row each; the search finds
/// them through the block's literal texts.
/// </summary>
public sealed class ToolbarPreferencesPage
{
    /// <summary>Fills <paramref name="section"/> (General, "bottom_bar"), which Boot has already put in its place. Call
    /// once, after the section is in the window's tree.</summary>
    public void Build(PrefsSection section, IReadOnlyList<ToolbarItemDefinition> items, ToolbarSettings settings)
    {
        string prefix = $"{section.CategoryId}.{section.Id}";
        section.AddNote($"{prefix}.intro", "ui.preferences.toolbar_heading");

        var list = new VBoxContainer();
        list.AddThemeConstantOverride("separation", PrefsStyle.RowSeparation / 2);
        foreach (var def in items)
        {
            var check = new CheckBox
            {
                Text = def.Label,
                ButtonPressed = settings.IsEnabled(def.Id),
                FocusMode = Control.FocusModeEnum.None,
            };
            check.Toggled += pressed => settings.SetEnabled(def.Id, pressed);
            list.AddChild(check);
        }
        section.AddBlock($"{prefix}.items", list).WithLiteralTexts(items.Select(i => i.Label).ToArray());
    }
}
