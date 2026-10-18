using LibreMetaverse;
using SLNG.Core;
using Xunit;

namespace SLNG.Net.Tests;

public class RezInventoryTests
{
    [Fact]
    public void RezObjectFromInventory_returns_NotConnected_when_disconnected()
    {
        using var session = new GridSession();
        var result = session.RezObjectFromInventory(Guid.NewGuid(), System.Numerics.Vector3.Zero);

        Assert.Equal(RezObjectResult.NotConnected, result);
    }

    [Fact]
    public void CanBuildAt_returns_false_when_disconnected()
    {
        using var session = new GridSession();
        bool canBuild = session.CanBuildAt(System.Numerics.Vector3.Zero, out var reason);

        Assert.False(canBuild);
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void IsObjectItem_returns_false_when_item_not_in_store()
    {
        using var session = new GridSession();
        bool isObject = session.IsObjectItem(Guid.NewGuid());

        Assert.False(isObject);
    }
}
