using System.Numerics;
using SLNG.Core.Components;
using SLNG.Core.ECS;
using SLNG.Net;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>FEAT-UI-05: linking and unlinking change a prim's parent while the client is
/// watching, which is the one case the child index was never told about.</summary>
public class WorldSimulationReparentTests
{
    private const ulong Region = 1ul;

    private static ObjectUpdateEvent Update(uint localId, Vector3 position, uint parentLocalId)
        => new(Region, localId, position, Quaternion.Identity, Vector3.One, 1, false,
            System.Guid.Empty, System.Guid.Empty, System.Guid.Empty, Vector4.One,
            ParentLocalId: parentLocalId, AttachmentPoint: 0);

    [Fact]
    public void AnUnlinkedPrimIsNoLongerCountedUnderItsOldRoot()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        // A root at (100, 200, 30) with one child one metre out along X.
        session.RaiseObjectUpdate(Update(1, new Vector3(100, 200, 30), 0));
        session.RaiseObjectUpdate(Update(2, new Vector3(1, 0, 0), 1));
        simulation.Pump();

        var child = world.GetEntity(Region, 2)!;
        Assert.Equal(new Vector3(101, 200, 30), child.GetComponent<TransformComponent>()!.Position);

        // Unlink: the prim is now standalone and the simulator reports its position in region
        // coordinates.
        session.RaiseObjectUpdate(Update(2, new Vector3(101, 200, 30), 0));
        simulation.Pump();
        Assert.Equal(new Vector3(101, 200, 30), child.GetComponent<TransformComponent>()!.Position);

        // The former root moves. The unlinked prim stays put either way -- ResolveWorldTransform
        // returns early once ParentLocalId is 0 -- so this half is a guard, not the regression.
        // The regression is the index itself: while it still listed the prim, the old root
        // counted as a linkset root and Unlink stayed offered on a prim with nothing under it.
        session.RaiseObjectUpdate(Update(1, new Vector3(150, 200, 30), 0));
        simulation.Pump();

        Assert.Equal(new Vector3(101, 200, 30), child.GetComponent<TransformComponent>()!.Position);
        Assert.False(simulation.HasChildren(Region, 1));
    }

    [Fact]
    public void LinkingUnderANewRootMovesThePrimOutOfTheOldOne()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        session.RaiseObjectUpdate(Update(1, new Vector3(10, 0, 0), 0));
        session.RaiseObjectUpdate(Update(2, new Vector3(50, 0, 0), 0));
        session.RaiseObjectUpdate(Update(3, new Vector3(1, 0, 0), 1));
        simulation.Pump();
        Assert.True(simulation.HasChildren(Region, 1));

        // Re-linked under root 2.
        session.RaiseObjectUpdate(Update(3, new Vector3(1, 0, 0), 2));
        simulation.Pump();

        Assert.False(simulation.HasChildren(Region, 1));
        Assert.True(simulation.HasChildren(Region, 2));
        Assert.Equal(new Vector3(51, 0, 0), world.GetEntity(Region, 3)!.GetComponent<TransformComponent>()!.Position);
    }

    [Fact]
    public void ReparentingRaisesTheEventAndAnOrdinaryUpdateDoesNot()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        int reparents = 0;
        simulation.ObjectReparented += (_, _) => reparents++;

        session.RaiseObjectUpdate(Update(1, new Vector3(10, 0, 0), 0));
        simulation.Pump();
        // A prim arriving unparented was never parented in the first place.
        Assert.Equal(0, reparents);

        session.RaiseObjectUpdate(Update(1, new Vector3(11, 0, 0), 0));
        simulation.Pump();
        Assert.Equal(0, reparents);

        session.RaiseObjectUpdate(Update(1, new Vector3(1, 0, 0), 2));
        simulation.Pump();
        Assert.Equal(1, reparents);
    }
}
