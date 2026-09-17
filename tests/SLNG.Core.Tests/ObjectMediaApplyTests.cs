using System;
using System.Numerics;
using SLNG.Core.Components;
using SLNG.Core.ECS;
using SLNG.Net;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>MVP3-3 Phase 1: WorldSimulation applying a completed <c>ObjectMedia</c> fetch onto the
/// world -- the last hop of ObjectUpdate doorbell -&gt; GridSession fetch -&gt; PrimitiveComponent.
/// Uses the same internal Raise* test seam as <see cref="FaceTexGenTests"/> (GridSession/
/// WorldSimulation wired together, no live LMV connection).</summary>
public class ObjectMediaApplyTests
{
    [Fact]
    public void ObjectMediaEvent_PopulatesMediaFacesAndVersion_OnAKnownEntity()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        session.RaiseObjectUpdate(new ObjectUpdateEvent(
            123ul, 42, Vector3.Zero, Quaternion.Identity, Vector3.One,
            1, false, Guid.Empty, Guid.Empty, Guid.Empty, Vector4.One));
        simulation.Pump();

        var mediaFace = new MediaFace(HomeUrl: "https://example.com/", CurrentUrl: "https://example.com/");
        session.RaiseObjectMedia(new ObjectMediaEvent(
            123ul, 42, Guid.NewGuid(), "x-mv:0000000001/11111111-1111-1111-1111-111111111111",
            new MediaFace?[] { null, mediaFace }));
        simulation.Pump();

        var prim = world.GetEntity(123ul, 42)!.GetComponent<PrimitiveComponent>()!;
        Assert.Equal("x-mv:0000000001/11111111-1111-1111-1111-111111111111", prim.MediaVersion);
        Assert.NotNull(prim.MediaFaces);
        Assert.Null(prim.MediaFaces![0]);
        Assert.Equal("https://example.com/", prim.MediaFaces[1]!.Value.CurrentUrl);
    }

    [Fact]
    public void ObjectMediaEvent_ForAnUnknownEntity_IsSilentlyDropped()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        // No matching ObjectUpdate was ever raised for (123, 99) -- e.g. the fetch completed
        // after the prim already left the world. Must not throw.
        session.RaiseObjectMedia(new ObjectMediaEvent(
            123ul, 99, Guid.NewGuid(), "x-mv:0000000001/11111111-1111-1111-1111-111111111111",
            Array.Empty<MediaFace?>()));
        simulation.Pump();

        Assert.Null(world.GetEntity(123ul, 99));
    }
}
