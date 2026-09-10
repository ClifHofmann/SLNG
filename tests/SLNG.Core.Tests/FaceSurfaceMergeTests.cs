using System;
using System.Numerics;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>BUG-RENDER-16: the surface-merge planner.
///
/// The merge is only safe because two merged faces provably cannot render differently, so the
/// tests that matter here are the MATERIAL-KEY EQUALITY ones: every field a material is built
/// from must break a run. The planner is pure and lives in Core precisely so this is testable —
/// the mesh build it feeds is Godot-side and is not.</summary>
public class FaceSurfaceMergeTests
{
    private static readonly Guid TexA = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TexB = new("bbbbbbbb-0000-0000-0000-000000000002");

    private static FaceTexture Face(
        Guid? texture = null, Guid? renderMaterial = null, Guid? legacyMaterial = null,
        Vector4? color = null, float repeatU = 1f, float repeatV = 1f,
        float offsetU = 0f, float offsetV = 0f, float rotation = 0f,
        byte texGen = 0, bool fullbright = false) =>
        new(texture ?? TexA, renderMaterial ?? Guid.Empty, legacyMaterial ?? Guid.Empty,
            color ?? new Vector4(1, 1, 1, 1), repeatU, repeatV, offsetU, offsetV, rotation,
            texGen, fullbright);

    private static (int Surfaces, bool[] RunStart) Plan(
        int[] submeshFaceIndices, FaceTexture[]? faces,
        FaceTexture? defaultFace = null, int animatedFace = FaceSurfaceMerge.NoAnimatedFace)
    {
        var runStart = new bool[submeshFaceIndices.Length];
        int surfaces = FaceSurfaceMerge.Plan(
            submeshFaceIndices, faces, defaultFace ?? Face(), animatedFace, runStart);
        return (surfaces, runStart);
    }

    // ---- the case this bug is about -------------------------------------------------------

    [Fact]
    public void IdenticalConsecutiveFaces_CollapseToOneSurface()
    {
        // The measured shape of the problem: a grass/hair mesh whose faces all carry the same
        // texture and tint, handed to Godot as N independently-reorderable transparent surfaces.
        var faces = new[] { Face(), Face(), Face(), Face(), Face(), Face() };
        var (surfaces, runStart) = Plan(new[] { 0, 1, 2, 3, 4, 5 }, faces);

        Assert.Equal(1, surfaces);
        Assert.Equal(new[] { true, false, false, false, false, false }, runStart);
    }

    [Fact]
    public void MultiMaterialMesh_MergesOnlyTheMatchingRuns()
    {
        // A, A, B, A -- the trailing A must NOT join the leading run: only consecutive matches
        // merge, because reordering non-adjacent triangles is the thing being avoided.
        var faces = new[] { Face(TexA), Face(TexA), Face(TexB), Face(TexA) };
        var (surfaces, runStart) = Plan(new[] { 0, 1, 2, 3 }, faces);

        Assert.Equal(3, surfaces);
        Assert.Equal(new[] { true, false, true, true }, runStart);
    }

    [Fact]
    public void NothingToMerge_IsIdentity()
    {
        var faces = new[] { Face(TexA), Face(TexB), Face(TexA) };
        var (surfaces, runStart) = Plan(new[] { 0, 1, 2 }, faces);

        Assert.Equal(3, surfaces);
        Assert.True(FaceSurfaceMerge.IsIdentity(runStart, runStart.Length));
    }

    [Fact]
    public void MergedPlan_IsNotIdentity()
    {
        var faces = new[] { Face(), Face() };
        var (_, runStart) = Plan(new[] { 0, 1 }, faces);

        Assert.False(FaceSurfaceMerge.IsIdentity(runStart, runStart.Length));
    }

    // ---- material-key equality: every field a material is built from must break a run -----

    public static TheoryData<string, FaceTexture> DifferingFaces() => new()
    {
        { "texture id", Face(texture: TexB) },
        { "glTF render material", Face(renderMaterial: TexB) },
        { "legacy Blinn-Phong material", Face(legacyMaterial: TexB) },
        { "colour tint", Face(color: new Vector4(1, 0, 0, 1)) },
        { "tint alpha", Face(color: new Vector4(1, 1, 1, 0.5f)) },
        { "repeat U", Face(repeatU: 2f) },
        { "repeat V", Face(repeatV: 2f) },
        { "offset U", Face(offsetU: 0.25f) },
        { "offset V", Face(offsetV: 0.25f) },
        { "rotation", Face(rotation: 1.5707964f) },
        { "texgen", Face(texGen: FaceTexture.TexGenPlanar) },
        { "fullbright", Face(fullbright: true) },
    };

    [Theory]
    [MemberData(nameof(DifferingFaces))]
    public void AnyMaterialRelevantDifference_BreaksTheRun(string what, FaceTexture different)
    {
        var faces = new[] { Face(), different };
        var (surfaces, runStart) = Plan(new[] { 0, 1 }, faces);

        Assert.False(Face().Equals(different), what + " must not compare equal");
        Assert.Equal(2, surfaces);
        Assert.Equal(new[] { true, true }, runStart);
    }

    [Fact]
    public void FaceTextureEqualityCoversEveryConstructorField()
    {
        // Guards the assumption the whole merge rests on: FaceTexture is a record struct, so
        // equality is generated over its fields. If a field is ever added without a case in
        // DifferingFaces above, two faces differing only in it would merge and render wrong.
        // Count - 1 because Color contributes two cases (colour and its alpha).
        var ctor = Assert.Single(typeof(FaceTexture).GetConstructors());
        Assert.Equal(ctor.GetParameters().Length, DifferingFaces().Count - 1);
    }

    // ---- faces outside the array fall back to the default face ----------------------------

    [Fact]
    public void FacesOutsideTheArray_ResolveToTheDefaultFace_AndMergeWithIt()
    {
        // A mesh with more submeshes than the prim has face records: the renderer's material
        // build falls back to the prim's default face for those, so the planner must too --
        // otherwise it would predict a different material than the one actually applied.
        var faces = new[] { Face(TexB) };
        var (surfaces, runStart) = Plan(new[] { 0, 1, 2 }, faces, defaultFace: Face(TexA));

        Assert.Equal(2, surfaces);
        Assert.Equal(new[] { true, true, false }, runStart);
    }

    [Fact]
    public void NullFaceArray_MergesEverythingOntoTheDefaultFace()
    {
        var (surfaces, _) = Plan(new[] { 0, 1, 2, 3 }, faces: null, defaultFace: Face(TexA));
        Assert.Equal(1, surfaces);
    }

    [Fact]
    public void NegativeFaceIndex_ResolvesToTheDefaultFace()
    {
        Assert.Equal(Face(TexA), FaceSurfaceMerge.Resolve(new[] { Face(TexB) }, Face(TexA), -1));
    }

    // ---- per-face texture animation is a merge barrier ------------------------------------

    [Fact]
    public void AnAnimatedFace_IsNeverMergedWithItsNeighbours()
    {
        // Four identical faces; face 1 is driven by llSetTextureAnim. Merging it into a run
        // would animate the static faces alongside it, so it has to stand alone -- and so does
        // the run that follows it.
        var faces = new[] { Face(), Face(), Face(), Face() };
        var (surfaces, runStart) = Plan(new[] { 0, 1, 2, 3 }, faces, animatedFace: 1);

        Assert.Equal(3, surfaces);
        Assert.Equal(new[] { true, true, true, false }, runStart);
    }

    [Fact]
    public void AnAllFacesAnimation_NeedsNoBarrier()
    {
        // Wire face 255 / -1 drives every face together, so a merged run animates correctly.
        var faces = new[] { Face(), Face(), Face() };
        var (surfaces, _) = Plan(new[] { 0, 1, 2 }, faces, animatedFace: FaceSurfaceMerge.NoAnimatedFace);

        Assert.Equal(1, surfaces);
    }

    // ---- the exact-signature guard --------------------------------------------------------

    [Fact]
    public void Signature_DistinguishesDifferentRunPatterns()
    {
        var a = new[] { true, false, true, true };
        var b = new[] { true, true, false, true };

        Assert.NotEqual(FaceSurfaceMerge.Signature(a, a.Length), FaceSurfaceMerge.Signature(b, b.Length));
        Assert.Equal(0b1101UL, FaceSurfaceMerge.Signature(a, a.Length));
    }

    [Fact]
    public void BeyondTheBitmaskLimit_NothingMerges()
    {
        // The cache key that keeps a merged shared mesh away from an instance with different
        // texturing is an exact 64-bit mask. Past that it cannot stay exact, and a collision
        // would render the wrong geometry -- so the planner declines to merge at all.
        int n = FaceSurfaceMerge.MaxMergeableSubmeshes + 1;
        var faceIndices = new int[n];
        var faces = new FaceTexture[n];
        for (int i = 0; i < n; i++) { faceIndices[i] = i; faces[i] = Face(); }

        var (surfaces, runStart) = Plan(faceIndices, faces);

        Assert.Equal(n, surfaces);
        Assert.True(FaceSurfaceMerge.IsIdentity(runStart, n));
    }

    [Fact]
    public void AtTheBitmaskLimit_MergingStillHappens()
    {
        int n = FaceSurfaceMerge.MaxMergeableSubmeshes;
        var faceIndices = new int[n];
        var faces = new FaceTexture[n];
        for (int i = 0; i < n; i++) { faceIndices[i] = i; faces[i] = Face(); }

        var (surfaces, runStart) = Plan(faceIndices, faces);

        Assert.Equal(1, surfaces);
        Assert.Equal(1UL, FaceSurfaceMerge.Signature(runStart, n));
    }

    [Fact]
    public void EmptyMesh_PlansNoSurfaces()
    {
        var (surfaces, _) = Plan(Array.Empty<int>(), Array.Empty<FaceTexture>());
        Assert.Equal(0, surfaces);
    }
}
