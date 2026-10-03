namespace SLNG.Core.Input;

/// <summary>
/// The reference viewer's shortcuts that SLNG has NO feature for. They are listed here, in one
/// place, so "why does Ctrl+B do nothing" has an answer and so nobody later binds a chord to a
/// stub that pretends. They are not in the catalog, so they are not rebindable and not shown on the
/// Keyboard page as actions; the page lists them as "not available yet".
///
/// <para>Each entry's chord is verified against <c>menu_viewer.xml</c> / <c>key_bindings.xml</c>
/// in the vendored viewer (the wiki and the source agree on all of these except where a comment
/// says otherwise). Developer and Admin menu entries (Ctrl+Alt+Q Develop menu, the Ctrl+Alt+Shift
/// rendering-type toggles, the consoles, the Linden-only admin chords) are deliberately not
/// listed one by one: SLNG has neither menu.</para>
/// </summary>
public static class SlShortcutGaps
{
    public readonly record struct Gap(string Chord, string SlName, string Why);

    public static IReadOnlyList<Gap> All { get; } = new Gap[]
    {
        new("Shift+A", "Slide left (third person)", "SLNG has no strafing: A/D turn the avatar and no sideways movement is sent."),
        new("Shift+D", "Slide right (third person)", "Same."),
        new("Shift+Left", "Slide left (third person)", "Same."),
        new("Shift+Right", "Slide right (third person)", "Same."),
        new("Space", "Stop moving", "No such command."),
        new("M", "Mouselook", "SLNG has no first-person / mouselook camera yet."),
        new("Alt+Shift+F", "Joystick flycam", "No joystick or flycam support."),
        new("Ctrl+\\", "Look at last chatter", "SLNG does not track who spoke last."),
        new("Shift+Enter", "Whisper (chat bar)", "The chat bar has only say; no whisper or shout."),
        new("Ctrl+Enter", "Shout (chat bar)", "Same."),
        new("Ctrl+Up", "Recall previous chat input", "The chat bar keeps no input history."),
        new("Ctrl+Down", "Recall next chat input", "Same."),
        new("Ctrl+Shift+I", "New inventory window", "One inventory window only."),
        new("Ctrl+G", "Gestures", "No gestures window."),
        new("Ctrl+F", "Search", "No in-viewer search."),
        new("Ctrl+Shift+A", "Nearby people", "The nearby list is part of the mini-map window; Ctrl+Shift+M opens it."),
        new("Ctrl+Shift+H", "Teleport home", "SLNG can teleport home (the region-restart window offers it) but has no Teleport Home command to bind."),
        new("Ctrl+B", "Build", "The edit window opens from an object's context menu, not as a tool mode."),
        new("Ctrl+1", "Focus tool", "No build tool modes (Ctrl+1 .. Ctrl+5)."),
        new("Ctrl+L", "Link", "Linking is not available."),
        new("Ctrl+Shift+L", "Unlink", "Linking is not available."),
        new("Ctrl+Z", "Undo (objects)", "No object undo; Ctrl+Z inside a text field is the field's own."),
        new("Ctrl+Y", "Redo (objects)", "Same."),
        new("Ctrl+D", "Duplicate", "Not available as a shortcut."),
        new("Ctrl+E", "Deselect", "Not available as a shortcut."),
        new("Ctrl+A", "Select all", "No object select-all; inside a text field it is the field's own."),
        new("Delete", "Delete selection", "Not available as a shortcut."),
        new("Ctrl+.", "Select next part or face", "No part-by-part selection."),
        new("Ctrl+,", "Select previous part or face", "No part-by-part selection."),
        new("H", "Focus on selection", "Not available."),
        new("G", "Snap to grid", "Grid snapping is set in the edit window."),
        new("Ctrl+U", "Upload image", "No upload dialog."),
        new("Ctrl+Shift+Y", "Sun: midday", "Time of day is set in the Environment window."),
        new("Ctrl+Shift+N", "Sun: sunset", "Same."),
        new("Ctrl+Shift+O", "Sun: sunrise", "Same."),
        new("Ctrl+Shift+Z", "Sun: midnight", "Same."),
        new("Ctrl+Alt+Shift+N", "Show beacons", "No beacons."),
        new("Ctrl+Alt+Shift+P", "Show property lines", "No property-line overlay."),
        new("Ctrl+Alt+Shift+M", "Mute / unmute sound", "SLNG plays no sound."),
        new("Ctrl+Shift+2", "Scene load statistics", "No such window."),
        new("Ctrl+`", "Snapshot to disk", "Use the Snapshot window (Ctrl+Shift+S)."),
        new("Alt+Shift+R", "Remove selected attachments", "Detach is in the Avatar menu and the inventory."),
        new("Ctrl+Alt+Q", "Develop menu", "SLNG has no Develop/Admin menu; its developer tools are the F-keys and the Developer menu."),
    };
}
