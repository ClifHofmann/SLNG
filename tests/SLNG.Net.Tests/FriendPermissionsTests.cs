using System.Collections.Concurrent;
using System.Reflection;
using LibreMetaverse;
using SLNG.Core;
using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>
/// FEAT-UI-35: what each side of a friendship may do (see online, find on the map, edit objects), read from
/// LibreMetaverse's FriendInfo and written back with GrantRights.
///
/// The trap is the naming. LibreMetaverse names the rights by who HOLDS them: <c>TheirFriendRights</c> is what the
/// friend may do with us (what we granted -- <c>FriendInfo.CanSeeMeOnline</c> reads it), <c>MyFriendRights</c> what we
/// may do with them. SLNG says it the other way round (<c>GrantedByMe</c> / <c>GrantedToMe</c>), so a swap would
/// show the person's own grants as the friend's and the other way round -- and the checkboxes would grant the
/// wrong thing. The tests pin the direction with distinct sets on each side.
/// </summary>
public class FriendPermissionsTests
{
    private static readonly UUID Denise = UUID.Parse("11111111-1111-1111-1111-111111111111");

    private static GridClient ClientOf(GridSession session) =>
        (GridClient)typeof(GridSession).GetField("_client", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(session)!;

    private static FriendInfo AddFriend(GridSession session, UUID id, FriendRights friendMayDo, FriendRights iMayDo)
    {
        var info = (FriendInfo)Activator.CreateInstance(
            typeof(FriendInfo), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            new object[] { id, FriendRights.None, FriendRights.None }, null)!;
        info.Name = "Denise Resident";
        info.TheirFriendRights = friendMayDo;
        info.MyFriendRights = iMayDo;

        var list = (ConcurrentDictionary<UUID, FriendInfo>)typeof(FriendsManager)
            .GetField("m_FriendList", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(ClientOf(session).Friends)!;
        list[id] = info;
        return info;
    }

    [Fact]
    public void GetFriends_ReadsWhatTheFriendMayDoWithUsAsGrantedByMe_AndTheOtherWayAsGrantedToMe()
    {
        using var session = new GridSession();
        AddFriend(session, Denise,
            friendMayDo: FriendRights.CanSeeOnline | FriendRights.CanModifyObjects, // what we let her do
            iMayDo: FriendRights.CanSeeOnMap);                                       // what she lets us do

        var entry = Assert.Single(session.GetFriends());

        Assert.Equal(FriendPermissions.SeeOnline | FriendPermissions.ModifyObjects, entry.GrantedByMe);
        Assert.Equal(FriendPermissions.SeeOnMap, entry.GrantedToMe);
    }

    [Fact]
    public void GetFriends_ForAFriendWithNoRights_ReportsNone()
    {
        using var session = new GridSession();
        AddFriend(session, Denise, FriendRights.None, FriendRights.None);

        var entry = Assert.Single(session.GetFriends());

        Assert.Equal(FriendPermissions.None, entry.GrantedByMe);
        Assert.Equal(FriendPermissions.None, entry.GrantedToMe);
    }

    // The values go to the grid as they are; a redeclared enum that drifted would grant something else.
    [Theory]
    [InlineData(FriendPermissions.SeeOnline, FriendRights.CanSeeOnline)]
    [InlineData(FriendPermissions.SeeOnMap, FriendRights.CanSeeOnMap)]
    [InlineData(FriendPermissions.ModifyObjects, FriendRights.CanModifyObjects)]
    [InlineData(FriendPermissions.None, FriendRights.None)]
    public void ThePermissionBitsAreTheGridsBits(FriendPermissions ours, FriendRights theirs)
    {
        Assert.Equal((int)theirs, (int)ours);
    }

    [Fact]
    public void SetFriendPermissions_WhenNotConnected_SendsNothingAndChangesNothing()
    {
        using var session = new GridSession();
        var info = AddFriend(session, Denise, FriendRights.CanSeeOnline, FriendRights.None);
        int changed = 0;
        session.FriendListChanged += (_, _) => changed++;

        bool sent = session.SetFriendPermissions(Denise.Guid, FriendPermissions.SeeOnline | FriendPermissions.ModifyObjects);

        Assert.False(sent);
        Assert.Equal(FriendRights.CanSeeOnline, info.TheirFriendRights);
        Assert.Equal(0, changed);
    }

    [Fact]
    public void SetFriendPermissions_ForSomeoneWhoIsNotAFriend_IsRefused()
    {
        using var session = new GridSession();

        Assert.False(session.SetFriendPermissions(Guid.NewGuid(), FriendPermissions.SeeOnline));
    }

    // ---- an incoming change becomes a notification --------------------------------------------------

    private static void RightsUpdate(GridSession session, FriendInfo info) =>
        typeof(GridSession).GetMethod("OnFriendRightsUpdate", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(session, new object?[] { null, new FriendInfoEventArgs(info) });

    private static void SeedFromLogin(GridSession session, bool success = true) =>
        typeof(GridSession).GetMethod("OnLoginResponseSeedFriendRights", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(session, new object?[] { success, false, "", "", null });

    [Fact]
    public void AFriendGivingUsARight_IsReported_WithWhatWasGained()
    {
        using var session = new GridSession();
        var info = AddFriend(session, Denise, friendMayDo: FriendRights.None, iMayDo: FriendRights.CanSeeOnline);
        SeedFromLogin(session);
        FriendRightsChangedEvent? changed = null;
        session.FriendRightsChanged += (_, e) => changed = e;

        info.MyFriendRights = FriendRights.CanSeeOnline | FriendRights.CanSeeOnMap; // she lets us find her
        RightsUpdate(session, info);

        Assert.NotNull(changed);
        Assert.Equal(Denise.Guid, changed!.FriendId);
        Assert.Equal("Denise Resident", changed.FriendName);
        Assert.Equal(FriendPermissions.SeeOnMap, changed.Gained);
        Assert.Equal(FriendPermissions.None, changed.Lost);
    }

    [Fact]
    public void AFriendTakingARightBack_IsReported_WithWhatWasLost()
    {
        using var session = new GridSession();
        var info = AddFriend(session, Denise, FriendRights.None, FriendRights.CanSeeOnMap | FriendRights.CanModifyObjects);
        SeedFromLogin(session);
        FriendRightsChangedEvent? changed = null;
        session.FriendRightsChanged += (_, e) => changed = e;

        info.MyFriendRights = FriendRights.CanModifyObjects;
        RightsUpdate(session, info);

        Assert.Equal(FriendPermissions.SeeOnMap, changed!.Lost);
        Assert.Equal(FriendPermissions.None, changed.Gained);
    }

    // The grid echoes our own grant back; that changes what the FRIEND may do, which we chose ourselves.
    [Fact]
    public void TheEchoOfOurOwnGrant_IsNotReported()
    {
        using var session = new GridSession();
        var info = AddFriend(session, Denise, FriendRights.None, FriendRights.CanSeeOnMap);
        SeedFromLogin(session);
        int reported = 0;
        session.FriendRightsChanged += (_, _) => reported++;

        info.TheirFriendRights = FriendRights.CanSeeOnline | FriendRights.CanModifyObjects;
        RightsUpdate(session, info);

        Assert.Equal(0, reported);
    }

    [Fact]
    public void WithoutAnEarlierSight_ThereIsNothingToCompareWith_SoNothingIsReported()
    {
        using var session = new GridSession();
        var info = AddFriend(session, Denise, FriendRights.None, FriendRights.CanSeeOnMap);
        int reported = 0;
        session.FriendRightsChanged += (_, _) => reported++;

        RightsUpdate(session, info); // no login seed, no GetFriends yet

        Assert.Equal(0, reported);

        // ...but that update was the first sight, so the next real change is reported.
        info.MyFriendRights = FriendRights.None;
        RightsUpdate(session, info);
        Assert.Equal(1, reported);
    }

    [Fact]
    public void AFriendAddedAfterLogin_GetsItsBaselineFromTheFirstGetFriends()
    {
        using var session = new GridSession();
        var info = AddFriend(session, Denise, FriendRights.None, FriendRights.None);
        session.GetFriends();
        FriendRightsChangedEvent? changed = null;
        session.FriendRightsChanged += (_, e) => changed = e;

        info.MyFriendRights = FriendRights.CanModifyObjects;
        RightsUpdate(session, info);

        Assert.Equal(FriendPermissions.ModifyObjects, changed!.Gained);
    }

    [Fact]
    public void AFailedLogin_SeedsNothing()
    {
        using var session = new GridSession();
        var info = AddFriend(session, Denise, FriendRights.None, FriendRights.None);
        SeedFromLogin(session, success: false);
        int reported = 0;
        session.FriendRightsChanged += (_, _) => reported++;

        info.MyFriendRights = FriendRights.CanSeeOnMap;
        RightsUpdate(session, info);

        Assert.Equal(0, reported);
    }

    [Fact]
    public void AFriendChangingTheirRights_TellsTheListToRedraw()
    {
        using var session = new GridSession();
        var info = AddFriend(session, Denise, FriendRights.None, FriendRights.None);
        int changed = 0;
        session.FriendListChanged += (_, _) => changed++;

        var handler = typeof(GridSession).GetMethod("OnFriendRightsUpdate", BindingFlags.NonPublic | BindingFlags.Instance)!;
        handler.Invoke(session, new object?[] { null, new FriendInfoEventArgs(info) });

        Assert.Equal(1, changed);
    }
}
