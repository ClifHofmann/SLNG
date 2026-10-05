namespace SLNG.Core;

/// <summary>
/// Whether a line that arrives in a conversation counts as unread (FEAT-UI-66).
/// </summary>
public static class ChatUnreadPolicy
{
    /// <summary>A line counts when it is the kind that counts (a presence line, for one, never does) and nobody is looking
    /// at it: it is for a conversation other than the selected one, or it is for the selected one while the window is
    /// open on another page (Friends, Groups, Recent) so that the conversation is selected but not on screen.
    /// A closed or minimized window is deliberately not counted for the selected conversation -- that would make nearby
    /// chat raise the toolbar's badge all the time.</summary>
    /// <param name="lineCounts">The line is of a kind that can be unread.</param>
    /// <param name="isSelectedConversation">The line is for the conversation selected on the Chat page.</param>
    /// <param name="windowOpenOnAnotherPage">The window is open and showing a page other than Chat.</param>
    public static bool CountsAsUnread(bool lineCounts, bool isSelectedConversation, bool windowOpenOnAnotherPage) =>
        lineCounts && (!isSelectedConversation || windowOpenOnAnotherPage);
}
