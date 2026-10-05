using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>
/// FEAT-UI-66: when a line counts as unread. The selected conversation counts as read as it arrives -- except while the
/// window is open on another page (Friends...), where nobody sees it, and the Chat tab's badge has to say so.
/// </summary>
public class ChatUnreadPolicyTests
{
    [Theory]
    // lineCounts, selected, windowOpenOnAnotherPage, expected
    [InlineData(true, false, false, true)]   // another conversation: unread
    [InlineData(true, false, true, true)]    // ... also with the Friends page open
    [InlineData(true, true, false, false)]   // the selected one on screen: read as it arrives
    [InlineData(true, true, true, true)]     // the selected one behind the Friends page: unread (the new rule)
    [InlineData(false, false, false, false)] // a line of a kind that never counts (a presence line)
    [InlineData(false, false, true, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, true, false)]
    public void TruthTable(bool lineCounts, bool selected, bool otherPage, bool expected)
    {
        Assert.Equal(expected, ChatUnreadPolicy.CountsAsUnread(lineCounts, selected, otherPage));
    }
}
