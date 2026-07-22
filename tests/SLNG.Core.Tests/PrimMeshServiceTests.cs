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
        ProfileBegin: 0f, ProfileEnd: 1f, ProfileHollow: 0f, PCode: 9);

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

    // Regression: LibreMetaverse.Rendering.MeshFoundry (NuGet 3.0.0) only assembles the path's
    // *last* end face into a cap (PrimMesh.Create gates the cap ViewerFaces on
    // `nodeIndex == path.pathNodes.Count - 1`); the first end face (the bottom, for a straight
    // extrusion) never gets a cap submesh, leaving the object's bottom entirely open. At normal
    // ~1m prim scale the resulting sliver is sub-pixel; scaled into a large flat platform it
    // reads as a visible seam/gap right at the top edge where the camera's sightline grazes past
    // the missing bottom into the hollow interior. PrimMeshService.Generate repairs this for any
    // straight-extruded profile (PathCurve Line/Flexible) by reconstructing the missing cap from
    // the side walls' own (correct) bottom-ring vertices.
    [Fact]
    public void Generate_box_closes_both_top_and_bottom_caps()
    {
        var mesh = PrimMeshService.Generate(BoxShape());

        Assert.NotNull(mesh);

        float minZ = mesh!.Submeshes.SelectMany(s => s.Positions).Min(p => p.Z);
        float maxZ = mesh.Submeshes.SelectMany(s => s.Positions).Max(p => p.Z);

        bool HasCapAt(float z) => mesh.Submeshes.Any(sm =>
            sm.Positions.Length >= 3 &&
            sm.Positions.All(p => System.MathF.Abs(p.Z - z) < 1e-3f) &&
            TriangleArea(sm) > 0.9f); // a real 1x1 quad cap, not a degenerate sliver

        Assert.True(HasCapAt(maxZ), "top cap missing");
        Assert.True(HasCapAt(minZ), "bottom cap missing (the open-bottom regression)");

        // The reconstructed bottom cap's corners must exactly match the side walls' bottom
        // corners (bit-identical, like the top cap already does) — no re-introduced crack.
        var bottomCap = mesh.Submeshes.First(sm =>
            sm.Positions.Length >= 3 &&
            sm.Positions.All(p => System.MathF.Abs(p.Z - minZ) < 1e-3f) &&
            TriangleArea(sm) > 0.9f);

        foreach (var corner in bottomCap.Positions)
        {
            bool weldedElsewhere = mesh.Submeshes
                .Where(sm => !ReferenceEquals(sm, bottomCap))
                .SelectMany(sm => sm.Positions)
                .Any(p => p == corner);
            Assert.True(weldedElsewhere, $"bottom cap corner {corner} isn't shared with a side wall");
        }
    }

    private static double TriangleArea(SLNG.Assets.MeshSubmesh sm)
    {
        double area = 0;
        for (int t = 0; t + 2 < sm.Indices.Length; t += 3)
        {
            var a = sm.Positions[sm.Indices[t]];
            var b = sm.Positions[sm.Indices[t + 1]];
            var c = sm.Positions[sm.Indices[t + 2]];
            var cross = System.Numerics.Vector3.Cross(b - a, c - a);
            area += 0.5 * cross.Length();
        }
        return area;
    }
}
