using System;
using System.Numerics;
using SLNG.Core.Components;
using SLNG.Core.ECS;
using SLNG.Net;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>BUG-NET-17: reported live 2026-09-17 -- a same-region ("local") teleport moved the
/// local avatar's X/Y correctly but rendered her stuck at the OLD height, because
/// ApplyAvatarUpdate deliberately holds the local agent's Z at its own ground-clamped value
/// during ordinary movement, and never overwrites a known SupportPlane with an absent one (a
/// same-region teleport keeps the SAME entity, so a stale plane describing the OLD surface
/// survived and kept steering the ground clamp). <see cref="AvatarUpdateEvent.IsTeleport"/> is
/// the fix: it forces both to be trusted verbatim from the event.</summary>
public class AvatarTeleportPositionTests
{
    [Fact]
    public void OrdinaryLocalAgentMovement_HoldsTheLocalGroundClampedZ()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        session.RaiseAvatarUpdate(new AvatarUpdateEvent(
            123ul, 1, Guid.NewGuid(), new Vector3(10, 10, 25), Quaternion.Identity,
            "Test", "Agent", IsLocalAgent: true));
        simulation.Pump();

        // AvatarController owns local Z between packets -- simulate it having already clamped to
        // a different height than the network reported (25), the normal steady-state situation.
        var transform = world.GetEntity(123ul, 1)!.GetComponent<TransformComponent>()!;
        transform.Position = new Vector3(10, 10, 20);

        // A small ordinary move: X/Y shift a little, network Z still says 25 -- must NOT overwrite
        // the locally-clamped 20.
        session.RaiseAvatarUpdate(new AvatarUpdateEvent(
            123ul, 1, Guid.NewGuid(), new Vector3(11, 11, 25), Quaternion.Identity,
            "Test", "Agent", IsLocalAgent: true));
        simulation.Pump();

        Assert.Equal(20f, transform.Position.Z);
    }

    [Fact]
    public void Teleport_SnapsInstantlyToTheNetworkZ_EvenFarFromTheOldOne()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        session.RaiseAvatarUpdate(new AvatarUpdateEvent(
            123ul, 1, Guid.NewGuid(), new Vector3(98, 81, 1036), Quaternion.Identity,
            "Test", "Agent", IsLocalAgent: true));
        simulation.Pump();

        // Teleport to a wildly different height within the SAME region -- the exact reported case
        // (a "test area" built high in the sky vs. the real destination far below it).
        session.RaiseAvatarUpdate(new AvatarUpdateEvent(
            123ul, 1, Guid.NewGuid(), new Vector3(150, 60, 30), Quaternion.Identity,
            "Test", "Agent", IsLocalAgent: true, IsTeleport: true));
        simulation.Pump();

        var transform = world.GetEntity(123ul, 1)!.GetComponent<TransformComponent>()!;
        Assert.Equal(new Vector3(150, 60, 30), transform.Position);
    }

    [Fact]
    public void Teleport_ClearsAStaleSupportPlane_WhenTheEventCarriesNone()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        session.RaiseAvatarUpdate(new AvatarUpdateEvent(
            123ul, 1, Guid.NewGuid(), Vector3.Zero, Quaternion.Identity,
            "Test", "Agent", IsLocalAgent: true, SupportPlane: new Vector4(0, 0, 1, 1036)));
        simulation.Pump();

        var avatar = world.GetEntity(123ul, 1)!.GetComponent<AvatarComponent>()!;
        Assert.NotNull(avatar.SupportPlane);

        session.RaiseAvatarUpdate(new AvatarUpdateEvent(
            123ul, 1, Guid.NewGuid(), new Vector3(150, 60, 30), Quaternion.Identity,
            "Test", "Agent", IsLocalAgent: true, IsTeleport: true));
        simulation.Pump();

        Assert.Null(avatar.SupportPlane);
    }

    [Fact]
    public void OrdinaryUpdate_NeverClearsAKnownSupportPlane()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        session.RaiseAvatarUpdate(new AvatarUpdateEvent(
            123ul, 1, Guid.NewGuid(), Vector3.Zero, Quaternion.Identity,
            "Test", "Agent", IsLocalAgent: true, SupportPlane: new Vector4(0, 0, 1, 20)));
        simulation.Pump();

        // An ordinary (non-teleport) update from a compact ObjectData layout carries no plane --
        // must be a no-op, not a clear (see ApplyAvatarUpdate's "absence read as information" note).
        session.RaiseAvatarUpdate(new AvatarUpdateEvent(
            123ul, 1, Guid.NewGuid(), new Vector3(1, 0, 20), Quaternion.Identity,
            "Test", "Agent", IsLocalAgent: true));
        simulation.Pump();

        var avatar = world.GetEntity(123ul, 1)!.GetComponent<AvatarComponent>()!;
        Assert.NotNull(avatar.SupportPlane);
    }

    /// <summary>BUG-NET-17, second round. Clearing the collision plane on the teleport event was
    /// not enough: the simulator keeps sending avatar updates throughout, and one queued from
    /// BEFORE the jump lands right after the resync carrying the old position and the old plane.
    /// The ground clamp then follows it back to the height just left — measured live 2026-09-18,
    /// resync to Z 27.1 followed by groundZ 1034.81.
    ///
    /// <para>The rule is temporal, not geometric: a plane is ignored while the updates carrying
    /// it are still talking about the old place. Distance alone cannot decide this — flying
    /// eighty metres above a floor is ordinary and that plane is correct.</para></summary>
    [Fact]
    public void AStaleInFlightUpdate_CannotRestoreThePlaneTheTeleportCleared()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);
        var agentId = Guid.NewGuid();

        // Standing in a skybox at 1035, with the simulator's plane for its floor.
        var skyboxFloor = new Vector4(0, 0, 1, 1034.81f);
        session.RaiseAvatarUpdate(new AvatarUpdateEvent(1ul, 3, agentId,
            new Vector3(132.4f, 122.7f, 1035.8f), Quaternion.Identity, "Test", "User", true,
            SupportPlane: skyboxFloor));
        simulation.Pump();

        var entity = world.GetEntity(1ul, 3)!;
        var avatar = entity.GetComponent<AvatarComponent>()!;
        Assert.Equal(skyboxFloor, avatar.SupportPlane);

        // Teleport down to ground level.
        session.RaiseAvatarUpdate(new AvatarUpdateEvent(1ul, 3, agentId,
            new Vector3(132.4f, 122.7f, 27.1f), Quaternion.Identity, "Test", "User", true,
            IsTeleport: true));
        simulation.Pump();
        Assert.Null(avatar.SupportPlane);

        // An update the simulator had already queued before the jump: old position, old plane.
        session.RaiseAvatarUpdate(new AvatarUpdateEvent(1ul, 3, agentId,
            new Vector3(132.4f, 122.7f, 1035.8f), Quaternion.Identity, "Test", "User", true,
            SupportPlane: skyboxFloor));
        simulation.Pump();
        Assert.Null(avatar.SupportPlane);

        // Once the simulator is talking about the new place, its plane is trustworthy again.
        var groundPlane = new Vector4(0, 0, 1, 26.2f);
        session.RaiseAvatarUpdate(new AvatarUpdateEvent(1ul, 3, agentId,
            new Vector3(132.5f, 122.6f, 27.2f), Quaternion.Identity, "Test", "User", true,
            SupportPlane: groundPlane));
        simulation.Pump();
        Assert.Equal(groundPlane, avatar.SupportPlane);
    }
}
