using System.Collections.Generic;
using System.Numerics;
using SLNG.Assets;
using Xunit;

namespace SLNG.Core.Tests;

public class AvatarMorphServiceTests
{
    private const int MorphParamId = 700;
    private const float NormalSoftenFactor = 0.65f; // llpolymorph.cpp:42

    // Two-vertex part: vertex 0 is morphed, vertex 1 is untouched by any morph.
    private static AvatarBodyPartMesh MakePart(params AvatarMorphTarget[] morphs) => new(
        Name: "test",
        Positions: new[] { new Vector3(1, 2, 3), new Vector3(-1, -2, -3) },
        Normals: new[] { new Vector3(0, 0, 1), new Vector3(0, 1, 0) },
        UVs: new[] { Vector2.Zero, Vector2.Zero },
        Indices: new[] { 0, 1, 0 },
        Bone1Names: new string?[] { "mPelvis", "mPelvis" },
        Bone1Weights: new[] { 1f, 1f },
        Bone2Names: new string?[] { null, null },
        Bone2Weights: new[] { 0f, 0f },
        Morphs: morphs);

    private static AvatarMorphTarget MoveVertex0(Vector3 posDelta, Vector3 normDelta) =>
        new(MorphParamId, "TestMorph",
            vertexIndices: new[] { 0 },
            positionDeltas: new[] { posDelta },
            normalDeltas: new[] { normDelta });

    [Fact]
    public void Zero_weight_leaves_the_base_mesh_unchanged()
    {
        var part = MakePart(MoveVertex0(new Vector3(10, 0, 0), new Vector3(1, 0, 0)));
        var (pos, nrm) = AvatarMorphService.Apply(part, new Dictionary<int, float> { [MorphParamId] = 0f });

        Assert.Equal(part.Positions[0], pos[0]);
        Assert.Equal(part.Positions[1], pos[1]);
        // Base normals are already unit-length, so renormalization returns them unchanged.
        Assert.True((part.Normals[0] - nrm[0]).Length() < 1e-6f);
        Assert.True((part.Normals[1] - nrm[1]).Length() < 1e-6f);
    }

    [Fact]
    public void Missing_weight_entry_is_treated_as_zero()
    {
        var part = MakePart(MoveVertex0(new Vector3(10, 0, 0), Vector3.Zero));
        var (pos, _) = AvatarMorphService.Apply(part, new Dictionary<int, float>()); // no entry for the param

        Assert.Equal(part.Positions[0], pos[0]);
    }

    [Fact]
    public void Position_delta_scales_linearly_with_weight_and_only_touches_listed_vertices()
    {
        // Viewer: coords[v] += morphDelta * delta_weight (llpolymorph.cpp:595-597); first apply
        // has delta_weight == weight (mLastWeight starts at 0). So morphed = base + delta*weight.
        var posDelta = new Vector3(4, -2, 0);
        var part = MakePart(MoveVertex0(posDelta, Vector3.Zero));
        const float w = 0.25f;

        var (pos, _) = AvatarMorphService.Apply(part, new Dictionary<int, float> { [MorphParamId] = w });

        Assert.True((pos[0] - (part.Positions[0] + posDelta * w)).Length() < 1e-6f);
        Assert.Equal(part.Positions[1], pos[1]); // untouched vertex stays put
    }

    [Fact]
    public void Normals_soften_by_0_65_and_are_renormalized_to_unit_length()
    {
        // Viewer: scaled_normals[v] += morphNormal * (delta_weight * 0.65), then normalize.
        var normDelta = new Vector3(1, 0, 0);
        var part = MakePart(MoveVertex0(Vector3.Zero, normDelta));
        const float w = 1.0f;

        var (_, nrm) = AvatarMorphService.Apply(part, new Dictionary<int, float> { [MorphParamId] = w });

        var expected = Vector3.Normalize(part.Normals[0] + normDelta * (w * NormalSoftenFactor));
        Assert.True((nrm[0] - expected).Length() < 1e-5f, $"expected {expected}, got {nrm[0]}");
        Assert.True(System.Math.Abs(nrm[0].Length() - 1f) < 1e-5f, "morphed normal must be unit length");
    }

    [Fact]
    public void Multiple_morphs_on_the_same_vertex_accumulate()
    {
        var part = MakePart(
            new AvatarMorphTarget(700, "A", new[] { 0 }, new[] { new Vector3(1, 0, 0) }, new[] { Vector3.Zero }),
            new AvatarMorphTarget(701, "B", new[] { 0 }, new[] { new Vector3(0, 1, 0) }, new[] { Vector3.Zero }));

        var (pos, _) = AvatarMorphService.Apply(part, new Dictionary<int, float> { [700] = 2f, [701] = 3f });

        var expected = part.Positions[0] + new Vector3(1, 0, 0) * 2f + new Vector3(0, 1, 0) * 3f;
        Assert.True((pos[0] - expected).Length() < 1e-6f, $"expected {expected}, got {pos[0]}");
    }

    [Fact]
    public void Base_mesh_is_not_mutated_so_the_cached_part_stays_reusable()
    {
        var basePos0 = new Vector3(1, 2, 3);
        var part = MakePart(MoveVertex0(new Vector3(10, 10, 10), Vector3.Zero));

        AvatarMorphService.Apply(part, new Dictionary<int, float> { [MorphParamId] = 1f });

        Assert.Equal(basePos0, part.Positions[0]); // the shared cached array must be untouched
    }
}
