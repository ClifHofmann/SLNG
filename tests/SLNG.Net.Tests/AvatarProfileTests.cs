using System.Reflection;
using LibreMetaverse;
using SLNG.Core;
using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>
/// FEAT-UI-13: GridSession maps LibreMetaverse's avatar-profile replies to engine-neutral DTO
/// events, and the social-action send paths no-op gracefully while disconnected. The private
/// handlers are invoked via reflection with hand-built LibreMetaverse EventArgs — the same shape
/// AvatarManager raises off the wire — mirroring <c>GridSessionTests.OnScriptDialog_maps…</c>.
/// </summary>
public class AvatarProfileTests
{
    private static void Invoke(GridSession session, string handler, object args)
    {
        var m = typeof(GridSession).GetMethod(handler, BindingFlags.NonPublic | BindingFlags.Instance)!;
        m.Invoke(session, new object?[] { null, args });
    }

    [Fact]
    public void OnAvatarPropertiesReply_maps_to_AvatarPropertiesEvent()
    {
        using var session = new GridSession();
        AvatarPropertiesEvent? received = null;
        session.AvatarPropertiesReceived += (s, e) => received = e;

        var avatarId = UUID.Random();
        var profileImg = UUID.Random();
        var firstLifeImg = UUID.Random();
        var partner = UUID.Random();
        var props = new Avatar.AvatarProperties
        {
            AboutText = "Hello world",
            FirstLifeText = "RL text",
            ProfileImage = profileImg,
            FirstLifeImage = firstLifeImg,
            Partner = partner,
            BornOn = "2007-05-14",
            CharterMember = "Resident",
            ProfileURL = "https://example.com",
        };

        Invoke(session, "OnAvatarPropertiesReply", new AvatarPropertiesReplyEventArgs(avatarId, props));

        Assert.NotNull(received);
        var p = received!.Properties;
        Assert.Equal(avatarId.Guid, p.AgentId);
        Assert.Equal("Hello world", p.AboutText);
        Assert.Equal("RL text", p.FirstLifeText);
        Assert.Equal(profileImg.Guid, p.ProfileImageId);
        Assert.Equal(firstLifeImg.Guid, p.FirstLifeImageId);
        Assert.Equal(partner.Guid, p.PartnerId);
        Assert.Equal("2007-05-14", p.BornOn);
        Assert.Equal("https://example.com", p.ProfileUrl);
    }

    [Fact]
    public void OnAvatarInterestsReply_maps_to_AvatarInterestsEvent()
    {
        using var session = new GridSession();
        AvatarInterestsEvent? received = null;
        session.AvatarInterestsReceived += (s, e) => received = e;

        var avatarId = UUID.Random();
        var interests = new Avatar.Interests
        {
            LanguagesText = "English",
            SkillsText = "Building",
            WantToText = "Explore",
        };

        Invoke(session, "OnAvatarInterestsReply", new AvatarInterestsReplyEventArgs(avatarId, interests));

        Assert.NotNull(received);
        Assert.Equal(avatarId.Guid, received!.Interests.AgentId);
        Assert.Equal("English", received.Interests.LanguagesText);
        Assert.Equal("Building", received.Interests.SkillsText);
        Assert.Equal("Explore", received.Interests.WantToText);
    }

    [Fact]
    public void OnAvatarGroupsReply_maps_to_AvatarGroupsEvent()
    {
        using var session = new GridSession();
        AvatarGroupsEvent? received = null;
        session.AvatarGroupsReceived += (s, e) => received = e;

        var avatarId = UUID.Random();
        var groupId = UUID.Random();
        var insignia = UUID.Random();
        var groups = new System.Collections.Generic.List<AvatarGroup>
        {
            new() { GroupID = groupId, GroupName = "Builders United", GroupInsigniaID = insignia },
        };

        Invoke(session, "OnAvatarGroupsReply", new AvatarGroupsReplyEventArgs(avatarId, groups));

        Assert.NotNull(received);
        Assert.Equal(avatarId.Guid, received!.AgentId);
        var g = Assert.Single(received.Groups);
        Assert.Equal(groupId.Guid, g.GroupId);
        Assert.Equal("Builders United", g.Name);
        Assert.Equal(insignia.Guid, g.InsigniaId);
    }

    [Fact]
    public void OnAvatarPicksReply_maps_to_AvatarPicksEvent()
    {
        using var session = new GridSession();
        AvatarPicksEvent? received = null;
        session.AvatarPicksReceived += (s, e) => received = e;

        var avatarId = UUID.Random();
        var pickId = UUID.Random();
        var picks = new System.Collections.Generic.Dictionary<UUID, string> { [pickId] = "Favourite Beach" };

        Invoke(session, "OnAvatarPicksReply", new AvatarPicksReplyEventArgs(avatarId, picks));

        Assert.NotNull(received);
        Assert.Equal(avatarId.Guid, received!.AgentId);
        var pick = Assert.Single(received.Picks);
        Assert.Equal(pickId.Guid, pick.PickId);
        Assert.Equal("Favourite Beach", pick.Name);
    }

    [Fact]
    public void OnPickInfoReply_maps_to_AvatarPickDetailEvent()
    {
        using var session = new GridSession();
        AvatarPickDetailEvent? received = null;
        session.AvatarPickDetailReceived += (s, e) => received = e;

        var pickId = UUID.Random();
        var snapshot = UUID.Random();
        var pick = new ProfilePick
        {
            PickID = pickId,
            Name = "Favourite Beach",
            Desc = "White sand.",
            SnapshotID = snapshot,
            SimName = "Howletts",
            PosGlobal = new Vector3d(305050.0, 276330.0, 26.0),
        };

        Invoke(session, "OnPickInfoReply", new PickInfoReplyEventArgs(pickId, pick));

        Assert.NotNull(received);
        var d = received!.Pick;
        Assert.Equal(pickId.Guid, d.PickId);
        Assert.Equal("Favourite Beach", d.Name);
        Assert.Equal("White sand.", d.Description);
        Assert.Equal(snapshot.Guid, d.SnapshotId);
        Assert.Equal("Howletts", d.SimName);
        Assert.Equal(305050.0, d.GlobalX);
        Assert.Equal(276330.0, d.GlobalY);
        Assert.Equal(26.0, d.GlobalZ);
    }

    [Fact]
    public void OnAvatarClassifiedReply_maps_to_AvatarClassifiedsEvent()
    {
        using var session = new GridSession();
        AvatarClassifiedsEvent? received = null;
        session.AvatarClassifiedsReceived += (s, e) => received = e;

        var avatarId = UUID.Random();
        var adId = UUID.Random();
        var ads = new System.Collections.Generic.Dictionary<UUID, string> { [adId] = "Land for sale" };

        Invoke(session, "OnAvatarClassifiedReply", new AvatarClassifiedReplyEventArgs(avatarId, ads));

        Assert.NotNull(received);
        Assert.Equal(avatarId.Guid, received!.AgentId);
        var ad = Assert.Single(received.Classifieds);
        Assert.Equal(adId.Guid, ad.ClassifiedId);
        Assert.Equal("Land for sale", ad.Name);
    }

    // FEAT-UI-13: OnChatFromSimulator carries the speaker's id + "is an agent" through to
    // ChatMessageEvent (for the profile link), and drops a truly empty line (a worn radio object
    // emitting "" on channel 0), matching the Linden/Firestorm nearby-chat handler.
    [Theory]
    [InlineData("hello", LibreMetaverse.ChatSourceType.Agent, true, true)]
    [InlineData("hello", LibreMetaverse.ChatSourceType.Object, true, false)]
    [InlineData("", LibreMetaverse.ChatSourceType.Object, false, false)]
    public void OnChatFromSimulator_maps_source_and_drops_empty(
        string message, LibreMetaverse.ChatSourceType sourceType, bool expectEvent, bool expectFromAgent)
    {
        using var session = new GridSession();
        ChatMessageEvent? received = null;
        session.ChatMessageReceived += (s, e) => received = e;

        var sourceId = UUID.Random();
        var args = new ChatEventArgs(
            simulator: null!, message: message, audible: ChatAudibleLevel.Fully, type: ChatType.Normal,
            sourceType: sourceType, fromName: "Some Speaker", sourceId: sourceId,
            ownerid: UUID.Zero, position: Vector3.Zero);

        Invoke(session, "OnChatFromSimulator", args);

        if (!expectEvent)
        {
            Assert.Null(received);
            return;
        }
        Assert.NotNull(received);
        Assert.Equal("Some Speaker", received!.FromName);
        Assert.Equal(message, received.Message);
        Assert.Equal(sourceId.Guid, received.SourceId);
        Assert.Equal(expectFromAgent, received.FromAgent);
    }

    // Every social-action send path must no-op (not throw) while disconnected — same contract as
    // the rest of GridSession's send surface.
    [Fact]
    public void Social_actions_without_connection_do_not_throw()
    {
        using var session = new GridSession();
        var id = System.Guid.NewGuid();

        Assert.Null(Record.Exception(() => session.RequestAvatarProfile(id)));
        Assert.Null(Record.Exception(() => session.OfferFriendship(id)));
        Assert.Null(Record.Exception(() => session.OfferTeleport(id)));
        Assert.Null(Record.Exception(() => session.PayAvatar(id, 100)));
        Assert.Null(Record.Exception(() => session.PayAvatar(id, 0)));
        Assert.Null(Record.Exception(() => session.RequestMuteList()));
        Assert.Null(Record.Exception(() => session.SetAvatarMuted(id, "Some Resident", true)));
        Assert.Null(Record.Exception(() => session.SetAvatarMuted(id, "Some Resident", false)));
        Assert.False(session.IsAvatarMuted(id));
        Assert.Null(Record.Exception(() =>
            session.UpdateOwnProfile("about", "fl", "https://x", Guid.NewGuid(), Guid.NewGuid(), true, false)));
        Assert.Null(Record.Exception(() => session.UpdateOwnInterests("English", "Building", "Explore")));
        Assert.Null(Record.Exception(() => session.RequestAvatarPickInfo(id, Guid.NewGuid())));
        Assert.Null(Record.Exception(() => session.TeleportToGlobalPosition("Howletts", 305050, 276330, 26)));
    }
}
