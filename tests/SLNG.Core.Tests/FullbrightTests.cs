using System;
using System.Numerics;
using SLNG.Core.Components;
using SLNG.Core.ECS;
using SLNG.Net;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>FEAT-RENDER-06. SL's per-face fullbright flag must survive the trip from the wire to
/// the renderer — same "a field was added but never read" hazard <see cref="FaceTexGenTests"/>
/// exists for. Pins both carriers: the per-face array and the default-face fallback.</summary>
public class FullbrightTests
{
    [Fact]
    public void DefaultFaceFullbright_ReachesThePrimitiveComponent()
    {
        // A prim whose faces are all identical sends NO per-face entries, so the default face is
        // the only carrier of its fullbright flag.
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        session.RaiseObjectUpdate(new ObjectUpdateEvent(
            123ul, 60, Vector3.Zero, Quaternion.Identity, Vector3.One,
            1, false, Guid.Empty, Guid.Empty, Guid.Empty, Vector4.One,
            Fullbright: true));
        simulation.Pump();

        var prim = world.GetEntity(123ul, 60)!.GetComponent<PrimitiveComponent>()!;
        Assert.True(prim.Fullbright);
    }

    [Fact]
    public void Fullbright_DefaultsFalse_WhenTheUpdateOmitsIt()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        session.RaiseObjectUpdate(new ObjectUpdateEvent(
            123ul, 61, Vector3.Zero, Quaternion.Identity, Vector3.One,
            1, false, Guid.Empty, Guid.Empty, Guid.Empty, Vector4.One));
        simulation.Pump();

        var prim = world.GetEntity(123ul, 61)!.GetComponent<PrimitiveComponent>()!;
        Assert.False(prim.Fullbright);
    }

    [Fact]
    public void PerFaceFullbright_IsCarriedOnEachFaceIndependently()
    {
        var faces = new[]
        {
            new FaceTexture(default, default, default, Vector4.One, 1f, 1f, 0f, 0f, 0f, 0, Fullbright: true),
            new FaceTexture(default, default, default, Vector4.One, 1f, 1f, 0f, 0f, 0f, 0, Fullbright: false),
        };

        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        session.RaiseObjectUpdate(new ObjectUpdateEvent(
            123ul, 62, Vector3.Zero, Quaternion.Identity, Vector3.One,
            1, false, Guid.Empty, Guid.Empty, Guid.Empty, Vector4.One,
            Faces: faces));
        simulation.Pump();

        var prim = world.GetEntity(123ul, 62)!.GetComponent<PrimitiveComponent>()!;
        Assert.NotNull(prim.Faces);
        Assert.True(prim.Faces![0].Fullbright);
        Assert.False(prim.Faces[1].Fullbright);
    }
}
