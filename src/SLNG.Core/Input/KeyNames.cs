using System.Globalization;

namespace SLNG.Core.Input;

/// <summary>
/// The canonical names of every key a shortcut can use, and the aliases accepted when parsing text
/// ("Esc", "Return", ",", "PgUp"). The set is deliberately small and fixed: the keys a viewer
/// shortcut can sensibly use, not every scancode. A key outside it cannot be bound, which is also
/// what makes a saved file from a future version (which may know more keys) safe to read: an
/// unknown name is dropped, never guessed at.
///
/// <para>Names are what is persisted and shown in the UI, so they are stable API: renaming one
/// invalidates people's saved bindings.</para>
/// </summary>
public static class KeyNames
{
    private static readonly string[] Canonical = BuildCanonical();

    private static readonly Dictionary<string, string> Lookup = BuildLookup();

    /// <summary>Every canonical key name, in a stable order.</summary>
    public static IReadOnlyList<string> All => Canonical;

    /// <summary>The canonical name for <paramref name="text"/> (any case, or an alias), or null when
    /// it is not a bindable key. A bare modifier ("Ctrl") is not a key.</summary>
    public static string? Canonicalize(string? text) =>
        !string.IsNullOrEmpty(text) && Lookup.TryGetValue(text, out var name) ? name : null;

    public static bool IsKnown(string name) => Canonicalize(name) != null;

    /// <summary>Keys that put a character into a text field: letters, digits, punctuation, Space and
    /// the numeric keypad's digits and operators.</summary>
    public static bool IsPrintable(string canonicalKey)
    {
        if (canonicalKey.Length == 1) return true; // A-Z, 0-9
        return canonicalKey switch
        {
            "Space" or "Comma" or "Period" or "Slash" or "Backslash" or "Backquote" or "Quote"
                or "Semicolon" or "Minus" or "Equal" or "Plus" or "BracketLeft" or "BracketRight"
                or "KpAdd" or "KpSubtract" or "KpMultiply" or "KpDivide" or "KpPeriod" => true,
            _ => canonicalKey.Length == 3 && canonicalKey.StartsWith("Kp", StringComparison.Ordinal)
                 && char.IsDigit(canonicalKey[2]),
        };
    }

    private static string[] BuildCanonical()
    {
        var names = new List<string>();
        for (char c = 'A'; c <= 'Z'; c++) names.Add(c.ToString());
        for (char c = '0'; c <= '9'; c++) names.Add(c.ToString());
        for (int i = 1; i <= 12; i++) names.Add("F" + i.ToString(CultureInfo.InvariantCulture));
        names.AddRange(new[]
        {
            "Up", "Down", "Left", "Right", "PageUp", "PageDown", "Home", "End", "Insert", "Delete",
            "Backspace", "Enter", "Tab", "Space", "Escape",
            "Comma", "Period", "Slash", "Backslash", "Backquote", "Quote", "Semicolon",
            "Minus", "Equal", "Plus", "BracketLeft", "BracketRight",
            "KpAdd", "KpSubtract", "KpMultiply", "KpDivide", "KpEnter", "KpPeriod",
        });
        for (int i = 0; i <= 9; i++) names.Add("Kp" + i.ToString(CultureInfo.InvariantCulture));
        return names.ToArray();
    }

    private static Dictionary<string, string> BuildLookup()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in Canonical) map[name] = name;

        // Aliases: what a person (or the Second Life wiki) writes for the same key.
        var aliases = new (string Alias, string Name)[]
        {
            ("Esc", "Escape"), ("Return", "Enter"), ("Ret", "Enter"),
            ("PgUp", "PageUp"), ("PgDn", "PageDown"), ("PgDown", "PageDown"),
            ("Del", "Delete"), ("Ins", "Insert"), ("Bksp", "Backspace"),
            ("Spacebar", "Space"),
            (",", "Comma"), (".", "Period"), ("/", "Slash"), ("\\", "Backslash"), ("`", "Backquote"),
            ("'", "Quote"), (";", "Semicolon"), ("-", "Minus"), ("=", "Equal"), ("+", "Plus"),
            ("[", "BracketLeft"), ("]", "BracketRight"),
            ("Num+", "KpAdd"), ("Num-", "KpSubtract"), ("Num*", "KpMultiply"), ("Num/", "KpDivide"),
            ("NumEnter", "KpEnter"),
        };
        foreach (var (alias, name) in aliases) map[alias] = name;
        return map;
    }
}
