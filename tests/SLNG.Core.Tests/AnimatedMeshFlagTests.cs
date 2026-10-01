using System;
using System.Numerics;
using SLNG.Core.Components;
using SLNG.Core.ECS;
using SLNG.Net;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>FEAT-ANIMESH-01. The animated-mesh flag rides ExtraParams, which an
/// ImprovedTerseObjectUpdate does not carry, so it has the same staleness hazard as the light and
/// reflection-probe fields: applying a terse-sourced "false" would drop an animesh back to a
/// plain rigged mesh (lying on its side) every time it moved. The gate is the point of these
/// tests; the parse itself is covered in SLNG.Net.Tests.</summary>
public class AnimatedMeshFlagTests
{
    private const ulong Region = 123ul;

    private static ObjectUpdateEvent Update(uint localId, bool animatedMesh, bool isFullUpdate = true) =>
        new(Region, localId, Vector3.Zero, Quaternion.Identity, Vector3.One,
            1, true, Guid.NewGuid(), Guid.Empty, Guid.Empty, Vector4.One,
            IsFullUpdate: isFullUpdate, IsAnimatedMesh: animatedMesh);

    private static PrimitiveComponent Prim(World world, uint localId) =>
        world.GetEntity(Region, localId)!.GetComponent<PrimitiveComponent>()!;

    [Fact]
    public void DefaultsFalse_WhenTheUpdateOmitsIt()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        session.RaiseObjectUpdate(new ObjectUpdateEvent(
            Region, 70, Vector3.Zero, Quaternion.Identity, Vector3.One,
            1, true, Guid.NewGuid(), Guid.Empty, Guid.Empty, Vector4.One));
        simulation.Pump();

        Assert.False(Prim(world, 70).IsAnimatedMesh);
    }

    [Fact]
    public void FullUpdate_SetsTheFlagOnThePrimitiveComponent()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        session.RaiseObjectUpdate(Update(71, animatedMesh: true));
        simulation.Pump();

        Assert.True(Prim(world, 71).IsAnimatedMesh);
    }

    [Fact]
    public void FullUpdateWithoutTheBlock_ClearsTheFlag()
    {
        // The latch discipline: the sim omits the block when animesh is switched off, which the
        // network layer reports as false on a FULL update. That has to win.
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        session.RaiseObjectUpdate(Update(72, animatedMesh: true));
        simulation.Pump();
        session.RaiseObjectUpdate(Update(72, animatedMesh: false));
        simulation.Pump();

        Assert.False(Prim(world, 72).IsAnimatedMesh);
    }

    [Fact]
    public void TerseUpdate_NeitherSetsNorClearsTheFlag()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        session.RaiseObjectUpdate(Update(73, animatedMesh: true));
        simulation.Pump();

        // An animesh that starts walking: terse updates, which can only say "false".
        session.RaiseObjectUpdate(Update(73, animatedMesh: false, isFullUpdate: false));
        simulation.Pump();
        Assert.True(Prim(world, 73).IsAnimatedMesh);

        // And a terse-sourced "true" must not invent the flag on an object that is not animesh.
        session.RaiseObjectUpdate(Update(74, animatedMesh: false));
        simulation.Pump();
        session.RaiseObjectUpdate(Update(74, animatedMesh: true, isFullUpdate: false));
        simulation.Pump();
        Assert.False(Prim(world, 74).IsAnimatedMesh);
    }
}
