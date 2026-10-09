using System.Numerics;
using SLNG.Core;
using Xunit;

namespace SLNG.Assets.Tests;

// BUG-PERF-06: rigging a worn mesh moved off the main thread, and its pure-data half moved here with
// it. These pin what that half has always done -- axes, winding, V-flip, the bind-shape normal rule,
// BUG-RENDER-12's consecutive-run merging and the viewer's weight remapping -- so the move cannot
// change what renders.
public sealed class RiggedMeshBuilderTests
{
    private static readonly FaceTexture FaceA = default(FaceTexture) with { TextureId = Guid.Parse("11111111-1111-1111-1111-111111111111") };
    private static readonly FaceTexture FaceB = default(FaceTexture) with { TextureId = Guid.Parse("22222222-2222-2222-2222-222222222222") };

    private static MeshSkin Skin(int joints, Matrix4x4? bindShape = null) => new(
        Enumerable.Range(0, joints).Select(j => $"joint{j}").ToArray(),
        Enumerable.Repeat(Matrix4x4.Identity, joints).ToArray(),
        bindShape ?? Matrix4x4.Identity,
        PelvisOffset: 0f);

    private static VertexBoneWeights On(int joint) => new(joint, 0, 0, 0, 1f, 0f, 0f, 0f);

    /// <summary>One triangle, every vertex fully on <paramref name="joint"/>.</summary>
    private static MeshSubmesh Triangle(int faceIndex, int joint = 0, Vector3? offset = null)
    {
        var o = offset ?? Vector3.Zero;
        return new MeshSubmesh(
            new[] { o, o + Vector3.UnitX, o + Vector3.UnitY },
            new[] { Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ },
            new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f) },
            new[] { 0, 1, 2 },
            faceIndex,
            new[] { On(joint), On(joint), On(joint) });
    }

    private static FaceTexture FaceFor(int faceIndex) => faceIndex switch { 0 => FaceA, 1 => FaceA, _ => FaceB };

    [Fact]
    public void SlotsForJoints_ranks_the_joints_that_resolve_and_marks_the_rest()
    {
        var known = new HashSet<string> { "mPelvis", "mChest", "mHead" };
        var slots = RiggedMeshBuilder.SlotsForJoints(new[] { "mPelvis", "nope", "mChest", "gone", "mHead" }, known.Contains);

        Assert.Equal(new[] { 0, -1, 1, -1, 2 }, slots);
        Assert.Equal(3, RiggedMeshBuilder.SlotCount(slots));
    }

    [Fact]
    public void A_vertex_goes_into_godot_axes_with_v_flipped_and_the_triangle_wound_clockwise()
    {
        var sub = new MeshSubmesh(
            new[] { new Vector3(1f, 2f, 3f), new Vector3(4f, 5f, 6f), new Vector3(7f, 8f, 9f) },
            new[] { Vector3.UnitY, Vector3.UnitY, Vector3.UnitY },
            new[] { new Vector2(0.25f, 0.75f), new Vector2(0.5f, 0.5f), new Vector2(1f, 0f) },
            new[] { 0, 1, 2 },
            0,
            new[] { On(0), On(0), On(0) });

        var geo = RiggedMeshBuilder.Build(new MeshData(new[] { sub }, Skin(1)), new[] { 0 }, FaceFor);

        var s = Assert.Single(geo.Surfaces);
        Assert.Equal(new Vector3(1f, 3f, -2f), s.Positions[0]);       // SL (x, y, z) -> Godot (x, z, -y)
        Assert.Equal(new Vector3(0f, 0f, -1f), s.Normals[0]);         // SL +Y (north) -> Godot -Z
        Assert.Equal(new Vector2(0.25f, 0.25f), s.UVs[0]);            // bottom-origin V -> top-origin
        Assert.Equal(new[] { 0, 2, 1 }, s.Indices);                   // CCW-front -> CW-front
        Assert.Equal(new[] { 0, 0, 0, 0 }, s.Bones[..4]);
        Assert.Equal(new[] { 1f, 0f, 0f, 0f }, s.Weights[..4]);
    }

    [Fact]
    public void The_bind_shape_moves_positions_and_its_inverse_transpose_turns_normals()
    {
        // Non-uniform scale is exactly where the two rules differ: x stretched 2x squashes the
        // normal's x, it does not stretch it.
        var bindShape = Matrix4x4.CreateScale(2f, 1f, 1f) * Matrix4x4.CreateTranslation(0f, 0f, 1f);
        var n = Vector3.Normalize(new Vector3(1f, 0f, 1f));
        var sub = new MeshSubmesh(
            new[] { new Vector3(1f, 0f, 0f), Vector3.Zero, Vector3.UnitY },
            new[] { n, n, n },
            new[] { Vector2.Zero, Vector2.UnitX, Vector2.UnitY },
            new[] { 0, 1, 2 },
            0,
            new[] { On(0), On(0), On(0) });

        var geo = RiggedMeshBuilder.Build(new MeshData(new[] { sub }, Skin(1, bindShape)), new[] { 0 }, FaceFor);

        var s = Assert.Single(geo.Surfaces);
        Assert.Equal(new Vector3(2f, 1f, 0f), s.Positions[0]);        // SL (2, 0, 1)
        var expected = Vector3.Normalize(new Vector3(0.5f, 0f, 1f));   // SL, through (S^-1)^T
        AssertNear(new Vector3(expected.X, expected.Z, -expected.Y), s.Normals[0]);
        // The bind-pose extent is in SL axes, after the bind shape.
        Assert.Equal(new Vector3(0f, 0f, 1f), geo.BindPoseMin);
        Assert.Equal(new Vector3(2f, 1f, 1f), geo.BindPoseMax);
    }

    [Fact]
    public void Consecutive_submeshes_with_the_same_face_record_share_one_surface()
    {
        // Faces 0 and 1 resolve to the same record, face 2 to another.
        var mesh = new MeshData(new[] { Triangle(0), Triangle(1, offset: Vector3.UnitZ), Triangle(2) }, Skin(1));

        var geo = RiggedMeshBuilder.Build(mesh, new[] { 0 }, FaceFor);

        Assert.Equal(2, geo.Surfaces.Count);
        Assert.Equal(new[] { 0, 2 }, geo.FaceIndices());
        var merged = geo.Surfaces[0];
        Assert.Equal(6, merged.Positions.Length);
        // The second triangle's indices are shifted past the first one's vertices, authored order kept.
        Assert.Equal(new[] { 0, 2, 1, 3, 5, 4 }, merged.Indices);
        Assert.Equal(new Vector3(0f, 1f, 0f), merged.Positions[3]);   // SL z = 1 -> Godot y
        Assert.Equal(3, geo.SubmeshCount);
    }

    [Fact]
    public void Matching_faces_that_are_not_next_to_each_other_stay_apart()
    {
        // A, B, A: merging the two A's would interleave triangles the creator ordered on purpose.
        var mesh = new MeshData(new[] { Triangle(0), Triangle(2), Triangle(1) }, Skin(1));

        var geo = RiggedMeshBuilder.Build(mesh, new[] { 0 }, FaceFor);

        Assert.Equal(new[] { 0, 2, 1 }, geo.FaceIndices());
    }

    [Fact]
    public void Submeshes_without_indices_or_weights_are_skipped()
    {
        var noWeights = Triangle(0) with { Weights = null };
        var noIndices = Triangle(0) with { Indices = Array.Empty<int>() };
        var mesh = new MeshData(new[] { noWeights, noIndices, Triangle(2) }, Skin(1));

        var geo = RiggedMeshBuilder.Build(mesh, new[] { 0 }, FaceFor);

        Assert.Equal(new[] { 2 }, geo.FaceIndices());
        Assert.Equal(3, geo.TotalVertices);
    }

    [Fact]
    public void No_bound_joint_means_no_surfaces()
    {
        var geo = RiggedMeshBuilder.Build(new MeshData(new[] { Triangle(0) }, Skin(1)), new[] { -1 }, FaceFor);

        Assert.Empty(geo.Surfaces);
    }

    [Fact]
    public void Weights_are_clamped_remapped_and_renormalised_like_the_viewer()
    {
        // Three joints; joint 1 names no bone of ours. Slots: joint0 -> 0, joint2 -> 1.
        int[] slots = { 0, -1, 1 };
        var weights = new[]
        {
            new VertexBoneWeights(2, 7, 0, 0, 0.5f, 0.5f, 0f, 0f),     // 7 is out of range -> clamped to joint 2
            new VertexBoneWeights(1, 2, 0, 0, 0.6f, 0.2f, 0f, 0f),     // joint 1 unresolved -> slot 0
            new VertexBoneWeights(0, 2, 0, 0, float.NaN, 0f, 0f, 0f),  // nothing usable -> orphan
        };
        var sub = Triangle(0) with { Weights = weights };

        var geo = RiggedMeshBuilder.Build(new MeshData(new[] { sub }, Skin(3)), slots, FaceFor);

        var s = Assert.Single(geo.Surfaces);
        Assert.Equal(new[] { 1, 1, 0, 0 }, s.Bones[0..4]);
        Assert.Equal(new[] { 0.5f, 0.5f, 0f, 0f }, s.Weights[0..4]);
        Assert.Equal(new[] { 0, 1, 0, 0 }, s.Bones[4..8]);
        AssertNear(0.75f, s.Weights[4]);
        AssertNear(0.25f, s.Weights[5]);
        Assert.Equal(new[] { 0, 0, 0, 0 }, s.Bones[8..12]);
        Assert.Equal(new[] { 1f, 0f, 0f, 0f }, s.Weights[8..12]);

        Assert.Equal(2, geo.RemappedInfluences);
        Assert.Equal(1, geo.OrphanedVertices);
        AssertNear(1.75f, geo.SlotWeightSum[0]);   // 0.75 + orphan's 1
        AssertNear(1.25f, geo.SlotWeightSum[1]);   // 0.5 + 0.5 + 0.25
    }

    [Fact]
    public void Non_finite_values_are_replaced_and_counted()
    {
        var sub = Triangle(0) with
        {
            Positions = new[] { Vector3.Zero, new Vector3(float.NaN, 0f, 0f), Vector3.UnitY },
            Normals = new[] { Vector3.UnitZ, Vector3.UnitZ, new Vector3(0f, float.PositiveInfinity, 0f) },
            UVs = new[] { new Vector2(float.NaN, 0f), Vector2.Zero, Vector2.Zero },
        };

        var geo = RiggedMeshBuilder.Build(new MeshData(new[] { sub }, Skin(1)), new[] { 0 }, FaceFor);

        var s = Assert.Single(geo.Surfaces);
        Assert.Equal(Vector3.Zero, s.Positions[1]);
        Assert.Equal(Vector3.UnitY, s.Normals[2]);   // Godot up
        Assert.Equal(Vector2.Zero, s.UVs[0]);
        Assert.Equal((1, 2), (geo.BadPositions, geo.FirstBadPosition));
        Assert.Equal((1, 3), (geo.BadNormals, geo.FirstBadNormal));
        Assert.Equal((1, 1), (geo.BadUvs, geo.FirstBadUv));
    }

    [Fact]
    public void Invisible_submeshes_are_skipped_and_surviving_face_indices_preserved()
    {
        var transparentFace = default(FaceTexture) with { TextureId = FaceTexture.TransparentTextureId };
        var zeroAlphaFace = default(FaceTexture) with { TextureId = Guid.NewGuid(), Color = new Vector4(1f, 1f, 1f, 0f) };
        var visibleFace0 = default(FaceTexture) with { TextureId = Guid.NewGuid() };
        var visibleFace2 = default(FaceTexture) with { TextureId = Guid.NewGuid() };

        var sub0 = Triangle(0);
        var sub1 = Triangle(1);
        var sub2 = Triangle(2);
        var sub3 = Triangle(3);

        FaceTexture Resolver(int faceIndex) => faceIndex switch
        {
            0 => visibleFace0,
            1 => transparentFace,
            2 => visibleFace2,
            3 => zeroAlphaFace,
            _ => default,
        };

        var mesh = new MeshData(new[] { sub0, sub1, sub2, sub3 }, Skin(1));
        var geo = RiggedMeshBuilder.Build(mesh, new[] { 0 }, Resolver);

        Assert.Equal(2, geo.Surfaces.Count);
        Assert.Equal(new[] { 0, 2 }, geo.FaceIndices());
        Assert.Equal(0, geo.Surfaces[0].FaceIndex);
        Assert.Equal(2, geo.Surfaces[1].FaceIndex);
    }

    [Fact]
    public void All_invisible_submeshes_yield_zero_surfaces_and_zero_extent()
    {
        var transparentFace = default(FaceTexture) with { TextureId = FaceTexture.TransparentTextureId };
        var sub0 = Triangle(0);
        var sub1 = Triangle(1);

        var mesh = new MeshData(new[] { sub0, sub1 }, Skin(1));
        var geo = RiggedMeshBuilder.Build(mesh, new[] { 0 }, _ => transparentFace);

        Assert.Empty(geo.Surfaces);
        Assert.Empty(geo.FaceIndices());
        Assert.Equal(0, geo.TotalVertices);
        Assert.Equal(Vector3.Zero, geo.BindPoseMin);
        Assert.Equal(Vector3.Zero, geo.BindPoseMax);
    }

    private static void AssertNear(float expected, float actual) => Assert.InRange(actual, expected - 1e-5f, expected + 1e-5f);

    private static void AssertNear(Vector3 expected, Vector3 actual)
    {
        AssertNear(expected.X, actual.X);
        AssertNear(expected.Y, actual.Y);
        AssertNear(expected.Z, actual.Z);
    }
}
