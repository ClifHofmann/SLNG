using System;
using System.Numerics;
using SLNG.Core.Components;
using SLNG.Core.ECS;
using SLNG.Net;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>
/// The Display Name of an avatar has to survive the avatar entity. The grid is asked once per
/// session per agent, so a name that arrives before the entity exists, or an avatar that leaves
/// and comes back, must not end up with a nametag that has quietly fallen back to the legacy name
/// for the rest of the session.
/// </summary>
public class AvatarDisplayNameTests
{
    private const ulong Region = 123ul;

    private static AvatarUpdateEvent Update(Guid agent, uint localId) =>
        new(Region, localId, agent, new Vector3(10, 10, 25), Quaternion.Identity,
            "Sidney", "Trezuguet", IsLocalAgent: false);

    private static string? DisplayNameOf(World world, uint localId) =>
        world.GetEntity(Region, localId)?.GetComponent<AvatarComponent>()?.DisplayName;

    [Fact]
    public void NameArrivingAfterTheAvatar_IsApplied()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);
        var agent = Guid.NewGuid();

        session.RaiseAvatarUpdate(Update(agent, 1));
        session.RaiseDisplayNameResolved(new NameResolvedEvent(agent, "Sid"));
        simulation.Pump();

        Assert.Equal("Sid", DisplayNameOf(world, 1));
    }

    [Fact]
    public void NameArrivingBeforeTheAvatar_IsKeptAndApplied()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);
        var agent = Guid.NewGuid();

        session.RaiseDisplayNameResolved(new NameResolvedEvent(agent, "Sid"));
        simulation.Pump();
        Assert.Null(world.GetEntity(Region, 1)); // nobody to give it to yet

        session.RaiseAvatarUpdate(Update(agent, 1));
        simulation.Pump();

        Assert.Equal("Sid", DisplayNameOf(world, 1));
    }

    [Fact]
    public void AvatarThatLeavesAndComesBack_KeepsItsName_WithoutBeingAskedAgain()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);
        var agent = Guid.NewGuid();

        session.RaiseAvatarUpdate(Update(agent, 1));
        session.RaiseDisplayNameResolved(new NameResolvedEvent(agent, "Sid"));
        simulation.Pump();
        Assert.Equal("Sid", DisplayNameOf(world, 1));

        session.RaiseObjectRemoved(new ObjectRemovedEvent(Region, 1));
        simulation.Pump();
        Assert.Null(world.GetEntity(Region, 1));

        // Back, under a new local id, and no second lookup happens for this agent.
        session.RaiseAvatarUpdate(Update(agent, 7));
        simulation.Pump();

        Assert.Equal("Sid", DisplayNameOf(world, 7));
    }

    [Fact]
    public void NamesAreKeptPerAgent()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);
        var sid = Guid.NewGuid();
        var shiva = Guid.NewGuid();

        session.RaiseDisplayNameResolved(new NameResolvedEvent(sid, "Sid"));
        session.RaiseAvatarUpdate(Update(shiva, 2));
        session.RaiseAvatarUpdate(Update(sid, 1));
        simulation.Pump();

        Assert.Equal("Sid", DisplayNameOf(world, 1));
        Assert.Equal(string.Empty, DisplayNameOf(world, 2)); // no name known for this one
    }
}
