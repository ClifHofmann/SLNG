using LibreMetaverse;
using SLNG.Core;

namespace SLNG.Net;

// GridSession, friendship part (BUG-NET-28).
//
// The receiving side of "Offer friendship": decoding the instant message, and answering it. The
// sending side is OfferFriendship in GridSession.Profiles.cs.
//
// Read off the reference viewer, not remembered:
//   dialog ids     llinstantmessage.h:156-158   IM_FRIENDSHIP_OFFERED 38, _ACCEPTED 39,
//                                               _DECLINED_DEPRECATED 40
//   the IM fields  llviewermessage.cpp:2145-2158 from_id = AgentData.AgentID, session_id =
//                                               MessageBlock.ID, offline = MessageBlock.Offline
//   the offer      llimprocessing.cpp:1444-1494 payload { from_id, session_id, online = (offline ==
//                                               IM_ONLINE), sender = the simulator's host }
//   accept         llviewermessage.cpp:255-300  AcceptFriendship cap (POST ?from=&agent_name=) when
//                                               the offer was stored offline and the region has the
//                                               cap, else the UDP AcceptFriendship message with
//                                               TransactionID = session_id and the calling-card folder
//   decline        llviewermessage.cpp:302-352  DeclineFriendship cap (DELETE ?from=) under the same
//                                               test, else UDP DeclineFriendship with TransactionID
//   after accept   llavatartracker (callingcard.cpp:789) formFriendship: both sides see each
//                                               other online, nothing more (the notification text
//                                               says "By default, you will be able to see each
//                                               other's online status.")
//   a reply (39)   llimprocessing.cpp:1496-1512 formFriendship + a requestonlinenotification and a
//                                               "<name> accepted your friendship offer" notice
//   a reply (40)   llimprocessing.cpp:1514      the default branch: logged as an unknown dialog
//
// LibreMetaverse's FriendsManager already does the list bookkeeping on its own Self.IM handler for
// 39 and inside AcceptFriendship*, so this part only has to decode, route and tell the UI to re-read.
public sealed partial class GridSession
{
    /// <summary>How a friendship reply travels (llviewermessage.cpp:277-300, 330-350).</summary>
    internal enum FriendshipReplyRoute
    {
        /// <summary>Nothing to send it with: no capability on offer and no transaction id to echo.</summary>
        None,

        /// <summary>The <c>AcceptFriendship</c> / <c>DeclineFriendship</c> UDP message.</summary>
        Udp,

        /// <summary>The <c>AcceptFriendship</c> / <c>DeclineFriendship</c> capability.</summary>
        Capability,
    }

    /// <summary>The viewer's choice of path: the capability for an offer stored while the agent was
    /// away (<c>!online</c>) when the region has it, otherwise the UDP message, which needs the
    /// offer's transaction id.</summary>
    internal static FriendshipReplyRoute ChooseFriendshipReplyRoute(bool online, bool capabilityAvailable, Guid sessionId)
    {
        if (!online && capabilityAvailable) return FriendshipReplyRoute.Capability;
        return sessionId != Guid.Empty ? FriendshipReplyRoute.Udp : FriendshipReplyRoute.None;
    }

    /// <summary>Reads an instant message as a friendship offer, or returns null when it is not one.
    /// Pure, so the wire reading can be tested without a network.</summary>
    /// <param name="sessionId">The message's <c>ID</c> field — LibreMetaverse's <c>IMSessionID</c>.</param>
    /// <param name="online">True unless the message was stored offline.</param>
    internal static FriendshipOfferEvent? TryDecodeFriendshipOffer(
        InstantMessageDialog dialog, Guid sessionId, Guid fromId, string? fromName, string? message, bool online)
    {
        if (dialog != InstantMessageDialog.FriendshipOffered) return null;

        return new FriendshipOfferEvent(fromId, fromName ?? string.Empty, message ?? string.Empty, sessionId, online);
    }

    /// <summary>Reads an instant message as another avatar's answer to our offer, or returns null.</summary>
    internal static FriendshipAnsweredEvent? TryDecodeFriendshipAnswer(
        InstantMessageDialog dialog, Guid fromId, string? fromName) => dialog switch
        {
            InstantMessageDialog.FriendshipAccepted => new FriendshipAnsweredEvent(fromId, fromName ?? string.Empty, Accepted: true),
            InstantMessageDialog.FriendshipDeclined => new FriendshipAnsweredEvent(fromId, fromName ?? string.Empty, Accepted: false),
            _ => null,
        };

    /// <summary>Raised when the set of friends changed in a way <see cref="FriendStatusChanged"/> does
    /// not say: we accepted an offer, or somebody accepted ours. Re-read <see cref="GetFriends"/>.
    /// Raised on a LibreMetaverse network thread — marshal before touching a scene node.</summary>
    public event EventHandler? FriendListChanged;

    // What each friend lets us do with them, as last seen -- the "before" of the next change. LibreMetaverse
    // overwrites its own copy before it raises FriendRightsUpdate, so without this the old value is gone by the time
    // we are told. Seeded when the login response has filled the friend list, and for a friend added later the first
    // time GetFriends sees them.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, FriendPermissions> _rightsGrantedToUs = new();

    private void OnLoginResponseSeedFriendRights(
        bool loginSuccess, bool redirect, string message, string reason, LoginResponseData? reply)
    {
        if (!loginSuccess) return;
        foreach (var friend in _client.Friends.FriendList.Values)
            _rightsGrantedToUs[friend.UUID.Guid] = (FriendPermissions)(int)friend.MyFriendRights;
    }

    /// <summary>Raised on a NETWORK thread when a friend gives us a right over them or takes one back. Not raised for
    /// the first sight of a friend (there is nothing to compare with), nor for our own grants.</summary>
    public event EventHandler<FriendRightsChangedEvent>? FriendRightsChanged;

    // A friend changed what we may do with them, or the grid echoed a change of ours. LibreMetaverse has already
    // stored it in the FriendInfo; the UI has to be told to re-read GetFriends, and a change of THEIR grant to us is
    // worth a notification. Our own echo changes the other half (TheirFriendRights) and compares equal here.
    private void OnFriendRightsUpdate(object? sender, FriendInfoEventArgs e)
    {
        FriendListChanged?.Invoke(this, EventArgs.Empty);

        Guid id = e.Friend.UUID.Guid;
        var after = (FriendPermissions)(int)e.Friend.MyFriendRights;
        bool known = _rightsGrantedToUs.TryGetValue(id, out var before);
        _rightsGrantedToUs[id] = after;
        if (!known || before == after) return;

        string name = e.Friend.Name ?? string.Empty;
        if (string.IsNullOrEmpty(name)) TryGetCachedName(id, out name!);
        FriendRightsChanged?.Invoke(this, new FriendRightsChangedEvent(id, name ?? string.Empty, before, after));
    }

    /// <summary>Sets what this friend may do with us: see that we are online, find us on the map, edit our objects.
    /// <paramref name="mine"/> is the friend's WHOLE set, not a change -- the grid takes the set in one message
    /// (<c>GrantUserRights</c>); build it from <c>FriendEntry.GrantedByMe</c> with
    /// <see cref="FriendPermissionsExtensions.With"/>. Returns false when we are not connected or the friend is not
    /// on the list; nothing is sent then.</summary>
    /// <remarks>LibreMetaverse's <c>GrantRights</c> only sends the packet and leaves its own copy as it was, so the
    /// copy is set here -- otherwise <see cref="GetFriends"/> would keep reporting the old set until the next login.
    /// What we grant is the FRIEND's rights over us, hence <c>TheirFriendRights</c>. Raises
    /// <see cref="FriendListChanged"/> so lists redraw.</remarks>
    public bool SetFriendPermissions(Guid friendId, FriendPermissions mine)
    {
        if (!_client.Network.Connected) return false;
        return ApplyGrantedRights(friendId, mine, (id, rights) => _client.Friends.GrantRights(id, rights));
    }

    /// <summary>The part of <see cref="SetFriendPermissions"/> after the connection check, with the send handed in so a
    /// test can see what would go out. Only the three bits the grid defines are sent (a stray higher bit in
    /// <paramref name="mine"/> would otherwise reach the wire), and LibreMetaverse's copy of what the FRIEND may do with
    /// us is set to the same set.</summary>
    internal bool ApplyGrantedRights(Guid friendId, FriendPermissions mine, Action<UUID, FriendRights> send)
    {
        if (!_client.Friends.FriendList.TryGetValue(new UUID(friendId), out var friend)) return false;

        var rights = (FriendRights)((int)mine & (int)(FriendRights.CanSeeOnline | FriendRights.CanSeeOnMap | FriendRights.CanModifyObjects));
        send(friend.UUID, rights);
        friend.TheirFriendRights = rights;
        FriendListChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    // The other side ended the friendship (TerminateFriendship, from any viewer). LibreMetaverse's
    // own handler has already dropped them from FriendList; the UI has to be told to re-read it,
    // or the friend stays on screen until the next relog.
    private void OnFriendshipTerminated(object? sender, FriendshipTerminatedEventArgs e)
    {
        FriendListChanged?.Invoke(this, EventArgs.Empty);

        Guid id = e.AgentID.Guid;
        _rightsGrantedToUs.TryRemove(id, out _); // see RemoveFriend
        if (ConsumeSelfRemoval(id)) return;
        string name = e.AgentName ?? string.Empty;
        if (string.IsNullOrEmpty(name)) TryGetCachedName(id, out name!);
        FriendshipEnded?.Invoke(this, new FriendshipEndedEvent(id, name ?? string.Empty));
    }

    /// <summary>Raised on a NETWORK thread when somebody removes us as a friend. Not raised for a
    /// friendship we ended ourselves with <see cref="RemoveFriend"/>.</summary>
    public event EventHandler<FriendshipEndedEvent>? FriendshipEnded;

    // Friendships WE ended, so that a TerminateFriendship the grid echoes back is not reported as
    // somebody else removing us. Consumed by the first echo; one that never comes is dropped by age.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTime> _selfRemovedFriends = new();
    private static readonly TimeSpan SelfRemovalEchoWindow = TimeSpan.FromSeconds(30);

    /// <summary>True, and forgets it, when <paramref name="agentId"/> was removed by us just now.</summary>
    internal bool ConsumeSelfRemoval(Guid agentId)
    {
        if (!_selfRemovedFriends.TryRemove(agentId, out var when)) return false;
        return DateTime.UtcNow - when <= SelfRemovalEchoWindow;
    }

    /// <summary>Ends the friendship with <paramref name="agentId"/> (the viewer's
    /// <c>LLAvatarTracker::terminateBuddy</c>: a <c>TerminateFriendship</c> message). Returns false,
    /// and sends nothing, when not connected or the avatar is not on the friend list.</summary>
    public bool RemoveFriend(Guid agentId)
    {
        if (agentId == Guid.Empty || !_client.Network.Connected) return false;
        var id = new UUID(agentId);
        if (!_client.Friends.FriendList.ContainsKey(id)) return false;
        _selfRemovedFriends[agentId] = DateTime.UtcNow;
        _rightsGrantedToUs.TryRemove(agentId, out _); // a friendship formed again later starts from its own rights
        _client.Friends.TerminateFriendship(id);
        FriendListChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>BUG-NET-28: somebody offered the agent friendship. Answer with
    /// <see cref="AcceptFriendshipOffer"/> / <see cref="DeclineFriendshipOffer"/>. Raised on a
    /// LibreMetaverse network thread — marshal before touching a scene node.</summary>
    public event EventHandler<FriendshipOfferEvent>? FriendshipOfferReceived;

    /// <summary>BUG-NET-28: somebody answered a friendship offer the agent sent. Raised on a
    /// LibreMetaverse network thread — marshal before touching a scene node.</summary>
    public event EventHandler<FriendshipAnsweredEvent>? FriendshipAnswered;

    private bool FriendshipCapabilityAvailable(string capability) =>
        _client.Network.CurrentSim?.Caps?.CapabilityURI(capability) != null;

    /// <summary>How long after forming a friendship the online status is asked for a second time. The first
    /// request can reach the grid before it has recorded the friendship, in which case nothing comes back and
    /// the new friend would stay "offline" until the next time they log in or out.</summary>
    private static readonly TimeSpan FriendOnlineRecheckDelay = TimeSpan.FromSeconds(3);

    /// <summary>Asks the grid whether a NEW friend is online -- the generic message
    /// <c>requestonlinenotification</c>, which the reference viewer sends when a friendship is formed
    /// (llimprocessing.cpp:2385, <c>IM_FRIENDSHIP_ACCEPTED</c>). The answer is an <c>OnlineNotification</c>,
    /// which LibreMetaverse turns into <c>FriendOnline</c> (and so <see cref="FriendStatusChanged"/>). Sent
    /// twice, see <see cref="FriendOnlineRecheckDelay"/>; both are harmless if the friend is offline.</summary>
    internal void RequestFriendOnlineStatus(Guid friendId)
    {
        if (friendId == Guid.Empty || !_client.Network.Connected) return;

        var id = new UUID(friendId);
        try
        {
            _client.Friends.RequestOnlineNotification(id);
            _ = Task.Run(async () =>
            {
                await Task.Delay(FriendOnlineRecheckDelay).ConfigureAwait(false);
                if (_client.Network.Connected && _client.Friends.FriendList.ContainsKey(id))
                    _client.Friends.RequestOnlineNotification(id);
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Friends] online status request for {friendId} failed: {ex.Message}");
        }
    }

    /// <summary>Accepts a friendship offer. Returns whether the answer was sent (or, for the
    /// capability route, started) — false when not connected, for an empty sender, or when there is
    /// neither a capability nor a transaction id to answer with.</summary>
    /// <remarks>The new friend is in <see cref="GetFriends"/> as soon as this returns on the UDP
    /// route (LibreMetaverse adds it locally, with the viewer's default rights), and
    /// <see cref="FriendListChanged"/> says so. On the capability route the friend is added only
    /// when the grid confirms, and <see cref="FriendListChanged"/> follows that.</remarks>
    public bool AcceptFriendshipOffer(FriendshipOfferEvent offer)
    {
        if (offer.FromId == Guid.Empty || !_client.Network.Connected) return false;

        switch (ChooseFriendshipReplyRoute(offer.Online, FriendshipCapabilityAvailable("AcceptFriendship"), offer.SessionId))
        {
            case FriendshipReplyRoute.Udp:
                _client.Friends.AcceptFriendship(new UUID(offer.FromId), new UUID(offer.SessionId));
                FriendListChanged?.Invoke(this, EventArgs.Empty);
                RequestFriendOnlineStatus(offer.FromId); // LibreMetaverse adds the friend as "offline" and asks nothing
                return true;
            case FriendshipReplyRoute.Capability:
                _ = AcceptFriendshipViaCapabilityAsync(offer.FromId);
                return true;
            default:
                return false;
        }
    }

    private async Task AcceptFriendshipViaCapabilityAsync(Guid fromId)
    {
        try
        {
            await _client.Friends.AcceptFriendshipViaCapAsync(new UUID(fromId)).ConfigureAwait(false);
            // LibreMetaverse logs a refusal and returns; the friend list is the only witness.
            if (_client.Friends.FriendList.ContainsKey(new UUID(fromId)))
            {
                FriendListChanged?.Invoke(this, EventArgs.Empty);
                RequestFriendOnlineStatus(fromId);
            }
            else
                Console.Error.WriteLine($"[Friends] The grid did not confirm the friendship with {fromId} (AcceptFriendship capability).");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Friends] AcceptFriendship capability failed: {ex.Message}");
        }
    }

    /// <summary>Declines a friendship offer: the viewer's <c>DeclineFriendship</c> to the simulator,
    /// which deletes the pending request. The offerer is not told (llviewermessage.cpp:318). Returns
    /// whether it was sent, under the same conditions as <see cref="AcceptFriendshipOffer"/>.</summary>
    public bool DeclineFriendshipOffer(FriendshipOfferEvent offer)
    {
        if (offer.FromId == Guid.Empty || !_client.Network.Connected) return false;

        switch (ChooseFriendshipReplyRoute(offer.Online, FriendshipCapabilityAvailable("DeclineFriendship"), offer.SessionId))
        {
            case FriendshipReplyRoute.Udp:
                _client.Friends.DeclineFriendship(new UUID(offer.FromId), new UUID(offer.SessionId));
                return true;
            case FriendshipReplyRoute.Capability:
                _ = DeclineFriendshipViaCapabilityAsync(offer.FromId);
                return true;
            default:
                return false;
        }
    }

    private async Task DeclineFriendshipViaCapabilityAsync(Guid fromId)
    {
        try
        {
            await _client.Friends.DeclineFriendshipViaCapAsync(new UUID(fromId)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Friends] DeclineFriendship capability failed: {ex.Message}");
        }
    }
}
