using System.Globalization;

namespace SLNG.Core.Input;

/// <summary>The modifier keys a <see cref="KeyChord"/> can carry. Meta (Cmd / the Windows key) is
/// deliberately absent: a chord that needs it is out of scope (macOS is a later goal), and the
/// dispatcher ignores any key event that has it held.</summary>
[Flags]
public enum KeyMods
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,
}

/// <summary>
/// One key plus the modifiers held with it ("Ctrl+Shift+I", "Alt+Left", "F3"). Engine-agnostic: the
/// key is a canonical NAME (<see cref="KeyNames"/>), never a Godot keycode, so this type, its text
/// form and everything saved with it stay stable if the engine's enum is renumbered. The app maps
/// the engine's key to the name at the edge (one table, in <c>app/scripts/Input</c>).
///
/// <para>A chord always has a real key. A modifier on its own ("Ctrl", "Shift+Alt") is not a chord
/// and cannot be parsed or constructed, so it can never be bound.</para>
///
/// <para>The text form is canonical: <c>Ctrl+Alt+Shift+Key</c> in that order, key names as in
/// <see cref="KeyNames"/> ("Comma", not ","). <see cref="TryParse"/> is more forgiving than
/// <see cref="ToString"/> is strict: modifier order, case and a few aliases ("Control", "Esc",
/// "PgUp", ",") are all accepted, and the result always formats back to the canonical form.</para>
/// </summary>
public readonly record struct KeyChord
{
    public string Key { get; }
    public KeyMods Mods { get; }

    public KeyChord(string key, KeyMods mods = KeyMods.None)
    {
        var canonical = KeyNames.Canonicalize(key)
            ?? throw new ArgumentException($"'{key}' is not a bindable key.", nameof(key));
        Key = canonical;
        Mods = mods;
    }

    public bool Ctrl => (Mods & KeyMods.Ctrl) != 0;
    public bool Alt => (Mods & KeyMods.Alt) != 0;
    public bool Shift => (Mods & KeyMods.Shift) != 0;

    /// <summary>Canonical text, e.g. <c>Ctrl+Shift+I</c>. Round-trips through <see cref="TryParse"/>.</summary>
    public override string ToString()
    {
        var text = string.Empty;
        if (Ctrl) text += "Ctrl+";
        if (Alt) text += "Alt+";
        if (Shift) text += "Shift+";
        return text + Key;
    }

    public static KeyChord Parse(string text) =>
        TryParse(text, out var chord) ? chord : throw new FormatException($"'{text}' is not a key chord.");

    /// <summary>Parses "Ctrl+Shift+I", "alt+left", "F3", "Ctrl++" (Ctrl and the plus key).
    /// Returns false for an empty string, an unknown key, a repeated or unknown modifier, or a
    /// modifier with no key. Never throws.</summary>
    public static bool TryParse(string? text, out KeyChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();

        // "+" separates modifiers from the key, and the plus key itself is spelled "+", so a trailing
        // "+" (alone, or right after the separator) is the KEY, not a separator.
        string keyPart;
        string[] modParts;
        if (text == "+")
        {
            keyPart = "+";
            modParts = Array.Empty<string>();
        }
        else if (text.EndsWith("++", StringComparison.Ordinal))
        {
            keyPart = "+";
            var head = text[..^2];
            modParts = head.Length == 0 ? Array.Empty<string>() : head.Split('+');
        }
        else
        {
            var parts = text.Split('+');
            keyPart = parts[^1];
            modParts = parts[..^1];
        }

        var mods = KeyMods.None;
        foreach (var part in modParts)
        {
            var flag = ParseModifier(part.Trim());
            if (flag == KeyMods.None) return false;   // "Foo+A", or an empty piece ("Ctrl++A")
            if ((mods & flag) != 0) return false;     // "Ctrl+Ctrl+A"
            mods |= flag;
        }

        var key = KeyNames.Canonicalize(keyPart.Trim());
        if (key == null) return false;                // also a lone modifier: "Ctrl" is not a key
        chord = new KeyChord(key, mods);
        return true;
    }

    private static KeyMods ParseModifier(string name) =>
        name.ToLower(CultureInfo.InvariantCulture) switch
        {
            "ctrl" or "control" or "ctl" or "strg" => KeyMods.Ctrl,
            "alt" => KeyMods.Alt,
            "shift" => KeyMods.Shift,
            _ => KeyMods.None,
        };

    /// <summary>"Ctrl+A;Alt+Left" form used by the saved file: chords joined by <c>;</c>. An empty
    /// string means "no chord at all" (the action is deliberately unbound).</summary>
    public static string FormatList(IEnumerable<KeyChord> chords) =>
        string.Join(';', chords.Select(c => c.ToString()));

    /// <summary>Inverse of <see cref="FormatList"/>. Entries that do not parse are dropped and
    /// reported through <paramref name="skipped"/>; the empty string gives an empty list.</summary>
    public static List<KeyChord> ParseList(string? text, out int skipped)
    {
        skipped = 0;
        var result = new List<KeyChord>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (var part in text.Split(';'))
        {
            if (string.IsNullOrWhiteSpace(part)) continue;
            if (TryParse(part, out var chord))
            {
                if (!result.Contains(chord)) result.Add(chord);
            }
            else skipped++;
        }
        return result;
    }
}
