using System.Reflection;
using LibreMetaverse;
using SLNG.Core;
using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>
/// BUG-INV-04: receiving an inventory offer.
///
/// Two things are worth pinning here. First the routing: an offer arrives as
/// <c>InventoryOffered</c> (4) or <c>TaskInventoryOffered</c> (9), neither of which is
/// <c>MessageFromAgent</c>, so it used to fall through <c>OnInstantMessage</c>'s final guard and
/// vanish — no window, no reply, and the item only visible after a relog. Second the binary
/// bucket, which the two offer kinds pack differently (llimprocessing.cpp:895-929): 17 bytes with
/// the item id from an avatar, 1 bare asset-type byte from an object. Getting that wrong files the
/// gift into the wrong folder or loses the id the local inventory cache needs.
/// </summary>
public class InventoryOfferTests
{
    private static void Invoke(GridSession session, string handler, object args)
    {
        var m = typeof(GridSession).GetMethod(handler, BindingFlags.NonPublic | BindingFlags.Instance)!;
        m.Invoke(session, new object?[] { null, args });
    }

    // InstantMessageEventArgs.IM is a get-only property over a struct, so every field has to be set
    // before the args are constructed -- including the binary bucket.
    private static InstantMessageEventArgs Im(
        InstantMessageDialog dialog, UUID sessionId, UUID fromAgent, string fromName, string message,
        byte[] binaryBucket)
    {
        var im = new InstantMessage
        {
            Dialog = dialog,
            IMSessionID = sessionId,
            FromAgentID = fromAgent,
            FromAgentName = fromName,
            Message = message,
            BinaryBucket = binaryBucket,
        };
        return new InstantMessageEventArgs(im, null);
    }

    private static byte[] AgentBucket(byte assetType, UUID itemId)
    {
        var bucket = new byte[17];
        bucket[0] = assetType;
        itemId.GetBytes().CopyTo(bucket, 1);
        return bucket;
    }

    // The reported case: a landmark handed over by another avatar. Before the fix this raised
    // nothing at all.
    [Fact]
    public void OnInstantMessage_raises_an_offer_for_an_agent_gift()
    {
        using var session = new GridSession();
        InventoryOfferEvent? offer = null;
        InstantMessageEvent? im = null;
        session.InventoryOfferReceived += (s, e) => offer = e;
        session.InstantMessageReceived += (s, e) => im = e;

        var sessionId = UUID.Random();
        var giver = UUID.Random();
        var itemId = UUID.Random();

        Invoke(session, "OnInstantMessage", Im(
            InstantMessageDialog.InventoryOffered, sessionId, giver, "Denise Resident",
            "Puris Town Square", AgentBucket((byte)AssetType.Landmark, itemId)));

        Assert.NotNull(offer);
        Assert.Equal(sessionId.Guid, offer!.OfferId);
        Assert.Equal(giver.Guid, offer.FromId);
        Assert.Equal("Denise Resident", offer.FromName);
        Assert.Equal("Puris Town Square", offer.ItemName);
        Assert.Equal(itemId.Guid, offer.ItemId);
        Assert.Equal((int)AssetType.Landmark, offer.AssetType);
        Assert.False(offer.FromTask);

        // An offer is not conversation -- it must not also land in an IM tab.
        Assert.Null(im);
    }

    // An in-world object's offer carries one byte and no id: nothing exists server-side until the
    // accept is sent, so there is nothing to fetch.
    [Fact]
    public void OnInstantMessage_raises_an_offer_for_a_task_gift()
    {
        using var session = new GridSession();
        InventoryOfferEvent? offer = null;
        session.InventoryOfferReceived += (s, e) => offer = e;

        Invoke(session, "OnInstantMessage", Im(
            InstantMessageDialog.TaskInventoryOffered, UUID.Random(), UUID.Random(), "Gift Box",
            "Welcome Pack", new[] { (byte)AssetType.Object }));

        Assert.NotNull(offer);
        Assert.True(offer!.FromTask);
        Assert.Equal(Guid.Empty, offer.ItemId);
        Assert.Equal((int)AssetType.Object, offer.AssetType);
    }

    [Fact]
    public void A_plain_instant_message_is_still_not_an_offer()
    {
        using var session = new GridSession();
        InventoryOfferEvent? offer = null;
        InstantMessageEvent? im = null;
        session.InventoryOfferReceived += (s, e) => offer = e;
        session.InstantMessageReceived += (s, e) => im = e;

        Invoke(session, "OnInstantMessage", Im(
            InstantMessageDialog.MessageFromAgent, UUID.Random(), UUID.Random(), "Denise Resident",
            "hi", Array.Empty<byte>()));

        Assert.Null(offer);
        Assert.NotNull(im);
    }

    [Theory]
    [InlineData(0)]   // absent
    [InlineData(1)]   // the TASK shape, on an AGENT offer -- no item id to file
    [InlineData(16)]  // an id with no asset type
    [InlineData(18)]  // one byte too many
    public void A_malformed_agent_bucket_is_dropped_rather_than_misfiled(int bucketSize)
    {
        using var session = new GridSession();
        InventoryOfferEvent? offer = null;
        session.InventoryOfferReceived += (s, e) => offer = e;

        Invoke(session, "OnInstantMessage", Im(
            InstantMessageDialog.InventoryOffered, UUID.Random(), UUID.Random(), "Denise Resident",
            "Something", new byte[bucketSize]));

        Assert.Null(offer);
    }

    [Fact]
    public void TryParseInventoryOfferBucket_reads_the_agent_shape()
    {
        var itemId = UUID.Random();

        Assert.True(GridSession.TryParseInventoryOfferBucket(
            AgentBucket((byte)AssetType.Notecard, itemId), fromTask: false,
            out var assetType, out var parsedId));

        Assert.Equal((int)AssetType.Notecard, assetType);
        Assert.Equal(itemId.Guid, parsedId);
    }

    [Fact]
    public void TryParseInventoryOfferBucket_reads_the_task_shape()
    {
        Assert.True(GridSession.TryParseInventoryOfferBucket(
            new[] { (byte)AssetType.Gesture }, fromTask: true, out var assetType, out var itemId));

        Assert.Equal((int)AssetType.Gesture, assetType);
        Assert.Equal(Guid.Empty, itemId);
    }

    // The 17-byte agent bucket is not a valid task bucket and vice versa -- each kind size-checks
    // only its own shape, exactly as the viewer does.
    [Fact]
    public void TryParseInventoryOfferBucket_does_not_accept_the_other_kinds_shape()
    {
        Assert.False(GridSession.TryParseInventoryOfferBucket(
            AgentBucket((byte)AssetType.Landmark, UUID.Random()), fromTask: true, out _, out _));

        Assert.False(GridSession.TryParseInventoryOfferBucket(
            new[] { (byte)AssetType.Landmark }, fromTask: false, out _, out _));

        Assert.False(GridSession.TryParseInventoryOfferBucket(null, fromTask: false, out _, out _));
    }

    // "The math for the dialog works, because the accept for inventory_offered ... is 1 greater
    // than the offer integer value" -- llviewermessage.cpp:1604-1610. RespondToInventoryOffer
    // relies on that arithmetic, so pin the four values it produces against LibreMetaverse's own
    // enum rather than trusting the comment.
    [Theory]
    [InlineData(InstantMessageDialog.InventoryOffered, InstantMessageDialog.InventoryAccepted, InstantMessageDialog.InventoryDeclined)]
    [InlineData(InstantMessageDialog.TaskInventoryOffered, InstantMessageDialog.TaskInventoryAccepted, InstantMessageDialog.TaskInventoryDeclined)]
    public void Accept_is_the_offer_dialog_plus_one_and_decline_plus_two(
        InstantMessageDialog offer, InstantMessageDialog accepted, InstantMessageDialog declined)
    {
        Assert.Equal(accepted, (InstantMessageDialog)((byte)offer + 1));
        Assert.Equal(declined, (InstantMessageDialog)((byte)offer + 2));
    }

    // Subscribing to LibreMetaverse's own offer event would decline every offer before the user
    // saw the window: InventoryManager.Self_IM fires it synchronously and then sends the reply
    // from args.Accept, which the constructor sets to false. This asserts that default, so a
    // future "just subscribe to the LMV event" refactor trips a test instead of silently
    // rejecting every gift.
    [Fact]
    public void LibreMetaverses_own_offer_event_defaults_to_declining()
    {
        var args = new InventoryObjectOfferedEventArgs(
            new InstantMessage(), AssetType.Landmark, UUID.Random(), fromTask: false, UUID.Random());

        Assert.False(args.Accept);
    }
}
