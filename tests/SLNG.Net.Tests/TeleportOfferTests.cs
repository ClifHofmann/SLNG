using System.Reflection;
using System.Text;
using LibreMetaverse;
using SLNG.Core;
using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>
/// BUG-NET-27: an incoming teleport offer (a "lure") and a teleport request.
///
/// Both arrive as an <c>ImprovedInstantMessage</c> whose dialog is neither <c>MessageFromAgent</c>
/// nor one of the two the session already handled, so <c>OnInstantMessage</c> dropped them at its
/// final guard — the user never saw them, and "ich kann kein TP request annehmen".
///
/// What is pinned here is the wire reading, taken from the reference viewer rather than from memory
/// (<c>llviewermessage.cpp</c> process_improved_im, <c>llimprocessing.cpp:1206-1326</c>): the lure id
/// is the message's <c>ID</c> field (LibreMetaverse's <c>IMSessionID</c>), the sender is the
/// <c>AgentID</c>, the text is the message body, and an offer's binary bucket is the pipe-separated
/// destination (<c>parse_lure_bucket</c>, :352-404) of which only the optional trailing access
/// token is read here. LibreMetaverse's own wording is inverted against the viewer's: its
/// <c>RequestTeleport</c> (22) is the viewer's IM_LURE_USER — an OFFER — and its <c>RequestLure</c>
/// (26) is the viewer's IM_TELEPORT_REQUEST.
/// </summary>
public class TeleportOfferTests
{
    private static void Invoke(GridSession session, string handler, object args)
    {
        var m = typeof(GridSession).GetMethod(handler, BindingFlags.NonPublic | BindingFlags.Instance)!;
        m.Invoke(session, new object?[] { null, args });
    }

    private static InstantMessageEventArgs Im(
        InstantMessageDialog dialog, UUID lureId, UUID fromAgent, string fromName, string message, byte[]? bucket)
    {
        var im = new InstantMessage
        {
            Dialog = dialog,
            IMSessionID = lureId,
            FromAgentID = fromAgent,
            FromAgentName = fromName,
            Message = message,
            BinaryBucket = bucket ?? Array.Empty<byte>(),
        };
        return new InstantMessageEventArgs(im, null);
    }

    /// <summary>The lure bucket the simulator sends: gx|gy|rx|ry|rz|lx|ly|lz[|access].</summary>
    private static byte[] LureBucket(string text) => Encoding.ASCII.GetBytes(text);

    // ---- routing through OnInstantMessage -------------------------------------------------

    [Fact]
    public void An_offer_is_raised_with_the_lure_id_sender_and_text()
    {
        using var session = new GridSession();
        TeleportOfferEvent? offer = null;
        InstantMessageEvent? im = null;
        session.TeleportOfferReceived += (s, e) => offer = e;
        session.InstantMessageReceived += (s, e) => im = e;

        var lureId = UUID.Random();
        var friend = UUID.Random();
        Invoke(session, "OnInstantMessage", Im(
            InstantMessageDialog.RequestTeleport, lureId, friend, "Denise Resident",
            "Come see my build\r\nsecondlife://Ahern/128/128/30", LureBucket("256000|256000|128|128|30|0|1|0|M")));

        Assert.NotNull(offer);
        Assert.Equal(TeleportOfferKind.Offer, offer!.Kind);
        Assert.Equal(lureId.Guid, offer.LureId);
        Assert.Equal(friend.Guid, offer.FromId);
        Assert.Equal("Denise Resident", offer.FromName);
        Assert.Equal("Come see my build\r\nsecondlife://Ahern/128/128/30", offer.Message);
        Assert.False(offer.Godlike);
        Assert.Equal(MaturityLevel.Moderate, offer.Maturity);

        // A lure is not conversation -- it must not also land in an IM tab.
        Assert.Null(im);
    }

    [Fact]
    public void A_request_is_raised_as_a_request_and_ignores_its_empty_bucket()
    {
        using var session = new GridSession();
        TeleportOfferEvent? request = null;
        session.TeleportOfferReceived += (s, e) => request = e;

        var id = UUID.Random();
        var asker = UUID.Random();
        Invoke(session, "OnInstantMessage", Im(
            InstantMessageDialog.RequestLure, id, asker, "Someone Resident", "Can I join you?", null));

        Assert.NotNull(request);
        Assert.Equal(TeleportOfferKind.Request, request!.Kind);
        Assert.Equal(id.Guid, request.LureId);
        Assert.Equal(asker.Guid, request.FromId);
        Assert.Equal("Can I join you?", request.Message);
        // llimprocessing.cpp:1235 -- the (empty) bucket of a request is deliberately not parsed.
        Assert.Null(request.Maturity);
    }

    // IM_GODLIKE_LURE_USER (25). The viewer teleports on it with no prompt at all
    // (llimprocessing.cpp:1415); SLNG asks like for any other lure, and remembers the flag so an
    // accept sends the same teleport flags the viewer would.
    [Fact]
    public void A_godlike_lure_is_an_offer_that_still_asks()
    {
        using var session = new GridSession();
        TeleportOfferEvent? offer = null;
        session.TeleportOfferReceived += (s, e) => offer = e;

        Invoke(session, "OnInstantMessage", Im(
            InstantMessageDialog.GodLikeRequestTeleport, UUID.Random(), UUID.Random(), "Linden", "Please come", null));

        Assert.NotNull(offer);
        Assert.Equal(TeleportOfferKind.Offer, offer!.Kind);
        Assert.True(offer.Godlike);
    }

    // The two replies OTHER people send to OUR offers. Not decisions for this user.
    [Theory]
    [InlineData(InstantMessageDialog.AcceptTeleport)]
    [InlineData(InstantMessageDialog.DenyTeleport)]
    [InlineData(InstantMessageDialog.MessageFromAgent)]
    public void Other_dialogs_are_not_teleport_offers(InstantMessageDialog dialog)
    {
        using var session = new GridSession();
        TeleportOfferEvent? offer = null;
        session.TeleportOfferReceived += (s, e) => offer = e;

        Invoke(session, "OnInstantMessage", Im(dialog, UUID.Random(), UUID.Random(), "Someone", "hi", null));

        Assert.Null(offer);
    }

    // ---- the pure decoder -----------------------------------------------------------------

    [Theory]
    [InlineData("256000|256000|128|128|30|0|1|0|PG", MaturityLevel.General)]
    [InlineData("256000|256000|128|128|30|0|1|0|M", MaturityLevel.Moderate)]
    [InlineData("256000|256000|128|128|30|0|1|0|A", MaturityLevel.Adult)]
    [InlineData("256000|256000|128|128|30|0|1|0| A ", MaturityLevel.Adult)] // the viewer trims it (:388)
    public void The_trailing_access_token_is_the_destination_maturity(string bucket, MaturityLevel expected)
    {
        var e = GridSession.TryDecodeTeleportIm(
            InstantMessageDialog.RequestTeleport, Guid.NewGuid(), Guid.NewGuid(), "N", "m", LureBucket(bucket));

        Assert.Equal(expected, e!.Maturity);
    }

    [Theory]
    [InlineData("256000|256000|128|128|30|0|1|0")]      // no token: the viewer's SIM_ACCESS_MIN, nothing to gate on
    [InlineData("256000|256000|128|128|30|0|1|0|?")]    // a token it does not know falls back the same way (:384)
    [InlineData("256000|256000|128")]                   // truncated: parse_lure_bucket fails
    [InlineData("not a bucket")]
    [InlineData("")]
    public void A_bucket_without_a_usable_token_leaves_the_maturity_unknown_but_the_offer_stands(string bucket)
    {
        // llimprocessing.cpp:1236 parses best-effort and falls through to the plain prompt either
        // way: a malformed bucket must never swallow the invitation.
        var e = GridSession.TryDecodeTeleportIm(
            InstantMessageDialog.RequestTeleport, Guid.NewGuid(), Guid.NewGuid(), "N", "m", LureBucket(bucket));

        Assert.NotNull(e);
        Assert.Null(e!.Maturity);
    }

    [Fact]
    public void A_null_name_and_message_become_empty_strings()
    {
        var e = GridSession.TryDecodeTeleportIm(
            InstantMessageDialog.RequestLure, Guid.NewGuid(), Guid.NewGuid(), null, null, null);

        Assert.Equal(string.Empty, e!.FromName);
        Assert.Equal(string.Empty, e.Message);
    }

    // ---- what accepting sends -------------------------------------------------------------

    // llagent.cpp:4366-4375: VIA_LURE (1<<2), or VIA_GODLIKE_LURE (1<<8) | DISABLE_CANCEL (1<<11).
    [Theory]
    [InlineData(false, 4u)]
    [InlineData(true, 256u | 2048u)]
    public void Accepting_sends_the_viewers_teleport_flags(bool godlike, uint expected) =>
        Assert.Equal(expected, GridSession.LureTeleportFlags(godlike));

    // ---- answering with no session ---------------------------------------------------------

    [Fact]
    public void Nothing_is_sent_without_a_connection()
    {
        using var session = new GridSession();
        var id = Guid.NewGuid();

        Assert.False(session.AcceptTeleportOffer(id, godlike: false));
        Assert.False(session.DeclineTeleportOffer(Guid.NewGuid(), id));
        Assert.False(session.AnswerTeleportRequest(Guid.NewGuid(), accept: true));
    }

    [Fact]
    public void An_empty_id_is_never_sent()
    {
        using var session = new GridSession();

        Assert.False(session.AcceptTeleportOffer(Guid.Empty, godlike: false));
        Assert.False(session.DeclineTeleportOffer(Guid.NewGuid(), Guid.Empty));
        Assert.False(session.DeclineTeleportOffer(Guid.Empty, Guid.NewGuid()));
        Assert.False(session.AnswerTeleportRequest(Guid.Empty, accept: true));
    }

    // llviewermessage.cpp:6361-6364 -- "No" to a request is a case that does nothing: no IM, no
    // decline. So there is nothing to send, and the answer is complete without a connection.
    [Fact]
    public void Declining_a_request_sends_nothing_and_is_complete()
    {
        using var session = new GridSession();

        Assert.True(session.AnswerTeleportRequest(Guid.NewGuid(), accept: false));
    }
}
