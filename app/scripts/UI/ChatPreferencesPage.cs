using SLNG.App.UI.Prefs;

namespace SLNG.App.UI;

/// <summary>
/// The "Conversations" and "Friends online" sections of the Chat &amp; IM category (FEAT-UI-79; FEAT-UI-69): how a
/// conference chat is shown, and how the viewer tells you a friend came online or went offline. Bound to
/// <see cref="UiSettings"/>. The long explanations stay tooltips.
/// </summary>
public sealed class ChatPreferencesPage
{
    /// <summary>Fills <paramref name="conversations"/> and <paramref name="friendsOnline"/> (Chat &amp; IM), which Boot has
    /// already put in their places.</summary>
    public void Build(PrefsSection conversations, PrefsSection friendsOnline, UiSettings settings)
    {
        conversations.AddCheck($"{conversations.CategoryId}.{conversations.Id}.conference_as_im", "ui.preferences.conference_as_im",
                settings.ConferenceChatsAsIm, on => settings.SetConferenceChatsAsIm(on))
            .WithTooltip("ui.preferences.conference_as_im_tip");

        string friends = $"{friendsOnline.CategoryId}.{friendsOnline.Id}";
        friendsOnline.AddCheck($"{friends}.friend_presence_toasts", "ui.preferences.friend_presence_toasts",
                settings.ShowFriendPresenceToasts, on => settings.SetShowFriendPresenceToasts(on))
            .WithTooltip("ui.preferences.friend_presence_toasts_tip")
            .WithKeywords("ChatOnlineNotification");

        friendsOnline.AddCheck($"{friends}.friend_presence_in_local_chat", "ui.preferences.friend_presence_in_local_chat",
                settings.ShowFriendPresenceInLocalChat, on => settings.SetShowFriendPresenceInLocalChat(on))
            .WithTooltip("ui.preferences.friend_presence_in_local_chat_tip");
    }
}
