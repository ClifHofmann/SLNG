using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using SLNG.Core.Components;
using SLNG.Core.ECS;
using SLNG.Net;
using Xunit;

namespace SLNG.Core.Tests;

public class GltfOverrideWorldTests
{
    private static readonly Guid MatA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid MatB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static FaceTexture Face(Guid renderMatId) => new(
        TextureId: Guid.NewGuid(),
        RenderMaterialId: renderMatId,
        LegacyMaterialId: Guid.Empty,
        Color: Vector4.One,
        RepeatU: 1f, RepeatV: 1f, OffsetU: 0f, OffsetV: 0f, Rotation: 0f, TexGen: 0, Fullbright: false
    );

    private static ObjectUpdateEvent MakeUpdate(
        ulong region, uint localId, FaceTexture[]? faces, Guid renderMatId = default)
    {
        return new ObjectUpdateEvent(
            RegionHandle: region,
            LocalId: localId,
            Position: Vector3.Zero,
            Rotation: Quaternion.Identity,
            Scale: Vector3.One,
            ProfileCurve: 1,
            IsMesh: false,
            MeshId: Guid.Empty,
            TextureId: Guid.NewGuid(),
            RenderMaterialId: renderMatId == default ? MatA : renderMatId,
            ColorTint: Vector4.One,
            Faces: faces
        );
    }

    [Fact]
    public void Test1_ObjectFirst_OverrideAfter()
    {
        var world = new World();
        using var session = new GridSession();
        using var sim = new WorldSimulation(world, session);

        var faces = new[] { Face(MatA), Face(MatA) };
        session.RaiseObjectUpdate(MakeUpdate(100ul, 10, faces));
        sim.Pump();

        int updateCount = 0;
        world.ComponentUpdated += (sender, args) =>
        {
            if (args.Component is PrimitiveComponent) updateCount++;
        };

        var ov1 = new GltfMaterialOverride { Metallic = 0.75f };
        session.RaiseGltfMaterialOverride(new GltfMaterialOverrideEvent(
            100ul, 10, new Dictionary<int, GltfMaterialOverride> { [1] = ov1 }));
        sim.Pump();

        var prim = world.GetEntity(100ul, 10)!.GetComponent<PrimitiveComponent>()!;
        Assert.Null(prim.Faces![0].PbrOverride);
        Assert.Equal(ov1, prim.Faces[1].PbrOverride);
        Assert.Equal(1, updateCount);
    }

    [Fact]
    public void Test2_OverrideFirst_ObjectAfter()
    {
        var world = new World();
        using var session = new GridSession();
        using var sim = new WorldSimulation(world, session);

        var ov1 = new GltfMaterialOverride { Metallic = 0.75f };
        session.RaiseGltfMaterialOverride(new GltfMaterialOverrideEvent(
            100ul, 10, new Dictionary<int, GltfMaterialOverride> { [1] = ov1 }));
        sim.Pump();

        var faces = new[] { Face(MatA), Face(MatA) };
        session.RaiseObjectUpdate(MakeUpdate(100ul, 10, faces));
        sim.Pump();

        var prim = world.GetEntity(100ul, 10)!.GetComponent<PrimitiveComponent>()!;
        Assert.Null(prim.Faces![0].PbrOverride);
        Assert.Equal(ov1, prim.Faces[1].PbrOverride);
    }

    [Fact]
    public void Test3_Replace_Removes_Old_Overrides()
    {
        var world = new World();
        using var session = new GridSession();
        using var sim = new WorldSimulation(world, session);

        var faces = new[] { Face(MatA), Face(MatA) };
        session.RaiseObjectUpdate(MakeUpdate(100ul, 10, faces));
        sim.Pump();

        var ov1 = new GltfMaterialOverride { Metallic = 0.5f };
        session.RaiseGltfMaterialOverride(new GltfMaterialOverrideEvent(
            100ul, 10, new Dictionary<int, GltfMaterialOverride> { [1] = ov1 }));
        sim.Pump();

        var ov0 = new GltfMaterialOverride { Roughness = 0.2f };
        session.RaiseGltfMaterialOverride(new GltfMaterialOverrideEvent(
            100ul, 10, new Dictionary<int, GltfMaterialOverride> { [0] = ov0 }));
        sim.Pump();

        var prim = world.GetEntity(100ul, 10)!.GetComponent<PrimitiveComponent>()!;
        Assert.Equal(ov0, prim.Faces![0].PbrOverride);
        Assert.Null(prim.Faces[1].PbrOverride);
    }

    [Fact]
    public void Test4_ClearAll_Removes_Overrides_And_Clears_Store()
    {
        var world = new World();
        using var session = new GridSession();
        using var sim = new WorldSimulation(world, session);

        var faces = new[] { Face(MatA), Face(MatA) };
        session.RaiseObjectUpdate(MakeUpdate(100ul, 10, faces));
        sim.Pump();

        var ov1 = new GltfMaterialOverride { Metallic = 0.5f };
        session.RaiseGltfMaterialOverride(new GltfMaterialOverrideEvent(
            100ul, 10, new Dictionary<int, GltfMaterialOverride> { [1] = ov1 }));
        sim.Pump();

        int notified = 0;
        world.ComponentUpdated += (s, a) => { if (a.Component is PrimitiveComponent) notified++; };

        // Clear all message
        session.RaiseGltfMaterialOverride(new GltfMaterialOverrideEvent(
            100ul, 10, new Dictionary<int, GltfMaterialOverride>()));
        sim.Pump();

        var prim = world.GetEntity(100ul, 10)!.GetComponent<PrimitiveComponent>()!;
        Assert.Null(prim.Faces![0].PbrOverride);
        Assert.Null(prim.Faces[1].PbrOverride);
        Assert.Equal(1, notified);

        // A second object update confirms nothing is re-stamped
        var cleanFaces = new[] { Face(MatA), Face(MatA) };
        session.RaiseObjectUpdate(MakeUpdate(100ul, 10, cleanFaces));
        sim.Pump();
        Assert.Same(cleanFaces, prim.Faces);
    }

    [Fact]
    public void Test5_Later_ObjectUpdate_Keeps_Override_And_Equal_Values()
    {
        var world = new World();
        using var session = new GridSession();
        using var sim = new WorldSimulation(world, session);

        var faces = new[] { Face(MatA), Face(MatA) };
        session.RaiseObjectUpdate(MakeUpdate(100ul, 10, faces));
        sim.Pump();

        var ov1 = new GltfMaterialOverride { Metallic = 0.5f };
        session.RaiseGltfMaterialOverride(new GltfMaterialOverrideEvent(
            100ul, 10, new Dictionary<int, GltfMaterialOverride> { [1] = ov1 }));
        sim.Pump();

        var prim = world.GetEntity(100ul, 10)!.GetComponent<PrimitiveComponent>()!;
        var stampedBefore = prim.Faces!.ToArray();

        // Later update arrives with same face parameters
        session.RaiseObjectUpdate(MakeUpdate(100ul, 10, faces));
        sim.Pump();

        Assert.NotNull(prim.Faces);
        Assert.True(prim.Faces!.SequenceEqual(stampedBefore));
    }

    [Fact]
    public void Test6_ObjectUpdate_Changing_RenderMaterialId_Keeps_Override()
    {
        var world = new World();
        using var session = new GridSession();
        using var sim = new WorldSimulation(world, session);

        var faces = new[] { Face(MatA), Face(MatA) };
        session.RaiseObjectUpdate(MakeUpdate(100ul, 10, faces, renderMatId: MatA));
        sim.Pump();

        var ov1 = new GltfMaterialOverride { Metallic = 0.5f };
        session.RaiseGltfMaterialOverride(new GltfMaterialOverrideEvent(
            100ul, 10, new Dictionary<int, GltfMaterialOverride> { [1] = ov1 }));
        sim.Pump();

        // New render-material id
        var newFaces = new[] { Face(MatB), Face(MatB) };
        session.RaiseObjectUpdate(MakeUpdate(100ul, 10, newFaces, renderMatId: MatB));
        sim.Pump();

        var prim = world.GetEntity(100ul, 10)!.GetComponent<PrimitiveComponent>()!;
        Assert.Equal(MatB, prim.RenderMaterialId);
        Assert.Equal(ov1, prim.Faces![1].PbrOverride);
    }

    [Fact]
    public void Test7_Kill_Drops_Override()
    {
        var world = new World();
        using var session = new GridSession();
        using var sim = new WorldSimulation(world, session);

        var faces = new[] { Face(MatA), Face(MatA) };
        session.RaiseObjectUpdate(MakeUpdate(100ul, 10, faces));
        sim.Pump();

        var ov1 = new GltfMaterialOverride { Metallic = 0.5f };
        session.RaiseGltfMaterialOverride(new GltfMaterialOverrideEvent(
            100ul, 10, new Dictionary<int, GltfMaterialOverride> { [1] = ov1 }));
        sim.Pump();

        // Kill object
        session.RaiseObjectRemoved(new ObjectRemovedEvent(100ul, 10));
        sim.Pump();

        // New object with same local id
        session.RaiseObjectUpdate(MakeUpdate(100ul, 10, faces));
        sim.Pump();

        var prim = world.GetEntity(100ul, 10)!.GetComponent<PrimitiveComponent>()!;
        Assert.Null(prim.Faces![1].PbrOverride);
    }

    [Fact]
    public void Test8_RegionDisconnect_Drops_Held_Overrides_For_That_Region()
    {
        var world = new World();
        using var session = new GridSession();
        using var sim = new WorldSimulation(world, session);

        var ov = new GltfMaterialOverride { Metallic = 0.5f };
        session.RaiseGltfMaterialOverride(new GltfMaterialOverrideEvent(
            100ul, 10, new Dictionary<int, GltfMaterialOverride> { [0] = ov }));
        session.RaiseGltfMaterialOverride(new GltfMaterialOverrideEvent(
            200ul, 20, new Dictionary<int, GltfMaterialOverride> { [0] = ov }));
        sim.Pump();

        // Disconnect region 100
        session.RaiseRegionDisconnected(new RegionDisconnectedEvent(100ul));
        sim.Pump();

        // Object on region 100 arrives later: override was dropped
        var faces = new[] { Face(MatA) };
        session.RaiseObjectUpdate(MakeUpdate(100ul, 10, faces));
        // Object on region 200 arrives later: override kept
        session.RaiseObjectUpdate(MakeUpdate(200ul, 20, faces));
        sim.Pump();

        var prim100 = world.GetEntity(100ul, 10)!.GetComponent<PrimitiveComponent>()!;
        Assert.Null(prim100.Faces![0].PbrOverride);

        var prim200 = world.GetEntity(200ul, 20)!.GetComponent<PrimitiveComponent>()!;
        Assert.Equal(ov, prim200.Faces![0].PbrOverride);
    }

    [Fact]
    public void Test9_NullFaces_GrowsArray_WithDefaultFaceCopies()
    {
        var world = new World();
        using var session = new GridSession();
        using var sim = new WorldSimulation(world, session);

        var ov2 = new GltfMaterialOverride { Metallic = 0.5f };
        session.RaiseGltfMaterialOverride(new GltfMaterialOverrideEvent(
            100ul, 10, new Dictionary<int, GltfMaterialOverride> { [2] = ov2 }));
        sim.Pump();

        // Object update with Faces == null
        session.RaiseObjectUpdate(MakeUpdate(100ul, 10, faces: null));
        sim.Pump();

        var prim = world.GetEntity(100ul, 10)!.GetComponent<PrimitiveComponent>()!;
        Assert.NotNull(prim.Faces);
        Assert.Equal(3, prim.Faces!.Length);
        Assert.Equal(prim.DefaultFace(), prim.Faces[0]);
        Assert.Equal(prim.DefaultFace(), prim.Faces[1]);
        Assert.Equal(ov2, prim.Faces[2].PbrOverride);
    }

    [Fact]
    public void Test10_Prim_NeverHadOverride_Keeps_EventArray()
    {
        var world = new World();
        using var session = new GridSession();
        using var sim = new WorldSimulation(world, session);

        var eventFaces = new[] { Face(MatA), Face(MatA) };
        session.RaiseObjectUpdate(MakeUpdate(100ul, 10, eventFaces));
        sim.Pump();

        var prim = world.GetEntity(100ul, 10)!.GetComponent<PrimitiveComponent>()!;
        Assert.Same(eventFaces, prim.Faces);
        Assert.False(prim.HasPbrOverrides);
    }

    [Fact]
    public void Test11_GltfOverrideLimit_Drops_UnknownObject_Keeps_LiveObject()
    {
        var world = new World();
        using var session = new GridSession();
        using var sim = new WorldSimulation(world, session);
        sim.GltfOverrideLimitOverride = 2; // set low for test

        var ov = new GltfMaterialOverride { Metallic = 0.5f };

        // 1 & 2 are unknown objects (held)
        session.RaiseGltfMaterialOverride(new GltfMaterialOverrideEvent(
            100ul, 1, new Dictionary<int, GltfMaterialOverride> { [0] = ov }));
        session.RaiseGltfMaterialOverride(new GltfMaterialOverrideEvent(
            100ul, 2, new Dictionary<int, GltfMaterialOverride> { [0] = ov }));
        sim.Pump();

        // 3rd is an unknown object, exceeds limit -> dropped
        session.RaiseGltfMaterialOverride(new GltfMaterialOverrideEvent(
            100ul, 3, new Dictionary<int, GltfMaterialOverride> { [0] = ov }));
        sim.Pump();

        // 4 is a LIVE object
        var faces = new[] { Face(MatA) };
        session.RaiseObjectUpdate(MakeUpdate(100ul, 4, faces));
        sim.Pump();

        // Override for live object 4 is accepted even if limit reached
        session.RaiseGltfMaterialOverride(new GltfMaterialOverrideEvent(
            100ul, 4, new Dictionary<int, GltfMaterialOverride> { [0] = ov }));
        sim.Pump();

        // When 3 arrives later, it has no override
        session.RaiseObjectUpdate(MakeUpdate(100ul, 3, faces));
        sim.Pump();

        var prim3 = world.GetEntity(100ul, 3)!.GetComponent<PrimitiveComponent>()!;
        Assert.Null(prim3.Faces![0].PbrOverride);

        var prim4 = world.GetEntity(100ul, 4)!.GetComponent<PrimitiveComponent>()!;
        Assert.Equal(ov, prim4.Faces![0].PbrOverride);
    }
}
