using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace SLNG.Core;

/// <summary>
/// Domain-level text parser for chat and notification messages:
/// - Replaces emoji shortcodes and common emoticons with Unicode characters.
/// - Parses Web URLs (http/https) and Second Life URLs (SLurls) into clickable BBCode links.
/// - Recognizes @ mentions, formatting them with highlights and avatar links.
/// - Escapes raw BBCode markup so user input cannot corrupt the rich display.
/// </summary>
public static partial class ChatTextParser
{
    private static readonly Dictionary<string, string> ShortcodeToEmoji = new(StringComparer.OrdinalIgnoreCase)
    {
        // Smileys & Emotion
        { ":smile:", "😄" },
        { ":smiley:", "😃" },
        { ":grinning:", "😀" },
        { ":blush:", "😊" },
        { ":wink:", "😉" },
        { ":heart_eyes:", "😍" },
        { ":kissing_heart:", "😘" },
        { ":kiss:", "💋" },
        { ":relaxed:", "☺️" },
        { ":yum:", "😋" },
        { ":sunglasses:", "😎" },
        { ":smirk:", "😏" },
        { ":neutral_face:", "😐" },
        { ":expressionless:", "😑" },
        { ":unamused:", "😒" },
        { ":sweat_smile:", "😅" },
        { ":sweat:", "😓" },
        { ":disappointed:", "😞" },
        { ":worried:", "😟" },
        { ":thinking:", "🤔" },
        { ":cry:", "😢" },
        { ":sob:", "😭" },
        { ":joy:", "😂" },
        { ":rofl:", "🤣" },
        { ":scream:", "😱" },
        { ":angry:", "😠" },
        { ":rage:", "😡" },
        { ":skull:", "💀" },
        { ":clown:", "🤡" },
        { ":ghost:", "👻" },
        { ":alien:", "👽" },
        { ":robot:", "🤖" },
        { ":poop:", "💩" },

        // Gestures & Body
        { ":thumbsup:", "👍" },
        { ":+1:", "👍" },
        { ":thumbsdown:", "👎" },
        { ":-1:", "👎" },
        { ":wave:", "👋" },
        { ":clap:", "👏" },
        { ":pray:", "🙏" },
        { ":raised_hands:", "🙌" },
        { ":ok_hand:", "👌" },
        { ":punch:", "👊" },
        { ":fist:", "✊" },
        { ":v:", "✌️" },
        { ":eyes:", "👀" },

        // Hearts & Symbols
        { ":heart:", "❤️" },
        { ":orange_heart:", "🧡" },
        { ":yellow_heart:", "💛" },
        { ":green_heart:", "💚" },
        { ":blue_heart:", "💙" },
        { ":purple_heart:", "💜" },
        { ":broken_heart:", "💔" },
        { ":heartbreak:", "💔" },
        { ":sparkles:", "✨" },
        { ":star:", "⭐" },
        { ":fire:", "🔥" },
        { ":100:", "💯" },
        { ":check:", "✔️" },
        { ":cross:", "❌" },
        { ":warning:", "⚠️" },
        { ":party:", "🎉" },
        { ":tada:", "🎉" },
        { ":rocket:", "🚀" },

        // Food & Objects
        { ":coffee:", "☕" },
        { ":beer:", "🍺" },
        { ":cake:", "🍰" },
        { ":pizza:", "🍕" },
        { ":burger:", "🍔" },
        { ":sun:", "☀️" },
        { ":moon:", "🌙" },
        { ":rainbow:", "🌈" },
        { ":cat:", "🐱" },
        { ":dog:", "🐶" },
    };

    private static readonly (string Pattern, string Replacement)[] EmoticonReplacements =
    {
        ( ":-)", "😊" ),
        ( ":)", "😊" ),
        ( ":-D", "😄" ),
        ( ":D", "😄" ),
        ( ";-)", "😉" ),
        ( ";)", "😉" ),
        ( ":-(", "🙁" ),
        ( ":(", "🙁" ),
        ( ":-P", "😛" ),
        ( ":P", "😛" ),
        ( ":-p", "😛" ),
        ( ":p", "😛" ),
        ( "<3", "❤️" ),
    };

    // Regex to detect Web URLs: http:// or https:// followed by non-whitespace/non-brackets
    private static readonly Regex WebUrlRegex = new(
        @"https?://[^\s<>""]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Regex to detect SLurls: secondlife:///app/...
    private static readonly Regex SlurlRegex = new(
        @"secondlife:///app/(?:agent|group|teleport|worldmap)/[^\s<>""]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Regex to detect @ mentions: @ followed by word characters, dots, or underscores
    private static readonly Regex MentionRegex = new(
        @"(?<=^|[\s\(\[\{])@([a-zA-Z0-9_\.]+)",
        RegexOptions.Compiled);

    /// <summary>
    /// Escapes literal square brackets for Godot RichTextLabel BBCode parser.
    /// </summary>
    public static string EscapeBbCode(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Replace("[", "[lb]");
    }

    /// <summary>
    /// Replaces shortcodes (:smile:) and common ASCII smileys with Unicode emoji characters.
    /// </summary>
    public static string ReplaceEmojiShortcodes(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        // Replace :shortcode: words
        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == ':')
            {
                int end = text.IndexOf(':', i + 1);
                if (end > i + 1 && end - i <= 32)
                {
                    string candidate = text.Substring(i, end - i + 1);
                    if (ShortcodeToEmoji.TryGetValue(candidate, out var emoji))
                    {
                        sb.Append(emoji);
                        i = end + 1;
                        continue;
                    }
                }
            }
            sb.Append(text[i]);
            i++;
        }

        string result = sb.ToString();

        // Replace ASCII emoticons surrounded by boundaries
        foreach (var (pat, rep) in EmoticonReplacements)
        {
            int index = 0;
            while ((index = result.IndexOf(pat, index, StringComparison.Ordinal)) >= 0)
            {
                bool leftOk = index == 0 || char.IsWhiteSpace(result[index - 1]);
                bool rightOk = (index + pat.Length >= result.Length) || char.IsWhiteSpace(result[index + pat.Length]);
                if (leftOk && rightOk)
                {
                    result = result.Remove(index, pat.Length).Insert(index, rep);
                    index += rep.Length;
                }
                else
                {
                    index += pat.Length;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Formats a chat or notification message into BBCode suitable for a Godot RichTextLabel:
    /// - Transforms emoji shortcodes
    /// - Escapes BBCode characters
    /// - Links Web URLs and SLurls
    /// - Highlights @mentions (and colors the current user's mention in bright gold)
    /// </summary>
    public static string FormatMessageToBbCode(
        string rawMessage,
        string? currentUserName = null,
        string? currentDisplayName = null,
        Func<string, (Guid? AvatarId, string DisplayName)?>? resolveMention = null)
    {
        if (string.IsNullOrEmpty(rawMessage)) return string.Empty;

        // 1. Emoji shortcode substitution
        string message = ReplaceEmojiShortcodes(rawMessage);

        // 2. Tokenize into (URL / SLurl / Mention / Text) segments
        var tokens = Tokenize(message);

        // 3. Assemble BBCode
        var sb = new StringBuilder();
        foreach (var token in tokens)
        {
            switch (token.Kind)
            {
                case TokenKind.Text:
                    sb.Append(EscapeBbCode(token.Text));
                    break;

                case TokenKind.WebUrl:
                    string cleanUrl = TrimTrailingPunctuation(token.Text, out string trailing);
                    sb.Append($"[url={cleanUrl}][color=#7ec0ee]{EscapeBbCode(cleanUrl)}[/color][/url]");
                    if (!string.IsNullOrEmpty(trailing))
                        sb.Append(EscapeBbCode(trailing));
                    break;

                case TokenKind.Slurl:
                    string cleanSlurl = TrimTrailingPunctuation(token.Text, out string slurlTrailing);
                    FormatSlurl(sb, cleanSlurl);
                    if (!string.IsNullOrEmpty(slurlTrailing))
                        sb.Append(EscapeBbCode(slurlTrailing));
                    break;

                case TokenKind.Mention:
                    string mentionName = token.Text; // without '@'
                    bool isMe = (!string.IsNullOrEmpty(currentUserName) && string.Equals(mentionName, currentUserName, StringComparison.OrdinalIgnoreCase))
                             || (!string.IsNullOrEmpty(currentDisplayName) && string.Equals(mentionName, currentDisplayName, StringComparison.OrdinalIgnoreCase));

                    var resolved = resolveMention?.Invoke(mentionName);
                    string disp = resolved?.DisplayName ?? mentionName;
                    Guid? avId = resolved?.AvatarId;

                    string color = isMe ? "#ffd700" : "#00e5ff"; // Gold for me, Cyan for others
                    string prefix = isMe ? "[b]" : "[b]";
                    string suffix = isMe ? "[/b]" : "[/b]";

                    if (avId.HasValue && avId.Value != Guid.Empty)
                    {
                        sb.Append($"[url=avatar:{avId.Value}][color={color}]{prefix}@{EscapeBbCode(disp)}{suffix}[/color][/url]");
                    }
                    else
                    {
                        sb.Append($"[color={color}]{prefix}@{EscapeBbCode(disp)}{suffix}[/color]");
                    }
                    break;
            }
        }

        return sb.ToString();
    }

    private static void FormatSlurl(StringBuilder sb, string slurl)
    {
        // secondlife:///app/agent/<id>/about
        // secondlife:///app/agent/<id>/im
        // secondlife:///app/group/<id>/about
        // secondlife:///app/teleport/<region>/<x>/<y>/<z>
        // secondlife:///app/worldmap/<region>/<x>/<y>/<z>
        const string prefix = "secondlife:///app/";
        if (slurl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            string rest = slurl[prefix.Length..];
            string[] parts = rest.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && string.Equals(parts[0], "agent", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(parts[1], out var agentId))
            {
                sb.Append($"[url=avatar:{agentId}][color=#7ec0ee]@{agentId}[/color][/url]");
                return;
            }
            if (parts.Length >= 2 && string.Equals(parts[0], "group", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(parts[1], out var groupId))
            {
                sb.Append($"[url={slurl}][color=#7ec0ee][Group: {groupId}][/color][/url]");
                return;
            }
            if (parts.Length >= 5 && string.Equals(parts[0], "teleport", StringComparison.OrdinalIgnoreCase))
            {
                string region = Uri.UnescapeDataString(parts[1]);
                string x = parts[2], y = parts[3], z = parts[4];
                sb.Append($"[url={slurl}][color=#7ec0ee][Teleport: {EscapeBbCode(region)} ({x}, {y}, {z})][/color][/url]");
                return;
            }
            if (parts.Length >= 5 && string.Equals(parts[0], "worldmap", StringComparison.OrdinalIgnoreCase))
            {
                string region = Uri.UnescapeDataString(parts[1]);
                string x = parts[2], y = parts[3], z = parts[4];
                sb.Append($"[url={slurl}][color=#7ec0ee][Map: {EscapeBbCode(region)} ({x}, {y}, {z})][/color][/url]");
                return;
            }
        }

        // Generic fallback
        sb.Append($"[url={slurl}][color=#7ec0ee]{EscapeBbCode(slurl)}[/color][/url]");
    }

    private static string TrimTrailingPunctuation(string url, out string trailing)
    {
        int end = url.Length;
        while (end > 0)
        {
            char c = url[end - 1];
            if (c == '.' || c == ',' || c == ';' || c == ':' || c == '!' || c == '?' || c == ')' || c == ']' || c == '>')
            {
                end--;
            }
            else
            {
                break;
            }
        }

        trailing = url[end..];
        return url[..end];
    }

    private enum TokenKind
    {
        Text,
        WebUrl,
        Slurl,
        Mention,
    }

    private readonly struct Token
    {
        public TokenKind Kind { get; }
        public string Text { get; }

        public Token(TokenKind kind, string text)
        {
            Kind = kind;
            Text = text;
        }
    }

    private static List<Token> Tokenize(string text)
    {
        var result = new List<Token>();
        int cur = 0;

        // Find matches for WebUrl, Slurl, Mention
        var matches = new List<(int Index, int Length, TokenKind Kind, string Value)>();

        foreach (Match m in SlurlRegex.Matches(text))
        {
            matches.Add((m.Index, m.Length, TokenKind.Slurl, m.Value));
        }

        foreach (Match m in WebUrlRegex.Matches(text))
        {
            // Only add if not overlapping with Slurl
            bool overlaps = false;
            foreach (var match in matches)
            {
                if (m.Index >= match.Index && m.Index < match.Index + match.Length)
                {
                    overlaps = true;
                    break;
                }
            }
            if (!overlaps)
            {
                matches.Add((m.Index, m.Length, TokenKind.WebUrl, m.Value));
            }
        }

        foreach (Match m in MentionRegex.Matches(text))
        {
            bool overlaps = false;
            foreach (var match in matches)
            {
                if (m.Index >= match.Index && m.Index < match.Index + match.Length)
                {
                    overlaps = true;
                    break;
                }
            }
            if (!overlaps)
            {
                // Group 1 has the name without '@'
                string name = m.Groups[1].Value;
                matches.Add((m.Index, m.Length, TokenKind.Mention, name));
            }
        }

        matches.Sort((a, b) => a.Index.CompareTo(b.Index));

        foreach (var m in matches)
        {
            if (m.Index > cur)
            {
                result.Add(new Token(TokenKind.Text, text[cur..m.Index]));
            }
            result.Add(new Token(m.Kind, m.Value));
            cur = m.Index + m.Length;
        }

        if (cur < text.Length)
        {
            result.Add(new Token(TokenKind.Text, text[cur..]));
        }

        return result;
    }
}
