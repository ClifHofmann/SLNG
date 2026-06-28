using System.Linq;
using SLNG.Assets;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

public class PrimMeshServiceTests
{
    // A unit box: square profile, straight line path, full extents.
    private static PrimShape BoxShape() => new PrimShape(
        ProfileCurve: 1,
        PathCurve: 16,          // Line
        PathBegin: 0f, PathEnd: 1f,
        PathScaleX: 1f, PathScaleY: 1f,
        PathShearX: 0f, PathShearY: 0f,
        PathTaperX: 0f, PathTaperY: 0f,
        PathTwist: 0f, PathTwistBegin: 0f,
        PathRadiusOffset: 0f, PathSkew: 0f, PathRevolutions: 1f,
        ProfileBegin: 0f, ProfileEnd: 1f, ProfileHollow: 0f);

    [Fact]
    public void Generate_box_produces_a_unit_cube_mesh()
    {
        var mesh = PrimMeshService.Generate(BoxShape());

        Assert.NotNull(mesh);
        Assert.NotEmpty(mesh!.Submeshes);

        var sub = mesh.Submeshes[0];
        Assert.True(sub.Positions.Length > 0);
        Assert.True(sub.Indices.Length % 3 == 0);   // triangles
        Assert.Equal(sub.Positions.Length, sub.Normals.Length);
        Assert.Equal(sub.Positions.Length, sub.UVs.Length);

        // MeshFoundry emits unit prim space (~±0.5); the renderer applies prim scale.
        float maxAbs = sub.Positions.Max(p => System.MathF.Max(System.MathF.Abs(p.X),
                                              System.MathF.Max(System.MathF.Abs(p.Y), System.MathF.Abs(p.Z))));
        Assert.InRange(maxAbs, 0.4f, 0.6f);

        // Every index must address a real vertex.
        Assert.All(sub.Indices, i => Assert.InRange(i, 0, sub.Positions.Length - 1));
    }

    [Fact]
    public void Identical_shapes_are_equal_so_they_share_a_cache_entry()
    {
        Assert.Equal(BoxShape(), BoxShape());
        Assert.Equal(BoxShape().GetHashCode(), BoxShape().GetHashCode());
    }

    [Fact]
    public void Different_profile_curves_are_not_equal()
    {
        var box = BoxShape();
        var cylinder = box with { ProfileCurve = 0 };
        Assert.NotEqual(box, cylinder);
    }
}
