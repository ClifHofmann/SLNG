namespace SLNG.Core.Presence;

/// <summary>
/// FEAT-UI-75: which text, if any, answers an incoming instant message. A port of Firestorm's
/// <c>LLIMProcessing::getAutoresponseTextForAvatar</c> together with the guard in front of it
/// (llimprocessing.cpp, the <c>IM_NOTHING_SPECIAL</c> case): the first branch whose mode applies wins, in the order
/// Do Not Disturb, Autorespond non-friends, Autorespond, Away.
/// </summary>
public static class AutoresponsePolicy
{
    /// <summary>The text to send back, or null when this message gets no answer.</summary>
    /// <param name="isMuted">A blocked avatar is never answered (Firestorm only does so on an explicit opt-in we do not offer).</param>
    public static string? Choose(PresenceConfig config, bool isFriend, bool isMuted)
    {
        if (isMuted) return null;

        string? text = null;
        if (config.DoNotDisturb) text = config.BusyMessage;
        else if (config.AutorespondNonFriends && !isFriend) text = config.NonFriendsMessage;
        else if (config.Autorespond) text = config.AutorespondMessage;
        else if (config.Away && config.SendAwayResponse) text = config.AwayMessage;

        // An empty text would go out as an empty IM, which the other side shows as a blank line.
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
