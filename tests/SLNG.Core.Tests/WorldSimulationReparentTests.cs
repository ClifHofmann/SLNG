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

    /// <summary>FEAT-UI-23: attachment status was only ever granted, never taken away. An object
    /// that was detached, dropped, or re-linked into a world build kept the component -- and an
    /// attachment's Position stays LOCAL to its attach point, so the region coordinate the
    /// simulator sends next was read as an offset and the object was drawn a hundred metres from
    /// the avatar. Reported in-world as "it vanishes the moment I resize it", because a resize is
    /// the first thing that makes the simulator send a fresh position for it.</summary>
    [Fact]
    public void DroppingAWornObjectTakesItsAttachmentStatusWithIt()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        Entity? cleared = null;
        simulation.AttachmentCleared += (_, entity) => cleared = entity;

        var avatar = world.GetOrCreateEntity(Region, 100);
        avatar.SetComponent(new TransformComponent { Position = new Vector3(128, 128, 25) });
        avatar.SetComponent(new AvatarComponent(System.Guid.NewGuid(), "Test", "Resident", isLocalAgent: true));

        // Worn: parented to the avatar, and the position is the offset from the attach point.
        session.RaiseObjectUpdate(Update(2, new Vector3(0.05f, 0f, 0.1f), 100));
        session.RaiseObjectUpdate(Update(3, new Vector3(0.2f, 0f, 0f), 2));  // a linked part of it
        simulation.Pump();

        var worn = world.GetEntity(Region, 2)!;
        var wornPart = world.GetEntity(Region, 3)!;
        Assert.NotNull(worn.GetComponent<AttachmentComponent>());
        Assert.NotNull(wornPart.GetComponent<AttachmentComponent>());
        Assert.Equal(new Vector3(0.05f, 0f, 0.1f), worn.GetComponent<TransformComponent>()!.Position);

        // Dropped: standalone now, and the simulator reports it in region coordinates.
        session.RaiseObjectUpdate(Update(2, new Vector3(97.5f, 81f, 1035.8f), 0));
        simulation.Pump();

        Assert.Null(worn.GetComponent<AttachmentComponent>());
        Assert.Null(wornPart.GetComponent<AttachmentComponent>());  // the part goes with it
        Assert.Equal(new Vector3(97.5f, 81f, 1035.8f), worn.GetComponent<TransformComponent>()!.Position);
        Assert.NotNull(cleared);
    }

    /// <summary>The other half of that rule: a parent that has not streamed in yet says nothing
    /// about whether this object is worn, and must not be read as "not worn". Attachments
    /// routinely arrive before the avatar they hang on.</summary>
    [Fact]
    public void AnUnresolvedParentDoesNotStripAttachmentStatus()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        var avatar = world.GetOrCreateEntity(Region, 100);
        avatar.SetComponent(new TransformComponent());
        avatar.SetComponent(new AvatarComponent(System.Guid.NewGuid(), "Test", "Resident", isLocalAgent: true));

        session.RaiseObjectUpdate(Update(2, new Vector3(0.05f, 0f, 0.1f), 100));
        simulation.Pump();
        Assert.NotNull(world.GetEntity(Region, 2)!.GetComponent<AttachmentComponent>());

        // Re-parented under a prim nothing has told us about yet.
        session.RaiseObjectUpdate(Update(2, new Vector3(0.05f, 0f, 0.1f), 77));
        simulation.Pump();

        Assert.NotNull(world.GetEntity(Region, 2)!.GetComponent<AttachmentComponent>());
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
