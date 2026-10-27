namespace SLNG.Core.Presence;

/// <summary>
/// FEAT-UI-75: what the agent has said about its own availability, and what to answer an instant message with.
/// Mirrors Firestorm's status menu (Away, Do Not Disturb, Autorespond, Autorespond to non-friends) and its
/// per-account response texts (<c>DoNotDisturbModeResponse</c>, <c>FSAutorespondModeResponse</c>,
/// <c>FSAutorespondNonFriendsResponse</c>, <c>FSAwayAvatarResponse</c>).
/// </summary>
/// <param name="Away">AFK: the control flag the sim shows as "(Away)" and the away animation.</param>
/// <param name="DoNotDisturb">Do Not Disturb (the old "Busy").</param>
/// <param name="Autorespond">Answer every first IM.</param>
/// <param name="AutorespondNonFriends">Answer a first IM from someone who is not a friend.</param>
/// <param name="SendAwayResponse">Whether Away alone also answers (<c>FSSendAwayAvatarResponse</c>, off by default).</param>
public sealed record PresenceConfig(
    bool Away = false,
    bool DoNotDisturb = false,
    bool Autorespond = false,
    bool AutorespondNonFriends = false,
    bool SendAwayResponse = false,
    string AwayMessage = PresenceConfig.DefaultAwayMessage,
    string BusyMessage = PresenceConfig.DefaultBusyMessage,
    string AutorespondMessage = PresenceConfig.DefaultAutorespondMessage,
    string NonFriendsMessage = PresenceConfig.DefaultAutorespondMessage)
{
    // The viewers' own default texts (settings_per_account.xml of Firestorm / strings.xml of the reference viewer).
    public const string DefaultAwayMessage =
        "The Resident you messaged is currently away from keyboard. Your message will still be shown in their IM panel for later viewing.";

    public const string DefaultBusyMessage =
        "This resident has turned on 'Do Not Disturb' and will see your message later.";

    public const string DefaultAutorespondMessage =
        "The Resident you messaged has activated the viewer's 'autorespond mode' which means they have requested not to be disturbed. Your message will still be shown in their IM panel for later viewing.";

    /// <summary>What the status readout shows: Do Not Disturb wins over Away, as in the nametag.</summary>
    public PresenceStatus Status =>
        DoNotDisturb ? PresenceStatus.DoNotDisturb : Away ? PresenceStatus.Away : PresenceStatus.Available;
}
