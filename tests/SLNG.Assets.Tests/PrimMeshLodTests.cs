using System.IO;
using System.Linq;
using SLNG.Assets;
using SLNG.Core;
using SLNG.Net;
using Xunit;

namespace SLNG.Assets.Tests;

/// <summary>
/// Regression tests for the "crumpled/angular origami" LOD bug on fix/broken-mesh-objects.
///
/// A curved procedural prim (sphere/torus/ring -- any profile using ProfileCurve.Circle,
/// HalfCircle, or EqualTriangle) is tessellated with a side count tied directly to
/// <see cref="MeshDetailLevel"/> (LibreMetaverse.Rendering.DetailLevel underneath: 6/12/24
/// sides for Low/Medium/High+). <see cref="AssetService.GetPrimMeshAsync"/> used to have no LOD
/// parameter at all and always meshed at Medium regardless of the object's on-screen size -- a
/// heavily-scaled, nearby curved element (e.g. a half-sphere dome cut for a curved bench/wall
/// segment, the real reported case: Scale ~4.34x1.84x1.18, ProfileCurve=HalfCircle,
/// PathCurve=Circle) rendered as a visibly faceted, angular "origami" shape instead of a smooth
/// curve, where a real SL viewer's own distance/size-based LOD would show far more facets.
///
/// These tests pin down that GetPrimMeshAsync (a) actually honors a requested
/// <see cref="MeshDetailLevel"/> instead of silently always using Medium, and (b) caches by
/// (shape, lod) so two different LOD requests for the same shape don't collide and silently
/// return whichever was generated first.
/// </summary>
public class PrimMeshLodTests
{
    // The real reported shape: HalfCircle profile + Circle path, cut to half (PathEnd=0.5) --
    // a half-sphere/dome, the classic curved bench/wall-segment technique from pre-mesh SL
    // building. See docs/HANDOVER_CLAUDE.md-adjacent investigation notes on fix/broken-mesh-objects.
    private static PrimShape CurvedDomeShape() => new PrimShape(
        ProfileCurve: 0x05, // HalfCircle
        PathCurve: 0x20,    // Circle
        PathBegin: 0f, PathEnd: 0.5f,
        PathScaleX: 1f, PathScaleY: 1f,
        PathShearX: 0f, PathShearY: 0f,
        PathTaperX: 0f, PathTaperY: 0f,
        PathTwist: 0f, PathTwistBegin: 0f,
        PathRadiusOffset: 0f, PathSkew: 0f, PathRevolutions: 1f,
        ProfileBegin: 0f, ProfileEnd: 1f, ProfileHollow: 0f, PCode: 9);

    private static AssetService NewAssetService()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "slng-assets-tests-" + System.Guid.NewGuid());
        return new AssetService(new GridSession(), tempDir);
    }

    [Fact]
    public async System.Threading.Tasks.Task GetPrimMeshAsync_HigherLod_ProducesMoreTessellatedGeometry()
    {
        var service = NewAssetService();
        var shape = CurvedDomeShape();

        var medium = await service.GetPrimMeshAsync(shape, MeshDetailLevel.Medium);
        var highest = await service.GetPrimMeshAsync(shape, MeshDetailLevel.Highest);

        Assert.NotNull(medium);
        Assert.NotNull(highest);

        int mediumTris = medium!.Submeshes.Sum(s => s.Indices.Length / 3);
        int highestTris = highest!.Submeshes.Sum(s => s.Indices.Length / 3);

        // The exact reported shape: Medium=90 tris, Highest=324 tris (verified via a standalone
        // diagnostic run against PrimMeshService.Generate directly). Assert the direction and a
        // meaningful margin rather than the exact numbers, so this doesn't become brittle to
        // minor LibreMetaverse mesher tweaks.
        Assert.True(highestTris > mediumTris * 2,
            $"expected Highest LOD to be substantially more tessellated than Medium for a curved profile, got Medium={mediumTris} tris, Highest={highestTris} tris");
    }

    [Fact]
    public async System.Threading.Tasks.Task GetPrimMeshAsync_DifferentLodRequests_DoNotShareACacheEntry()
    {
        var service = NewAssetService();
        var shape = CurvedDomeShape();

        // Request Medium first (populating whatever cache entry exists for this shape), THEN
        // request Highest for the identical shape. If the cache were keyed on shape alone (the
        // pre-fix behavior), this second call would incorrectly return the already-cached Medium
        // result instead of actually generating Highest-detail geometry.
        var first = await service.GetPrimMeshAsync(shape, MeshDetailLevel.Medium);
        var second = await service.GetPrimMeshAsync(shape, MeshDetailLevel.Highest);

        Assert.NotNull(first);
        Assert.NotNull(second);

        int firstTris = first!.Submeshes.Sum(s => s.Indices.Length / 3);
        int secondTris = second!.Submeshes.Sum(s => s.Indices.Length / 3);
        Assert.NotEqual(firstTris, secondTris);

        // And a repeat request at the ORIGINAL lod must still return the original (cached)
        // result, not get clobbered by the intervening Highest request.
        var firstAgain = await service.GetPrimMeshAsync(shape, MeshDetailLevel.Medium);
        Assert.Equal(firstTris, firstAgain!.Submeshes.Sum(s => s.Indices.Length / 3));
    }

    [Fact]
    public async System.Threading.Tasks.Task GetPrimMeshAsync_DefaultLod_IsMedium_UnchangedBehaviorForCallersThatDontCare()
    {
        var service = NewAssetService();
        var shape = CurvedDomeShape();

        var byDefault = await service.GetPrimMeshAsync(shape);
        var explicitMedium = await service.GetPrimMeshAsync(shape, MeshDetailLevel.Medium);

        Assert.NotNull(byDefault);
        Assert.NotNull(explicitMedium);
        Assert.Equal(
            explicitMedium!.Submeshes.Sum(s => s.Indices.Length / 3),
            byDefault!.Submeshes.Sum(s => s.Indices.Length / 3));
    }
}
