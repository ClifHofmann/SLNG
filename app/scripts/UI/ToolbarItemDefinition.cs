using System;

namespace SLNG.App.UI;

/// <summary>
/// Describes one item that can appear as an icon button in the bottom <see cref="ButtonBar"/>
/// and be shown/hidden from the "Toolbar" tab of <see cref="PreferencesWindow"/>. Adding a new
/// toggleable panel to the bar later means constructing one more of these (see
/// Boot.SetupHud) — nothing in ButtonBar or PreferencesWindow itself needs to change.
/// </summary>
public sealed class ToolbarItemDefinition
{
    /// <summary>Stable identifier persisted in preferences.cfg. Never shown to the user, so it
    /// stays plain ASCII even if Label is localized later.</summary>
    public string Id { get; }

    /// <summary>Human-readable name shown next to the checkbox in Preferences and as the bar
    /// button's tooltip (the bar itself is icon-only, per spec — no caption on the button).</summary>
    public string Label { get; }

    /// <summary>Glyph drawn on the bar button itself.</summary>
    public string IconGlyph { get; }

    /// <summary>Invoked when the bar button is clicked. Must call the target panel's own
    /// show/hide entry point (e.g. InventoryPanel.Toggle()) rather than duplicating visibility
    /// logic here.</summary>
    public Action Toggle { get; }

    /// <summary>Optional: lets the bar button reflect the panel's real current visibility
    /// (e.g. after the user closes it via its own SLNGWindow close button) instead of only
    /// updating in response to its own click.</summary>
    public Func<bool>? IsActive { get; }

    public ToolbarItemDefinition(string id, string label, string iconGlyph, Action toggle, Func<bool>? isActive = null)
    {
        Id = id;
        Label = label;
        IconGlyph = iconGlyph;
        Toggle = toggle;
        IsActive = isActive;
    }
}
