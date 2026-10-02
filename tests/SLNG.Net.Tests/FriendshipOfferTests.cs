using System.Reflection;
using LibreMetaverse;
using SLNG.Core;
using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>
/// BUG-NET-28: an incoming friendship offer (IM dialog 38) and the replies to our own offers
/// (39 accepted, 40 declined).
///
/// The offer fell through <c>OnInstantMessage</c>'s final <c>Dialog != MessageFromAgent</c> guard, and
/// LibreMetaverse only raises <c>FriendsManager.FriendshipOffered</c> when somebody listens, so a
/// friend request never reached the user. What is pinned here is the wire reading, taken from the
/// reference viewer rather than from memory (<c>llimprocessing.cpp:1444-1520</c>,
/// <c>llviewermessage.cpp:255-368</c>): the transaction id the reply must echo is the message's
/// <c>ID</c> field (LibreMetaverse's <c>IMSessionID</c>), the sender is the packet's <c>AgentID</c>,
/// and <c>online</c> is <c>Offline == IM_ONLINE</c> — which, with the presence of the capability, is
/// what chooses between the UDP reply and the HTTP one (<c>llviewermessage.cpp:277, 330</c>).
/// </summary>
public class FriendshipOfferTests
{
    private static void Invoke(GridSession session, string handler, object args)
    {
        var m = typeof(GridSession).GetMethod(handler, BindingFlags.NonPublic | BindingFlags.Instance)!;
        m.Invoke(session, new object?[] { null, args });
    }

    private static InstantMessageEventArgs Im(
        InstantMessageDialog dialog, UUID sessionId, UUID fromAgent, string fromName, string message,
        InstantMessageOnline offline = InstantMessageOnline.Online)
    {
        var im = new InstantMessage
        {
            Dialog = dialog,
            IMSessionID = sessionId,
            FromAgentID = fromAgent,
            FromAgentName = fromName,
            Message = message,
            Offline = offline,
            BinaryBucket = Array.Empty<byte>(),
        };
        return new InstantMessageEventArgs(im, null);
    }

    // ---- routing through OnInstantMessage -------------------------------------------------

    [Fact]
    public void An_offer_is_raised_with_the_transaction_id_sender_and_text()
    {
        using var session = new GridSession();
        FriendshipOfferEvent? offer = null;
        InstantMessageEvent? im = null;
        session.FriendshipOfferReceived += (s, e) => offer = e;
        session.InstantMessageReceived += (s, e) => im = e;

        var transaction = UUID.Random();
        var from = UUID.Random();
        Invoke(session, "OnInstantMessage", Im(
            InstantMessageDialog.FriendshipOffered, transaction, from, "Denise Resident", "Do ya wanna be my buddy?"));

        Assert.NotNull(offer);
        Assert.Equal(transaction.Guid, offer!.SessionId);
        Assert.Equal(from.Guid, offer.FromId);
        Assert.Equal("Denise Resident", offer.FromName);
        Assert.Equal("Do ya wanna be my buddy?", offer.Message);
        Assert.True(offer.Online);

        // A friend request is not conversation -- it must not also land in an IM tab.
        Assert.Null(im);
    }

    // llimprocessing.cpp:1448 -- payload["online"] = (offline == IM_ONLINE). An offer that was stored
    // while we were away arrives with the Offline flag set.
    [Theory]
    [InlineData(InstantMessageOnline.Online, true)]
    [InlineData(InstantMessageOnline.Offline, false)]
    public void The_offline_flag_is_carried_as_online(InstantMessageOnline flag, bool expectedOnline)
    {
        var e = GridSession.TryDecodeFriendshipOffer(
            InstantMessageDialog.FriendshipOffered, Guid.NewGuid(), Guid.NewGuid(), "N", "m", online: flag == InstantMessageOnline.Online);

        Assert.Equal(expectedOnline, e!.Online);
    }

    [Theory]
    [InlineData(InstantMessageDialog.FriendshipAccepted, true)]
    [InlineData(InstantMessageDialog.FriendshipDeclined, false)]
    public void A_reply_to_our_offer_is_raised_as_an_answer(InstantMessageDialog dialog, bool accepted)
    {
        using var session = new GridSession();
        FriendshipAnsweredEvent? answer = null;
        FriendshipOfferEvent? offer = null;
        session.FriendshipAnswered += (s, e) => answer = e;
        session.FriendshipOfferReceived += (s, e) => offer = e;

        var from = UUID.Random();
        Invoke(session, "OnInstantMessage", Im(dialog, UUID.Random(), from, "Denise Resident", ""));

        Assert.NotNull(answer);
        Assert.Equal(accepted, answer!.Accepted);
        Assert.Equal(from.Guid, answer.FromId);
        Assert.Equal("Denise Resident", answer.FromName);
        // Somebody ANSWERING our offer is not somebody asking for a decision from us.
        Assert.Null(offer);
    }

    // An accepted offer changes the friend list (LibreMetaverse adds the friend itself in its own
    // Self.IM handler); the panel is told so it re-reads it. A decline changes nothing.
    [Theory]
    [InlineData(InstantMessageDialog.FriendshipAccepted, 1)]
    [InlineData(InstantMessageDialog.FriendshipDeclined, 0)]
    [InlineData(InstantMessageDialog.FriendshipOffered, 0)]
    public void The_friend_list_is_reported_changed_only_by_an_acceptance(InstantMessageDialog dialog, int expected)
    {
        using var session = new GridSession();
        int changed = 0;
        session.FriendListChanged += (s, e) => changed++;

        Invoke(session, "OnInstantMessage", Im(dialog, UUID.Random(), UUID.Random(), "Someone", "hi"));

        Assert.Equal(expected, changed);
    }

    [Theory]
    [InlineData(InstantMessageDialog.MessageFromAgent)]
    [InlineData(InstantMessageDialog.RequestTeleport)]
    [InlineData(InstantMessageDialog.GroupInvitation)]
    public void Other_dialogs_are_not_friendship_events(InstantMessageDialog dialog)
    {
        using var session = new GridSession();
        FriendshipOfferEvent? offer = null;
        FriendshipAnsweredEvent? answer = null;
        session.FriendshipOfferReceived += (s, e) => offer = e;
        session.FriendshipAnswered += (s, e) => answer = e;

        Invoke(session, "OnInstantMessage", Im(dialog, UUID.Random(), UUID.Random(), "Someone", "hi"));

        Assert.Null(offer);
        Assert.Null(answer);
    }

    // ---- the pure decoders ----------------------------------------------------------------

    [Fact]
    public void A_null_name_and_message_become_empty_strings()
    {
        var offer = GridSession.TryDecodeFriendshipOffer(
            InstantMessageDialog.FriendshipOffered, Guid.NewGuid(), Guid.NewGuid(), null, null, online: true);
        var answer = GridSession.TryDecodeFriendshipAnswer(
            InstantMessageDialog.FriendshipAccepted, Guid.NewGuid(), null);

        Assert.Equal(string.Empty, offer!.FromName);
        Assert.Equal(string.Empty, offer.Message);
        Assert.Equal(string.Empty, answer!.FromName);
    }

    // llimprocessing.cpp:1480 -- "support for friendship offers from clients before July 2008": an
    // offer with no text is still an offer and still asks.
    [Fact]
    public void An_offer_without_text_is_still_an_offer()
    {
        var offer = GridSession.TryDecodeFriendshipOffer(
            InstantMessageDialog.FriendshipOffered, Guid.NewGuid(), Guid.NewGuid(), "N", "", online: true);

        Assert.NotNull(offer);
    }

    // ---- which way a reply goes ----------------------------------------------------------

    // llviewermessage.cpp:277 / :330: the capability when the offer was stored offline AND the region
    // has one; otherwise the UDP message, which needs a transaction id; otherwise nothing.
    [Theory]
    [InlineData(true, true, true, "Udp")]           // delivered live: UDP even with the cap
    [InlineData(true, false, true, "Udp")]
    [InlineData(false, true, true, "Capability")]   // stored offline: the cap
    [InlineData(false, true, false, "Capability")]  // ... which needs no transaction id
    [InlineData(false, false, true, "Udp")]         // offline but no cap: viewer falls back to UDP
    [InlineData(true, true, false, "None")]         // no id, nothing to echo
    [InlineData(false, false, false, "None")]
    public void The_reply_route_follows_the_viewers_choice(
        bool online, bool capability, bool hasTransactionId, string expected)
    {
        var id = hasTransactionId ? Guid.NewGuid() : Guid.Empty;

        Assert.Equal(expected, GridSession.ChooseFriendshipReplyRoute(online, capability, id).ToString());
    }

    // ---- answering with no session ---------------------------------------------------------

    [Fact]
    public void Nothing_is_sent_without_a_connection()
    {
        using var session = new GridSession();
        var offer = new FriendshipOfferEvent(Guid.NewGuid(), "N", "m", Guid.NewGuid(), Online: true);

        Assert.False(session.AcceptFriendshipOffer(offer));
        Assert.False(session.DeclineFriendshipOffer(offer));
    }

    [Fact]
    public void An_empty_sender_is_never_answered()
    {
        using var session = new GridSession();
        var offer = new FriendshipOfferEvent(Guid.Empty, "N", "m", Guid.NewGuid(), Online: true);

        Assert.False(session.AcceptFriendshipOffer(offer));
        Assert.False(session.DeclineFriendshipOffer(offer));
    }

    [Fact]
    public void Nobody_is_removed_without_a_connection_or_without_an_id()
    {
        using var session = new GridSession();
        bool changed = false;
        session.FriendListChanged += (_, _) => changed = true;

        Assert.False(session.RemoveFriend(Guid.NewGuid()));
        Assert.False(session.RemoveFriend(Guid.Empty));
        Assert.False(changed);
    }

    [Fact]
    public void A_removal_we_never_made_is_not_swallowed()
    {
        using var session = new GridSession();

        Assert.False(session.ConsumeSelfRemoval(Guid.NewGuid()));
    }
}
