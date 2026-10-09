using LibreMetaverse;

namespace SLNG.Net;

/// <summary>Where a group's name for a chat line came from (BUG-UI-23). Reported in the one-line diagnostic
/// per new group session, so a wrong tab title can be traced to its source without a debugger.</summary>
internal enum GroupNameSource
{
    /// <summary>No source knew it; a name request goes out and the tab is re-titled when the answer arrives.</summary>
    None,
    /// <summary>The agent's group membership list (<c>AgentGroupDataUpdate</c>) -- authoritative, always preferred.</summary>
    Membership,
    /// <summary>The message's binary bucket, which the reference viewer uses as the session name
    /// (<c>LLViewerChatterBoxInvitation::post</c>, llimview.cpp:4249 and 4264).</summary>
    Bucket,
    /// <summary>The shared name cache: an earlier name reply, a profile, or a bucket remembered from an earlier line.</summary>
    NameCache,
}

/// <summary>The pure decisions behind group chat on <see cref="GridSession"/> (BUG-UI-23): which sessions are
/// group chat, which name a group gets, and whether a line is the user's muted group's. Kept free of any
/// connection so each is unit-tested on its own; <c>GridSession.OnInstantMessage</c> only gathers the inputs.
///
/// <para>The reference viewer does NOT name a group tab after the speaker: <c>LLIMMgr::addMessage</c> takes
/// the session name from the binary bucket and falls back to the speaker only for an ad-hoc session
/// (llimview.cpp:3172-3178); a session id that is one of the agent's groups is a group session
/// (<c>LLIMSession</c> constructor, llimview.cpp:764). The speaker's name -- <c>FromAgentName</c> on both the
/// UDP message and the <c>ChatterBoxInvitation</c> event (<c>from_name</c>) -- is therefore never a group name here.</para></summary>
internal static class GroupChatSessionLogic
{
    /// <summary>What <c>GridSession.OnGroupNamesReply</c> stores when the grid answers a name request with an
    /// empty name. It is a placeholder for object-owner displays, not a name: nothing in group chat may treat it
    /// as one, or the real name is never learnt (the cache then "has" the group).</summary>
    internal const string UnknownGroupName = "(unknown group)";

    /// <summary>The longest bucket read as a session name. Group names are 35 characters; this is generous
    /// without accepting a real binary payload (an IM bucket is at most 1024 bytes).</summary>
    internal const int MaxSessionNameBytes = 1024;

    /// <summary>True for a name worth showing: not blank and not the "(unknown group)" placeholder.</summary>
    internal static bool IsRealName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && !string.Equals(name, UnknownGroupName, StringComparison.Ordinal);

    /// <summary>The session name in a message's binary bucket: UTF-8 up to the FIRST NUL, the way the viewer reads
    /// it (<c>std::string((char*)&amp;bin_bucket[0])</c>, llimview.cpp:4264). Anything after that NUL is not part
    /// of the name -- the previous decoder rejected such a bucket outright. Empty when there is no text, it holds a
    /// control character, or it is a single character (the viewer ignores a name of one character or less,
    /// llimview.cpp:3175).</summary>
    internal static string DecodeSessionName(byte[]? bucket)
    {
        if (bucket == null || bucket.Length == 0 || bucket.Length > MaxSessionNameBytes) return string.Empty;
        int end = Array.IndexOf(bucket, (byte)0);
        if (end < 0) end = bucket.Length;
        if (end == 0) return string.Empty;
        string text = System.Text.Encoding.UTF8.GetString(bucket, 0, end).Trim();
        if (text.Length <= 1) return string.Empty;
        return text.Any(char.IsControl) ? string.Empty : text;
    }

    /// <summary>True for the dialogs a session line (or the invitation to one) arrives as: <c>SessionAdd</c> (13,
    /// the viewer's <c>IM_SESSION_INVITE</c>), <c>SessionGroupStart</c> (15), <c>SessionSend</c> (17), and
    /// <c>MessageFromAgent</c> (0, e.g. group chat lines arriving as plain IM dialogs, BUG-UI-37). Not
    /// <c>SessionCardlessStart</c> (16, a conference being started) and not the typing indicators.</summary>
    internal static bool IsSessionLineDialog(InstantMessageDialog dialog) =>
        dialog is InstantMessageDialog.SessionAdd or InstantMessageDialog.SessionGroupStart or InstantMessageDialog.SessionSend or InstantMessageDialog.MessageFromAgent;

    /// <summary>Whether a session message is group chat.
    ///
    /// <para>A session id that is one of the agent's groups IS group chat, whatever LibreMetaverse's own
    /// <c>GroupChatSessions</c> table says. That table forgets a session the moment we leave it
    /// (<c>RequestLeaveGroupChat</c> removes it), yet the simulator can still deliver lines of it -- already in
    /// flight, or a leave it did not honour. Such a line carries no <c>FromGroup</c> flag, so judging by the table
    /// alone sent it to the ad-hoc conference path, where it opened a tab for a group the user had muted.</para>
    ///
    /// <para>Otherwise the old rule: the message's own group flag, or a session LibreMetaverse tracks while the
    /// membership list has not arrived yet (nothing can be ruled out then -- a group line in an odd tab beats a
    /// lost one), or tracks and is in the list.</para></summary>
    internal static bool IsGroupSession(
        InstantMessageDialog dialog, bool groupFlag, bool libreMetaverseTracksSession, bool membershipLoaded, bool inMembership)
    {
        if (inMembership && IsSessionLineDialog(dialog)) return true;
        if (groupFlag) return true;
        return libreMetaverseTracksSession && (!membershipLoaded || inMembership);
    }

    /// <summary>Picks a group's name: the membership list first, then the message's bucket, then whatever the shared
    /// name cache holds. A blank name or the "(unknown group)" placeholder is skipped at every step. Returns
    /// <see cref="GroupNameSource.None"/> and an empty string when nothing knows it.</summary>
    internal static (string Name, GroupNameSource Source) ChooseName(
        string? membershipName, string? bucketName, string? cachedName)
    {
        if (IsRealName(membershipName)) return (membershipName!.Trim(), GroupNameSource.Membership);
        if (IsRealName(bucketName)) return (bucketName!.Trim(), GroupNameSource.Bucket);
        if (IsRealName(cachedName)) return (cachedName!.Trim(), GroupNameSource.NameCache);
        return (string.Empty, GroupNameSource.None);
    }

    /// <summary>True when a line must be consumed silently because its group's chat is switched off
    /// (FEAT-UI-54). Decided by the session id alone -- never by whether LibreMetaverse or the membership list
    /// calls it group chat -- so a muted group stays silent on every path: the invitation that opens the session,
    /// a line over UDP, and a stray line after we left the session.</summary>
    internal static bool ShouldConsumeAsIgnored(InstantMessageDialog dialog, bool hasText, bool groupMuted) =>
        groupMuted && hasText && IsSessionLineDialog(dialog);
}

/// <summary>At most one name request per group per <see cref="MinInterval"/>, so a busy group with no known name
/// is not asked for on every line, while a lost or slow answer is still retried by a later line (BUG-UI-23).</summary>
internal sealed class GroupNameRequestGate
{
    internal static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(30);

    private readonly Dictionary<Guid, long> _lastTicks = new();
    private readonly object _lock = new();

    /// <summary>True when a request should go out now for <paramref name="groupId"/>; records it.</summary>
    internal bool ShouldRequest(Guid groupId, long nowTicks)
    {
        lock (_lock)
        {
            if (_lastTicks.TryGetValue(groupId, out var last) && nowTicks - last < MinInterval.Ticks) return false;
            _lastTicks[groupId] = nowTicks;
            return true;
        }
    }
}
