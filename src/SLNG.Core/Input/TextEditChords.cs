namespace SLNG.Core.Input;

/// <summary>
/// The chords a focused text field keeps for itself. While the person is typing, a shortcut on one
/// of these never fires: Ctrl+C in the chat bar copies TEXT whatever the inventory's Ctrl+C is bound
/// to, and a bare letter types a letter however it is bound.
///
/// <para>Everything else - a Ctrl+I, an F-key, an Alt+Shift chord - is a command, not text, and
/// still fires from a text field (as every menu shortcut does in the reference viewer). The one
/// grey area is AltGr, which Windows reports as Ctrl+Alt and which types characters on some layouts
/// (@ on a German keyboard is AltGr+Q). A Ctrl+Alt+letter chord is therefore NOT claimed here: that
/// keeps today's Ctrl+Alt+R and Ctrl+Alt+T working while typing, at the price that binding an
/// <see cref="KeyContext.Always"/> action to a Ctrl+Alt+letter that is also an AltGr character
/// fires it while typing that character. The Keyboard page cannot know the layout; it says so.</para>
/// </summary>
public static class TextEditChords
{
    /// <summary>Whether a focused text field consumes <paramref name="chord"/> for editing.</summary>
    public static bool Owns(KeyChord chord)
    {
        if (chord.Alt) return false;

        if (!chord.Ctrl)
        {
            // Plain or Shift-only: typing, caret movement, selection, deletion, newline, Tab.
            return KeyNames.IsPrintable(chord.Key) || chord.Key is
                "Backspace" or "Delete" or "Enter" or "KpEnter" or "Tab" or "Escape" or "Insert"
                or "Left" or "Right" or "Up" or "Down" or "Home" or "End" or "PageUp" or "PageDown";
        }

        // Ctrl (optionally with Shift): the clipboard, undo/redo, select-all and word-wise editing.
        return chord.Key is "A" or "C" or "V" or "X" or "Z" or "Y"
            or "Backspace" or "Delete" or "Insert" or "Left" or "Right" or "Home" or "End";
    }
}
