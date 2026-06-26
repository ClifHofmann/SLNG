using System.Linq;
using System.Numerics;
using SLNG.Core.ECS;
using SLNG.Core.Components;
using SLNG.Net;
using Xunit;

namespace SLNG.Core.Tests;

public class WorldSimulationTests
{
    [Fact]
    public void ObjectUpdateEvent_CreatesEntityWithTransform()
    {
        var world = new World();
        // We instantiate GridSession to trigger the event.
        // For testing we just trigger the internal method.
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        var pos = new Vector3(10, 20, 30);
        var rot = Quaternion.Identity;

        // Trigger the internal RaiseObjectUpdate method (which we added a wrapper or just made internal).
        // Since GridSession.RaiseObjectUpdate is internal, and InternalsVisibleTo is set for SLNG.Net.Tests,
        // we need to make sure SLNG.Core.Tests also has InternalsVisibleTo!
        
        var scale = new Vector3(1, 1, 1);
        byte pCode = 1;

        session.RaiseObjectUpdate(new ObjectUpdateEvent(100, pos, rot, scale, pCode));

        var entity = world.GetEntity(100);
        Assert.NotNull(entity);
        
        var transform = entity.GetComponent<TransformComponent>();
        Assert.NotNull(transform);
        Assert.Equal(pos, transform.Position);
        Assert.Equal(rot, transform.Rotation);

        var prim = entity.GetComponent<PrimitiveComponent>();
        Assert.NotNull(prim);
        Assert.Equal(scale, prim.Scale);
        Assert.Equal(pCode, prim.ProfileCurve);
    }
}
