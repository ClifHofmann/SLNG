using System;
using System.Numerics;
using SLNG.Core.Components;
using SLNG.Core.ECS;
using SLNG.Net;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>SL's texgen must survive the trip from the wire to the renderer.
///
/// These exist because the field was added, documented and logged without ever being read
/// correctly: <c>FaceTexture.TexGen</c> stores SL's raw enum (planar = 2) while both its own
/// doc comment and the renderer's diagnostic compared it against 1. The result was a planar
/// face that reported itself as "default", so the missing projection never showed up in a log.
/// A constant is easy to get wrong twice; these tests pin the value and the plumbing.</summary>
public class FaceTexGenTests
{
    [Fact]
    public void PlanarIsTwo_NotOne()
    {
        // LLTextureEntry::eTexGen (lltextureentry.h:79) and LibreMetaverse's MappingType
        // (TextureEntry.cs:98) both define TEX_GEN_PLANAR = 0x02.
        Assert.Equal(0, FaceTexture.TexGenDefault);
        Assert.Equal(2, FaceTexture.TexGenPlanar);
    }

    [Theory]
    [InlineData((byte)0, false)]
    [InlineData((byte)2, true)]
    [InlineData((byte)4, false)]   // spherical -- not implemented, must not select planar
    [InlineData((byte)6, false)]   // cylindrical -- likewise
    public void IsPlanar_OnlyForTheWireValueTwo(byte texGen, bool expected)
    {
        var face = new FaceTexture(default, default, default, Vector4.One, 1f, 1f, 0f, 0f, 0f, texGen);
        Assert.Equal(expected, face.IsPlanar);
    }

    [Fact]
    public void DefaultFaceTexGen_ReachesThePrimitiveComponent()
    {
        // A prim whose faces are all identical sends NO per-face entries, so the default face is
        // the only carrier of its texgen. This path had no texgen field at all.
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        session.RaiseObjectUpdate(new ObjectUpdateEvent(
            123ul, 42, Vector3.Zero, Quaternion.Identity, new Vector3(2, 2, 2),
            1, false, Guid.Empty, Guid.Empty, Guid.Empty, Vector4.One,
            TexGen: FaceTexture.TexGenPlanar));
        simulation.Pump();

        var prim = world.GetEntity(123ul, 42)!.GetComponent<PrimitiveComponent>()!;
        Assert.Equal(FaceTexture.TexGenPlanar, prim.TexGen);
    }

    [Fact]
    public void TexGenDefaultsToZero_WhenTheUpdateOmitsIt()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        session.RaiseObjectUpdate(new ObjectUpdateEvent(
            123ul, 43, Vector3.Zero, Quaternion.Identity, Vector3.One,
            1, false, Guid.Empty, Guid.Empty, Guid.Empty, Vector4.One));
        simulation.Pump();

        var prim = world.GetEntity(123ul, 43)!.GetComponent<PrimitiveComponent>()!;
        Assert.Equal(FaceTexture.TexGenDefault, prim.TexGen);
    }
}
