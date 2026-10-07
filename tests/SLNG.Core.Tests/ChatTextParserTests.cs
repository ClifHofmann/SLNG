using System;
using Xunit;

namespace SLNG.Core.Tests;

public class ChatTextParserTests
{
    [Fact]
    public void EscapeBbCode_EscapesSquareBrackets()
    {
        string input = "Hello [color=red]World[/color] [test]";
        string escaped = ChatTextParser.EscapeBbCode(input);
        Assert.Equal("Hello [lb]color=red]World[lb]/color] [lb]test]", escaped);
    }

    [Fact]
    public void ReplaceEmojiShortcodes_ReplacesShortcodesAndSmilies()
    {
        string input = "Great job :thumbsup: and :heart: :)";
        string result = ChatTextParser.ReplaceEmojiShortcodes(input);
        Assert.Equal("Great job 👍 and ❤️ 😊", result);
    }

    [Fact]
    public void FormatMessageToBbCode_FormatsWebUrlWithTrailingPunctuation()
    {
        string input = "Check out https://secondlife.com/destinations, it is cool!";
        string bbcode = ChatTextParser.FormatMessageToBbCode(input);

        Assert.Contains("[url=https://secondlife.com/destinations][color=#7ec0ee]https://secondlife.com/destinations[/color][/url],", bbcode);
        Assert.DoesNotContain("destinations,", bbcode.Substring(bbcode.IndexOf("[url=") + 5, 33));
    }

    [Fact]
    public void FormatMessageToBbCode_FormatsSlurlTeleport()
    {
        string input = "Meet me at secondlife:///app/teleport/Achel/128/128/25 now!";
        string bbcode = ChatTextParser.FormatMessageToBbCode(input);

        Assert.Contains("[url=secondlife:///app/teleport/Achel/128/128/25][color=#7ec0ee][Teleport: Achel (128, 128, 25)][/color][/url]", bbcode);
    }

    [Fact]
    public void FormatMessageToBbCode_FormatsSlurlAgent()
    {
        var id = Guid.NewGuid();
        string input = $"Profile: secondlife:///app/agent/{id}/about";
        string bbcode = ChatTextParser.FormatMessageToBbCode(input);

        Assert.Contains($"[url=avatar:{id}][color=#7ec0ee]@{id}[/color][/url]", bbcode);
    }

    [Fact]
    public void FormatMessageToBbCode_FormatsMentionsForCurrentUserInGold()
    {
        string input = "Hey @JohnDoe and @Susannah!";
        string bbcode = ChatTextParser.FormatMessageToBbCode(
            input,
            currentUserName: "JohnDoe",
            currentDisplayName: "John");

        // JohnDoe is current user -> gold #ffd700
        Assert.Contains("[color=#ffd700][b]@JohnDoe[/b][/color]", bbcode);
        // Susannah is someone else -> cyan #00e5ff
        Assert.Contains("[color=#00e5ff][b]@Susannah[/b][/color]", bbcode);
    }

    [Fact]
    public void FormatMessageToBbCode_FormatsMentionsWithResolvedAvatarLink()
    {
        var targetId = Guid.NewGuid();
        string input = "Say hi to @Sina!";
        string bbcode = ChatTextParser.FormatMessageToBbCode(
            input,
            currentUserName: "Me",
            currentDisplayName: "Me",
            resolveMention: name => name.Equals("Sina", StringComparison.OrdinalIgnoreCase)
                ? (targetId, "Sina Resident")
                : null);

        Assert.Contains($"[url=avatar:{targetId}][color=#00e5ff][b]@Sina Resident[/b][/color][/url]", bbcode);
    }

    [Fact]
    public void FormatMessageToBbCode_EscapesUserInputBracketsWhileKeepingTags()
    {
        string input = "Check [b]bold[/b] and https://example.com";
        string bbcode = ChatTextParser.FormatMessageToBbCode(input);

        Assert.Contains("[lb]b]bold[lb]/b]", bbcode);
        Assert.Contains("[url=https://example.com]", bbcode);
    }
}
