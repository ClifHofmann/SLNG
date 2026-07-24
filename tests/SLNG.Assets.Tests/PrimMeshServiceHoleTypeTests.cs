using System;
using System.Linq;
using SLNG.Assets;
using SLNG.Core;
using Xunit;

namespace SLNG.Assets.Tests;

/// <summary>
/// Regression tests for the ProfileCurve/HoleType packed-byte bug found while investigating the
/// "crumpled/angular origami" prim-geometry report on fix/broken-mesh-objects.
///
/// LibreMetaverse's <c>Primitive.ConstructionData.profileCurve</c> is a single packed byte: the
/// low nibble is the outer profile curve type (Circle/Square/Triangle/...), the high nibble is the
/// hollow cut's OWN shape (HoleType Same/Circle/Square/Triangle, pre-shifted into
/// 0x00/0x10/0x20/0x30 — see LibreMetaverse.Types.EnumsPrimitive). <c>SLNG.Net.GridSession</c> used
/// to read the masked <c>ConstructionData.ProfileCurve</c> PROPERTY (which zeroes the high nibble)
/// instead of the raw field when building <see cref="PrimShape"/>, so every hollow prim's hole
/// shape silently collapsed to HoleType.Same (0x00) regardless of what the creator actually chose.
///
/// These tests pin down that PrimMeshService.Generate() itself handles a fully-packed byte (both
/// nibbles) correctly and produces bounded, non-degenerate geometry — the fix is in GridSession
/// (not exercised here directly, since it requires a live LibreMetaverse Simulator/Primitive), but
/// this guards the consuming side of the contract: PrimShape.ProfileCurve must be treated, end to
/// end, as the raw packed byte, not a masked curve-only value.
/// </summary>
public class PrimMeshServiceHoleTypeTests
{
    // ProfileCurve.Square (0x01) | HoleType.Circle (0x10) -- a square outer profile with a round
    // hollow core, e.g. a square architectural pillar cored out with a round hole. This only
    // differs from a HoleType.Same square hollow in the *shape* of the inner hole, so a mesher
    // that silently drops the HoleType nibble would still produce a plausible-looking (but wrong)
    // mesh here rather than an outright crash -- which is exactly why this bug went unnoticed
    // until visually compared against a reference viewer.
    private const byte SquareProfileWithRoundHole = 0x01 | 0x10;
    private const byte SquareProfileWithSameHole = 0x01; // high nibble 0x00 = HoleType.Same

    private static PrimShape HollowBoxShape(byte packedProfileCurve) => new PrimShape(
        ProfileCurve: packedProfileCurve,
        PathCurve: 0x10, // Line
        PathBegin: 0f, PathEnd: 1f,
        PathScaleX: 1f, PathScaleY: 1f,
        PathShearX: 0f, PathShearY: 0f,
        PathTaperX: 0f, PathTaperY: 0f,
        PathTwist: 0f, PathTwistBegin: 0f,
        PathRadiusOffset: 0f, PathSkew: 0f, PathRevolutions: 1f,
        ProfileBegin: 0f, ProfileEnd: 1f, ProfileHollow: 0.5f, PCode: 9);

    [Theory]
    [InlineData(SquareProfileWithRoundHole)]
    [InlineData(SquareProfileWithSameHole)]
    public void HollowBox_WithPackedHoleTypeNibble_ProducesBoundedNonDegenerateGeometry(byte packedProfileCurve)
    {
        var mesh = PrimMeshService.Generate(HollowBoxShape(packedProfileCurve));

        Assert.NotNull(mesh);
        Assert.NotEmpty(mesh!.Submeshes);

        var allPositions = mesh.Submeshes.SelectMany(s => s.Positions).ToList();
        Assert.NotEmpty(allPositions);

        // No NaN/Infinity -- the actual signature of the sculpt-map "melted blob" class of bug and
        // (per this investigation) a plausible signature of a hole-type/profile mismatch producing
        // self-intersecting profile geometry.
        Assert.All(allPositions, p =>
        {
            Assert.False(float.IsNaN(p.X) || float.IsNaN(p.Y) || float.IsNaN(p.Z), $"NaN vertex: {p}");
            Assert.False(float.IsInfinity(p.X) || float.IsInfinity(p.Y) || float.IsInfinity(p.Z), $"Infinite vertex: {p}");
        });

        // A hollow box, however its hole is shaped, must stay within the unit prim cube (±0.5,
        // with a small margin for numerical slop) -- geometry escaping far outside that box is the
        // "spikes" failure mode; sharp self-folding within bounds is the "crumpled origami" one.
        // Either way, positions must never exceed the cube by more than a hair.
        float maxAbs = allPositions.Max(p => MathF.Max(MathF.Abs(p.X), MathF.Max(MathF.Abs(p.Y), MathF.Abs(p.Z))));
        Assert.True(maxAbs < 0.55f, $"hollow box geometry escaped the unit prim cube: max |coord| = {maxAbs}");

        // Every index must address a real vertex in the same submesh (no dangling/garbage indices).
        foreach (var sub in mesh.Submeshes)
        {
            Assert.All(sub.Indices, i => Assert.InRange(i, 0, sub.Positions.Length - 1));
        }
    }

    [Fact]
    public void HollowBox_DifferentHoleTypeNibble_ChangesGeneratedGeometry()
    {
        // The whole point of carrying the packed byte through intact: a Circle hole and a Same
        // (square-matching) hole on the same outer profile/hollow amount must NOT produce
        // identical vertex data. If they did, the high nibble is being silently discarded
        // somewhere in the pipeline -- exactly the bug this investigation found in GridSession.
        var roundHole = PrimMeshService.Generate(HollowBoxShape(SquareProfileWithRoundHole));
        var sameHole = PrimMeshService.Generate(HollowBoxShape(SquareProfileWithSameHole));

        Assert.NotNull(roundHole);
        Assert.NotNull(sameHole);

        int roundHoleVertexCount = roundHole!.Submeshes.Sum(s => s.Positions.Length);
        int sameHoleVertexCount = sameHole!.Submeshes.Sum(s => s.Positions.Length);

        // A round hole is meshed with many more sides than a 4-sided "Same" (square-matching)
        // hole, so the vertex counts alone should differ when the hole type is actually honored.
        Assert.NotEqual(roundHoleVertexCount, sameHoleVertexCount);
    }
}
