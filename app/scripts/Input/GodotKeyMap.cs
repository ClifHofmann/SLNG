using System.Collections.Generic;
using Godot;
using SLNG.Core.Input;

namespace SLNG.App;

/// <summary>
/// The one place Godot's <see cref="Key"/> meets the engine-agnostic key names of
/// <c>SLNG.Core.Input</c>. Everything past this file speaks <see cref="KeyChord"/>; nothing in the
/// table, the saved file or the Keyboard page knows what a Godot keycode is.
///
/// <para>Matching uses <see cref="InputEventKey.Keycode"/> (the key's label on the person's layout),
/// which is NOT changed by Shift: Shift+1 reports <c>Key1</c>, so "Ctrl+Shift+1" is a stable chord on
/// every layout. A shortcut therefore follows the key's printed letter, not its position - the same
/// choice <c>Boot._Input</c> always made.</para>
/// </summary>
public static class GodotKeyMap
{
    private static readonly Dictionary<Key, string> ToName = BuildToName();
    private static readonly Dictionary<string, Key> ToKey = BuildToKey();

    private static Dictionary<Key, string> BuildToName()
    {
        var map = new Dictionary<Key, string>();
        for (int i = 0; i < 26; i++) map[Key.A + i] = ((char)('A' + i)).ToString();
        for (int i = 0; i < 10; i++) map[Key.Key0 + i] = ((char)('0' + i)).ToString();
        for (int i = 0; i < 12; i++) map[Key.F1 + i] = "F" + (i + 1);
        for (int i = 0; i < 10; i++) map[Key.Kp0 + i] = "Kp" + i;
        map[Key.Up] = "Up";
        map[Key.Down] = "Down";
        map[Key.Left] = "Left";
        map[Key.Right] = "Right";
        map[Key.Pageup] = "PageUp";
        map[Key.Pagedown] = "PageDown";
        map[Key.Home] = "Home";
        map[Key.End] = "End";
        map[Key.Insert] = "Insert";
        map[Key.Delete] = "Delete";
        map[Key.Backspace] = "Backspace";
        map[Key.Enter] = "Enter";
        map[Key.Tab] = "Tab";
        map[Key.Space] = "Space";
        map[Key.Escape] = "Escape";
        map[Key.Comma] = "Comma";
        map[Key.Period] = "Period";
        map[Key.Slash] = "Slash";
        map[Key.Backslash] = "Backslash";
        map[Key.Quoteleft] = "Backquote";
        map[Key.Apostrophe] = "Quote";
        map[Key.Semicolon] = "Semicolon";
        map[Key.Minus] = "Minus";
        map[Key.Equal] = "Equal";
        map[Key.Plus] = "Plus";
        map[Key.Bracketleft] = "BracketLeft";
        map[Key.Bracketright] = "BracketRight";
        map[Key.KpAdd] = "KpAdd";
        map[Key.KpSubtract] = "KpSubtract";
        map[Key.KpMultiply] = "KpMultiply";
        map[Key.KpDivide] = "KpDivide";
        map[Key.KpEnter] = "KpEnter";
        map[Key.KpPeriod] = "KpPeriod";
        return map;
    }

    private static Dictionary<string, Key> BuildToKey()
    {
        var map = new Dictionary<string, Key>(System.StringComparer.Ordinal);
        foreach (var (key, name) in ToName) map[name] = key;
        return map;
    }

    /// <summary>The Godot key a canonical name stands for, or <see cref="Key.None"/> for a name this
    /// build cannot map (which <see cref="KeyNames"/> and this table are tested never to be).</summary>
    public static Key KeyOf(string canonicalName) =>
        ToKey.TryGetValue(canonicalName, out var key) ? key : Key.None;

    /// <summary>The canonical name of a Godot key, or null when it is not a bindable key (a bare
    /// modifier, CapsLock, a media key, ...).</summary>
    public static string? NameOf(Key key) => ToName.TryGetValue(key, out var name) ? name : null;

    /// <summary>The chord a key event stands for. False for a key that cannot be bound, for a bare
    /// modifier press, and for any event with Meta (Cmd / the Windows key) held - Meta chords are out
    /// of scope, and the OS usually owns them anyway.</summary>
    public static bool TryGetChord(InputEventKey e, out KeyChord chord)
    {
        chord = default;
        if (e.MetaPressed) return false;

        var key = e.Keycode != Key.None ? e.Keycode : e.PhysicalKeycode;
        if (!ToName.TryGetValue(key, out var name)) return false;

        var mods = KeyMods.None;
        if (e.CtrlPressed) mods |= KeyMods.Ctrl;
        if (e.AltPressed) mods |= KeyMods.Alt;
        if (e.ShiftPressed) mods |= KeyMods.Shift;
        chord = new KeyChord(name, mods);
        return true;
    }

    /// <summary>A synthetic key-press event for a chord, for the selftest (and nothing else).</summary>
    public static InputEventKey MakeEvent(KeyChord chord, bool pressed = true)
    {
        var key = KeyOf(chord.Key);
        return new InputEventKey
        {
            Keycode = key,
            PhysicalKeycode = key,
            Pressed = pressed,
            CtrlPressed = chord.Ctrl,
            AltPressed = chord.Alt,
            ShiftPressed = chord.Shift,
        };
    }
}
