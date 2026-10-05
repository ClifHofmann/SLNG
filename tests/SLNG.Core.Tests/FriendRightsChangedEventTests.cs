using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>
/// FEAT-UI-35: what a friend giving or taking a right says -- the notification text is built from these, one line per
/// right, in a fixed order.
/// </summary>
public class FriendRightsChangedEventTests
{
    private static FriendRightsChangedEvent Change(FriendPermissions before, FriendPermissions after) =>
        new(Guid.NewGuid(), "Denise Resident", before, after);

    [Fact]
    public void GainedAndLost_AreTheDifferencesOfTheTwoSets()
    {
        var e = Change(FriendPermissions.SeeOnline | FriendPermissions.SeeOnMap, FriendPermissions.SeeOnMap | FriendPermissions.ModifyObjects);

        Assert.Equal(FriendPermissions.ModifyObjects, e.Gained);
        Assert.Equal(FriendPermissions.SeeOnline, e.Lost);
    }

    [Fact]
    public void Changes_HasOneEntryPerRight_InTheOrderOnlineMapEdit()
    {
        var e = Change(FriendPermissions.None, FriendPermissions.ModifyObjects | FriendPermissions.SeeOnline | FriendPermissions.SeeOnMap);

        Assert.Equal(
            new[]
            {
                new FriendRightChange(FriendPermissions.SeeOnline, Gained: true),
                new FriendRightChange(FriendPermissions.SeeOnMap, Gained: true),
                new FriendRightChange(FriendPermissions.ModifyObjects, Gained: true),
            },
            e.Changes);
    }

    [Fact]
    public void Changes_CanMixGivingAndTakingBack()
    {
        var e = Change(FriendPermissions.SeeOnMap, FriendPermissions.SeeOnline);

        Assert.Equal(
            new[]
            {
                new FriendRightChange(FriendPermissions.SeeOnline, Gained: true),
                new FriendRightChange(FriendPermissions.SeeOnMap, Gained: false),
            },
            e.Changes);
    }

    [Fact]
    public void Changes_LeavesOutWhatStayedTheSame()
    {
        var e = Change(FriendPermissions.SeeOnline | FriendPermissions.SeeOnMap, FriendPermissions.SeeOnline);

        var only = Assert.Single(e.Changes);
        Assert.Equal(new FriendRightChange(FriendPermissions.SeeOnMap, Gained: false), only);
    }

    [Fact]
    public void Changes_IsEmptyWhenNothingChanged()
    {
        var e = Change(FriendPermissions.SeeOnMap, FriendPermissions.SeeOnMap);

        Assert.Empty(e.Changes);
        Assert.Equal(FriendPermissions.None, e.Gained);
        Assert.Equal(FriendPermissions.None, e.Lost);
    }

    [Fact]
    public void AFriendEntryMadeTheOldWay_HasNoRights()
    {
        var entry = new FriendEntry(Guid.NewGuid(), "Denise Resident", true);

        Assert.Equal(FriendPermissions.None, entry.GrantedByMe);
        Assert.Equal(FriendPermissions.None, entry.GrantedToMe);
    }
}
