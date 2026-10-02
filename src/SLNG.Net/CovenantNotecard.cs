using System.Globalization;
using System.Text;

namespace SLNG.Net;

/// <summary>Reads the text out of a notecard asset, which is what an estate covenant is
/// (FEAT-LAND-05). Pure and internal. Follows the reference viewer's <c>LLNotecard::importStream</c>
/// (<c>llnotecard.cpp</c>:127-200) and <c>onCovenantLoadComplete</c> (<c>llviewermessage.cpp</c>:6872).
///
/// <para>Layout of the asset (UTF-8, LF line ends):</para>
/// <code>
/// Linden text version 2          (1 is accepted too)
/// {
/// LLEmbeddedItems version 1
/// {
/// count N
/// { ext char index I / inv_item 0 / { ...inventory item... } }   x N
/// }
/// Text length B                  B = number of BYTES of the text that follows
/// &lt;B bytes of UTF-8&gt;}
/// </code>
/// Embedded inventory items are skipped; the placeholder characters they leave in the text
/// (<c>U+100000 + index</c>, <c>LLTextEditor::FIRST_EMBEDDED_CHAR</c>) are dropped, because a read-only
/// covenant view has no icon to draw there. Version 1 notecards (placeholder bytes 0x80 and up) are read
/// as UTF-8 too; the odd embedded byte becomes a replacement character, which is acceptable for an
/// ancient format.</summary>
internal static class CovenantNotecard
{
    /// <summary>First code point the notecard format uses for an embedded item.</summary>
    private const int FirstEmbeddedChar = 0x100000;

    /// <summary>The notecard's text, or null when the bytes are not a notecard in the layout above. The
    /// viewer treats the same cases as an import error (a length that does not match what follows
    /// included), so a malformed covenant is reported as a failure and never as "no covenant".</summary>
    internal static string? Extract(byte[]? data)
    {
        if (data == null || data.Length == 0) return null;

        int pos = 0;

        if (!NextLine(data, ref pos, out string line) || !TryPrefixedInt(line, "Linden text version ", out int version)
            || version is not (1 or 2))
            return null;

        if (!NextLine(data, ref pos, out line) || line != "{") return null;

        if (!NextLine(data, ref pos, out line) || !TryPrefixedInt(line, "LLEmbeddedItems version ", out int embeddedVersion)
            || embeddedVersion != 1)
            return null;
        if (!NextLine(data, ref pos, out line) || line != "{") return null;
        if (!NextLine(data, ref pos, out line) || !TryPrefixedInt(line, "count ", out int count) || count < 0) return null;

        for (int i = 0; i < count; i++)
            if (!SkipBlock(data, ref pos)) return null;

        if (!NextLine(data, ref pos, out line) || line != "}") return null;

        if (!NextLine(data, ref pos, out line) || !TryPrefixedInt(line, "Text length ", out int length) || length < 0)
            return null;
        if (length > data.Length - pos) return null;

        return StripEmbedded(Encoding.UTF8.GetString(data, pos, length));
    }

    // Reads one line, trimmed (the viewer skips whitespace before each token), skipping blank lines.
    // False at the end of the data.
    private static bool NextLine(byte[] data, ref int pos, out string line)
    {
        while (pos < data.Length)
        {
            int end = Array.IndexOf(data, (byte)'\n', pos);
            int stop = end < 0 ? data.Length : end;
            string s = Encoding.UTF8.GetString(data, pos, stop - pos).Trim();
            pos = end < 0 ? data.Length : end + 1;
            if (s.Length == 0) continue;
            line = s;
            return true;
        }

        line = string.Empty;
        return false;
    }

    // Skips one brace-balanced "{ ... }" block; a brace is only a brace when it is alone on its line, so
    // an item name that contains one cannot unbalance it.
    private static bool SkipBlock(byte[] data, ref int pos)
    {
        if (!NextLine(data, ref pos, out string line) || line != "{") return false;
        int depth = 1;
        while (depth > 0)
        {
            if (!NextLine(data, ref pos, out line)) return false;
            if (line == "{") depth++;
            else if (line == "}") depth--;
        }
        return true;
    }

    private static bool TryPrefixedInt(string line, string prefix, out int value)
    {
        value = 0;
        return line.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(line.AsSpan(prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static string StripEmbedded(string text)
    {
        // Fast path: most covenants have none.
        bool any = false;
        foreach (var rune in text.EnumerateRunes())
            if (rune.Value >= FirstEmbeddedChar) { any = true; break; }
        if (!any) return text;

        var sb = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
            if (rune.Value < FirstEmbeddedChar) sb.Append(rune.ToString());
        return sb.ToString();
    }
}
