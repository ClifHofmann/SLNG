using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>
/// FEAT-UI-35: the grid takes a friend's whole set of rights in one message, so every checkbox is "the old set, one bit
/// different". A change that clobbered the other bits would revoke rights nobody touched.
/// </summary>
public class FriendPermissionsWithTests
{
    private const FriendPermissions OnlineAndMap = FriendPermissions.SeeOnline | FriendPermissions.SeeOnMap;

    [Fact]
    public void SwitchingOneOn_KeepsTheOthers()
    {
        Assert.Equal(OnlineAndMap | FriendPermissions.ModifyObjects, OnlineAndMap.With(FriendPermissions.ModifyObjects, true));
    }

    [Fact]
    public void SwitchingOneOff_KeepsTheOthers()
    {
        Assert.Equal(FriendPermissions.SeeOnMap, OnlineAndMap.With(FriendPermissions.SeeOnline, false));
    }

    [Fact]
    public void SwitchingOnSomethingAlreadyOn_ChangesNothing()
    {
        Assert.Equal(OnlineAndMap, OnlineAndMap.With(FriendPermissions.SeeOnline, true));
    }

    [Fact]
    public void SwitchingOffSomethingAlreadyOff_ChangesNothing()
    {
        Assert.Equal(OnlineAndMap, OnlineAndMap.With(FriendPermissions.ModifyObjects, false));
    }
}
