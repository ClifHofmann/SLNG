using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>
/// A friend toast is only worth showing for a real change: the library also reports an offline
/// friend going offline and an online one coming online again.
/// </summary>
public class FriendPresenceTrackerTests
{
    private static readonly Guid Anna = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Ben = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void ComingOnline_FromUnknown_IsAChange()
    {
        var tracker = new FriendPresenceTracker();
        Assert.True(tracker.Update(Anna, online: true));
    }

    [Fact]
    public void GoingOffline_FromUnknown_IsNotAChange()
    {
        // The friends list starts offline, so "offline" for a friend never seen says nothing new.
        var tracker = new FriendPresenceTracker();
        Assert.False(tracker.Update(Anna, online: false));
    }

    [Fact]
    public void RepeatedState_IsNotAChange()
    {
        var tracker = new FriendPresenceTracker();
        Assert.True(tracker.Update(Anna, online: true));
        Assert.False(tracker.Update(Anna, online: true));
        Assert.True(tracker.Update(Anna, online: false));
        Assert.False(tracker.Update(Anna, online: false));
    }

    [Fact]
    public void FriendsAreTrackedSeparately()
    {
        var tracker = new FriendPresenceTracker();
        Assert.True(tracker.Update(Anna, online: true));
        Assert.True(tracker.Update(Ben, online: true));
        Assert.True(tracker.Update(Anna, online: false));
        Assert.False(tracker.Update(Ben, online: true));
    }

    [Fact]
    public void Reset_ForgetsEveryone()
    {
        var tracker = new FriendPresenceTracker();
        tracker.Update(Anna, online: true);
        tracker.Reset();
        Assert.True(tracker.Update(Anna, online: true));
    }
}
