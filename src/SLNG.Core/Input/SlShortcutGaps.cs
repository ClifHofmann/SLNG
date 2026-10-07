namespace SLNG.Core.Input;

/// <summary>
/// The reference viewer's shortcuts that SLNG has NO binding for yet. They are listed here, in one
/// place, so "why does Ctrl+B do nothing" has an answer, so nobody binds a chord to a stub that
/// pretends, and - the point of <see cref="Gap.Feature"/> - so that whoever builds the feature that
/// makes a chord meaningful finds it here and binds it.
///
/// <para><b>How a gap closes.</b> Every entry names the ROADMAP ticket that must bind it. When that
/// ticket is built: add the action (see the recipe in <c>docs/specs/FEAT-UI-43-keybindings.md</c>),
/// delete the entry from this list. A test (<c>SlShortcutGapTests</c>) fails if a gap's ticket is
/// missing from <c>docs/ROADMAP.md</c>, or is already ✅ Done - a finished feature whose shortcut is
/// still a "gap" is exactly what this list exists to catch.</para>
///
/// <para>The entries are not in the catalog, so they are not rebindable and not shown as actions; the
/// Keyboard page and the manual say they exist. Each chord is verified against <c>menu_viewer.xml</c> /
/// <c>menu_edit.xml</c> / <c>key_bindings.xml</c> of the vendored viewer (a test does it when
/// <c>scratch/slviewer</c> is present). The Linden-only Admin menu (Ctrl+Alt+Shift+O/L/I/C/Del ...)
/// is deliberately absent: it is never to be built. Ctrl+Alt+T ("Highlight Transparent" in the viewer)
/// is also absent because SLNG's own developer tool owns that chord, see <see cref="KeyActions.SlngExtras"/>.</para>
/// </summary>
public static class SlShortcutGaps
{
    /// <param name="Chord">The viewer's chord, in <see cref="KeyChord"/> text form.</param>
    /// <param name="SlName">What the viewer calls the command.</param>
    /// <param name="Why">Why SLNG does not bind it today.</param>
    /// <param name="Feature">The ROADMAP ticket id that, when built, must bind this chord.</param>
    public readonly record struct Gap(string Chord, string SlName, string Why, string Feature);

    public static IReadOnlyList<Gap> All { get; } = new Gap[]
    {
        // ---- movement and camera
        new("Shift+A", "Slide left (third person)", "SLNG has no strafing: A/D turn the avatar and no sideways movement is sent. Today Shift+A still turns (Shift is the run modifier), so the movement actions' IgnoreExtraShift must go when this is built.", "FEAT-AVATAR-04"),
        new("Shift+D", "Slide right (third person)", "Same.", "FEAT-AVATAR-04"),
        new("Shift+Left", "Slide left (third person)", "Same.", "FEAT-AVATAR-04"),
        new("Shift+Right", "Slide right (third person)", "Same.", "FEAT-AVATAR-04"),
        new("Space", "Stop moving", "There is no auto-walk to stop.", "FEAT-AVATAR-04"),
        new("M", "Mouselook", "SLNG has no first-person / mouselook camera yet.", "FEAT-RENDER-23"),
        new("Alt+Shift+F", "Joystick flycam", "No joystick or flycam support (and, since FEAT-UI-43, a gamepad no longer walks the avatar).", "FEAT-UI-50"),
        new("Ctrl+\\", "Look at last chatter", "SLNG does not track who spoke last.", "FEAT-UI-46"),

        // ---- chat bar
        new("Shift+Enter", "Whisper (chat bar)", "The chat bar has only say; no whisper or shout.", "FEAT-UI-45"),
        new("Ctrl+Enter", "Shout (chat bar)", "Same.", "FEAT-UI-45"),
        new("Ctrl+Up", "Recall previous chat input", "The chat bar keeps no input history.", "FEAT-UI-45"),
        new("Ctrl+Down", "Recall next chat input", "Same.", "FEAT-UI-45"),

        // ---- windows
        new("Ctrl+Shift+I", "New inventory window", "One inventory window only.", "FEAT-UI-47"),
        new("Ctrl+G", "Gestures", "No gestures window.", "FEAT-UI-48"),
        new("Ctrl+F", "Search", "No in-viewer search.", "FEAT-UI-49"),
        new("Ctrl+Shift+A", "Nearby people", "The nearby list is part of the mini-map window (the radar); Ctrl+Shift+M opens it. A second chord for the same window is not wired yet.", "FEAT-UI-44"),
        new("Ctrl+Shift+H", "Teleport home", "SLNG can teleport home (the region-restart window offers it) but has no Teleport Home command to bind.", "FEAT-UI-44"),
        new("Ctrl+Alt+Shift+R", "Set UI size to default", "The UI scale exists (Preferences > Display) but has no reset command.", "FEAT-UI-44"),
        new("Ctrl+Alt+Q", "Develop menu", "SLNG's Developer menu is always visible; the viewer hides its Develop menu behind this chord. Its debug consoles (Ctrl+Shift+3 / 4 / 5) are not built either.", "FEAT-UI-53"),
        new("Ctrl+Shift+2", "Scene load statistics", "No such window.", "FEAT-PERF-10"),
        new("Ctrl+`", "Snapshot to disk", "The Snapshot window saves one fixed PNG path; a one-key save needs the destination options.", "FEAT-UI-17"),

        // ---- building
        new("Ctrl+Shift+L", "Unlink", "Linking exists, as a button in the Edit window (FEAT-UI-05), but has no shortcut yet.", "FEAT-UI-44"),
        new("Ctrl+B", "Build", "The Edit window opens from an object's context menu; there is no build mode to toggle.", "FEAT-UI-51"),
        new("Ctrl+1", "Focus tool", "No build tool modes (Focus, Move, Edit, Create on Ctrl+1 .. Ctrl+4).", "FEAT-UI-51"),
        new("Ctrl+5", "Land tool", "No land editing yet.", "MVP4-2"),
        new("Ctrl+.", "Select next part or face", "No part-by-part selection by key.", "FEAT-UI-51"),
        new("Ctrl+,", "Select previous part or face", "Same.", "FEAT-UI-51"),
        new("H", "Focus on selection", "No focus-on-selection command.", "FEAT-UI-51"),
        new("Shift+H", "Zoom to selection", "Same.", "FEAT-UI-51"),
        new("G", "Snap to grid", "Grid snapping is set in the edit window, with no toggle command.", "FEAT-UI-51"),
        new("Shift+X", "Snap object XY to grid", "Same.", "FEAT-UI-51"),
        new("Shift+G", "Use selection for grid", "Same.", "FEAT-UI-51"),
        new("Ctrl+Shift+B", "Grid options", "Same.", "FEAT-UI-51"),
        new("Ctrl+Z", "Undo (objects)", "No object undo; Ctrl+Z inside a text field is the field's own.", "FEAT-UI-52"),
        new("Ctrl+Y", "Redo (objects)", "Same.", "FEAT-UI-52"),
        new("Ctrl+D", "Duplicate", "No duplicate command.", "FEAT-UI-52"),
        new("Ctrl+E", "Deselect", "Deselecting is a click on empty ground; there is no command to bind.", "FEAT-UI-52"),
        new("Ctrl+A", "Select all", "No object select-all; inside a text field it is the field's own.", "FEAT-UI-52"),
        new("Delete", "Delete selection", "The context menu's Delete is a stub; nothing to bind.", "FEAT-UI-52"),
        new("Alt+Shift+R", "Remove selected attachments", "Detach is in the Avatar menu, the inventory and the right-click menu; none works on a selection.", "FEAT-UI-52"),
        new("Ctrl+U", "Upload image", "No upload dialog.", "MVP6-5"),
        new("Ctrl+Alt+U", "Upload model", "No upload dialog.", "MVP6-5"),

        // ---- environment, overlays, sound, rendering toggles
        new("Ctrl+Shift+Y", "Sun: midday", "Time of day is set in the Environment window; no one-key presets.", "FEAT-ENV-04"),
        new("Ctrl+Shift+N", "Sun: sunset", "Same.", "FEAT-ENV-04"),
        new("Ctrl+Shift+O", "Sun: sunrise", "Same.", "FEAT-ENV-04"),
        new("Ctrl+Shift+Z", "Sun: midnight", "Same.", "FEAT-ENV-04"),
        new("Ctrl+Shift+X", "Use the shared (region) environment", "The Environment window has a 'use region setting' button but no command to bind.", "FEAT-ENV-04"),
        new("Ctrl+Alt+Shift+N", "Show beacons", "No beacons.", "FEAT-RENDER-24"),
        new("Ctrl+Alt+Shift+P", "Show property lines", "No property-line overlay.", "FEAT-RENDER-24"),
        new("Ctrl+Alt+Shift+M", "Mute / unmute sound", "SLNG plays no sound; the top-bar speaker button is a placeholder.", "FEAT-AUDIO-01"),
        new("Ctrl+Alt+Shift+=", "Hide particles", "No master switch for particles.", "FEAT-RENDER-25"),
        new("Ctrl+Alt+Shift+1", "Rendering-type toggles (Simple, Alpha, Tree, Avatars ... Ctrl+Alt+Shift+1 to \\)", "No per-type render switches.", "FEAT-RENDER-25"),
        new("Ctrl+Alt+F1", "Rendering-feature toggles (UI, Selected, Highlighted ... Ctrl+Alt+F1 to F9)", "No per-feature render switches.", "FEAT-RENDER-25"),
    };
}
