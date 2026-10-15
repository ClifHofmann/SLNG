using System;
using System.Numerics;
using Xunit;
using Kind = SLNG.Core.BatchPotentialTally.SurfaceKind;
using Reasons = SLNG.Core.BatchPotentialTally.PrivateReasons;

namespace SLNG.Core.Tests;

/// <summary>FEAT-PERF-20 Phase 0: the arithmetic behind the [BatchPotential] line. Each test is a
/// scene small enough to count by hand.</summary>
public class BatchPotentialTallyTests
{
    private static readonly Guid MeshA = new("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid MeshB = new("bbbbbbbb-0000-0000-0000-00000000000b");
    private static readonly Guid TexA = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TexB = new("bbbbbbbb-0000-0000-0000-000000000002");

    private static PrimMaterialKey KeyOf(Guid texture) =>
        PrimMaterialKey.Create(new FaceTexture(texture, Guid.Empty, Guid.Empty, Vector4.One, 1, 1, 0, 0, 0),
                               Vector3.One, variant: 1);

    private static ulong _nextInstance = 1000;

    // One face, one material object -- what the renderer builds today.
    private static BatchPotentialTally.Sample S(
        Guid mesh, int surface, Guid texture, Vector3 position,
        Kind kind = Kind.Opaque, Reasons priv = Reasons.None, bool inView = true,
        bool casts = true, bool isStatic = true, int layer = 0, bool depthPass = false, ulong? instance = null) =>
        new(mesh, surface, KeyOf(texture), instance ?? _nextInstance++, kind, priv, depthPass,
            inView, casts, isStatic, layer, position);

    [Fact]
    public void IdenticalFacesOnOneMesh_CollapseOnlyWhenTheyShareAMaterial()
    {
        var t = new BatchPotentialTally();
        for (int i = 0; i < 5; i++) t.Add(S(MeshA, 0, TexA, new Vector3(i, 0, 0)));

        var r = t.Compute();
        Assert.Equal(5, r.Main.Surfaces);
        Assert.Equal(5, r.Main.Now);       // five material objects: nothing merges today
        Assert.Equal(1, r.Main.Shared);    // one key, one mesh surface: one instanced draw
        Assert.Equal(1, r.Keys);
    }

    [Fact]
    public void DifferentMeshes_DoNotAutoInstance_ButMergePerCell()
    {
        var t = new BatchPotentialTally();
        t.Add(S(MeshA, 0, TexA, new Vector3(1, 0, 0)));
        t.Add(S(MeshB, 0, TexA, new Vector3(2, 0, 0)));
        t.Add(S(MeshB, 0, TexA, new Vector3(40, 0, 0)));   // next 32 m cell, same 64 m cell

        var r = t.Compute();
        Assert.Equal(2, r.Main.Shared);    // (A, tex) and (B, tex)
        Assert.Equal(2, r.Main.Cell32);    // two 32 m cells
        Assert.Equal(1, r.Main.Cell64);    // one 64 m cell
    }

    [Fact]
    public void DepthLayer_SplitsAutoInstancing_InTheMainPassOnly()
    {
        var t = new BatchPotentialTally();
        t.Add(S(MeshA, 0, TexA, Vector3.Zero, layer: 0));
        t.Add(S(MeshA, 0, TexA, Vector3.Zero, layer: 1));

        var r = t.Compute();
        Assert.Equal(2, r.Main.Shared);
        Assert.Equal(1, r.Shadow.Shared);
    }

    [Fact]
    public void PrivateSurfaces_KeepOneDrawEach_AndAreCountedByReason()
    {
        var t = new BatchPotentialTally();
        t.Add(S(MeshA, 0, TexA, Vector3.Zero));
        t.Add(S(MeshA, 0, TexA, Vector3.Zero, priv: Reasons.TextureAnimation));
        t.Add(S(MeshA, 0, TexA, Vector3.Zero, priv: Reasons.Selected | Reasons.Media));

        var r = t.Compute();
        Assert.Equal(3, r.Main.Shared);
        Assert.Equal(2, r.Private);
        Assert.Equal(1, r.PrivateAnim);
        Assert.Equal(1, r.PrivateSelected);
        Assert.Equal(1, r.PrivateMedia);
        Assert.Equal(0, r.PrivateMirror);
        Assert.Equal(1, r.StaticEligible);
        Assert.Equal(3, r.Main.Cell32);    // one cell batch + the two private ones as today
    }

    [Fact]
    public void BlendSurfaces_AreNeverBatched_AndCastNoShadow()
    {
        var t = new BatchPotentialTally();
        t.Add(S(MeshA, 0, TexA, Vector3.Zero, kind: Kind.Blend));
        t.Add(S(MeshA, 0, TexA, Vector3.Zero, kind: Kind.Blend, depthPass: true));

        var r = t.Compute();
        Assert.Equal(3, r.Main.Surfaces);  // two colour draws and one depth pass
        Assert.Equal(3, r.Main.Now);
        Assert.Equal(3, r.Main.Shared);
        Assert.Equal(3, r.Main.Both64);
        Assert.Equal(0, r.Shadow.Surfaces);
        Assert.Equal(2, r.Blend);
        Assert.Equal(1, r.DepthPasses);
    }

    [Fact]
    public void HiddenPlaceholders_ShareTheirOneMaterialAlready()
    {
        var t = new BatchPotentialTally();
        t.Add(S(MeshA, 1, TexA, Vector3.Zero, kind: Kind.Hidden, instance: 7));
        t.Add(S(MeshA, 1, TexB, Vector3.Zero, kind: Kind.Hidden, instance: 7));

        var r = t.Compute();
        Assert.Equal(1, r.Main.Now);
        Assert.Equal(1, r.Main.Shared);
        Assert.Equal(1, r.Shadow.Now);     // still a shadow draw: an opaque material casts
        Assert.Equal(2, r.Hidden);
    }

    [Fact]
    public void MovingObjects_StayOutOfTheCellMerge()
    {
        var t = new BatchPotentialTally();
        t.Add(S(MeshA, 0, TexA, Vector3.Zero, isStatic: false));
        t.Add(S(MeshB, 0, TexA, Vector3.Zero, isStatic: false));

        var r = t.Compute();
        Assert.Equal(0, r.StaticEligible);
        Assert.Equal(2, r.Main.Cell32);
        Assert.Equal(2, r.Main.Both32);
    }

    [Fact]
    public void CastersAndNonCasters_AreSeparateCellMeshes_AndOnlyCastersReachTheShadowPass()
    {
        var t = new BatchPotentialTally();
        t.Add(S(MeshA, 0, TexA, Vector3.Zero, casts: true));
        t.Add(S(MeshB, 0, TexA, Vector3.Zero, casts: false));

        var r = t.Compute();
        Assert.Equal(2, r.Main.Cell32);
        Assert.Equal(1, r.Shadow.Surfaces);
        Assert.Equal(1, r.Shadow.Cell32);
    }

    [Fact]
    public void OffScreenSurfaces_CountForTheShadowPassOnly()
    {
        var t = new BatchPotentialTally();
        t.Add(S(MeshA, 0, TexA, Vector3.Zero, inView: false));

        var r = t.Compute();
        Assert.Equal(0, r.Main.Surfaces);
        Assert.Equal(1, r.Shadow.Surfaces);
    }

    [Fact]
    public void Both_UsesSharedMaterialsForWhatTheCellsLeave()
    {
        var t = new BatchPotentialTally();
        t.Add(S(MeshA, 0, TexA, Vector3.Zero));                     // into a cell
        t.Add(S(MeshB, 0, TexB, Vector3.Zero, isStatic: false));    // moving, shareable
        t.Add(S(MeshB, 0, TexB, Vector3.One, isStatic: false));     // moving, same key and mesh

        var r = t.Compute();
        Assert.Equal(3, r.Main.Cell32);    // 1 cell batch + 2 movers as today
        Assert.Equal(2, r.Main.Both32);    // 1 cell batch + the movers auto-instanced
    }

    [Fact]
    public void Cell_FloorsNegativeCoordinates()
    {
        Assert.Equal((-1, 0, 0), BatchPotentialTally.Cell(new Vector3(-0.5f, 0, 31.9f), 32f));
        Assert.Equal((1, 0, 0), BatchPotentialTally.Cell(new Vector3(64f, 0, 0), 64f));
    }

    [Fact]
    public void Clear_StartsANewPass()
    {
        var t = new BatchPotentialTally();
        t.Add(S(MeshA, 0, TexA, Vector3.Zero));
        t.Clear();
        Assert.Equal(0, t.Count);
        Assert.Equal(0, t.Compute().Main.Surfaces);
    }
}
