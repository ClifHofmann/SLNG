using System;
using System.Linq;
using System.Numerics;
using SLNG.Assets;
using SLNG.Core;
using Xunit;
using Xunit.Abstractions;

namespace SLNG.Assets.Tests;

/// <summary>
/// TEMPORARY diagnostic (fix/broken-mesh-objects): dumps exactly which surfaces MeshFoundry emits
/// for a plain box prim, to confirm or refute the "one end cap is silently missing" claim that
/// PrimMeshService.RepairMissingEndCap was written for and that
/// PrimMeshServiceTests.Generate_box_closes_both_top_and_bottom_caps currently fails on.
/// </summary>
public class PrimBoxFaceDumpTests
{
    private readonly ITestOutputHelper _out;
    public PrimBoxFaceDumpTests(ITestOutputHelper output) => _out = output;

    private static PrimShape BoxShape() => new()
    {
        PathCurve = 16,       // Line — straight extrusion
        ProfileCurve = 1,     // Square
        PathScaleX = 1f,
        PathScaleY = 1f,
        PathBegin = 0f,
        PathEnd = 1f,
        ProfileBegin = 0f,
        ProfileEnd = 1f,
        PathRevolutions = 1f,
        PCode = 9,
    };

    [Theory]
    [InlineData("box", 1, 0f)]
    [InlineData("cylinder", 0, 0f)]
    [InlineData("prism", 3, 0f)]
    [InlineData("hollow box", 1, 0.5f)]
    [InlineData("hollow cylinder", 0, 0.5f)]
    [InlineData("hollow prism", 3, 0.5f)]
    public void Dump_box_surfaces(string label, int profileCurve, float hollow)
    {
        var mesh = PrimMeshService.Generate(BoxShape() with { ProfileCurve = (byte)profileCurve, ProfileHollow = hollow });
        Assert.NotNull(mesh);

        _out.WriteLine($"{label}: {mesh!.Submeshes.Count} submeshes");
        foreach (var sm in mesh.Submeshes)
        {
            var n = sm.Normals.Length > 0 ? sm.Normals[0] : Vector3.Zero;
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var p in sm.Positions) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }

            // Total triangle area — a real cap on a unit box is 1.0; a missing/degenerate one ~0.
            double area = 0;
            for (int t = 0; t + 2 < sm.Indices.Length; t += 3)
            {
                var a = sm.Positions[sm.Indices[t]];
                var b = sm.Positions[sm.Indices[t + 1]];
                var c = sm.Positions[sm.Indices[t + 2]];
                area += Vector3.Cross(b - a, c - a).Length() / 2.0;
            }

            _out.WriteLine($"  face={sm.FaceIndex} verts={sm.Positions.Length,3} tris={sm.Indices.Length / 3,2} " +
                           $"area={area:F3} n0=({n.X:F2},{n.Y:F2},{n.Z:F2}) bbox=({min.X:F2},{min.Y:F2},{min.Z:F2})..({max.X:F2},{max.Y:F2},{max.Z:F2})");
        }

        // A closed unit box has 6 quad faces of area 1 each.
        double total = 0;
        foreach (var sm in mesh.Submeshes)
            for (int t = 0; t + 2 < sm.Indices.Length; t += 3)
                total += Vector3.Cross(sm.Positions[sm.Indices[t + 1]] - sm.Positions[sm.Indices[t]],
                                       sm.Positions[sm.Indices[t + 2]] - sm.Positions[sm.Indices[t]]).Length() / 2.0;
        _out.WriteLine($"  TOTAL surface area = {total:F3}  (a closed unit box = 6.000)");

        // Closed surface: a real (flat, non-degenerate) cap must exist at BOTH the top (+Z) and
        // bottom (-Z) extremes — exactly the defect RepairMissingEndCap fixes. This check ignores
        // any submesh that doesn't lie entirely in one Z plane, so it isn't fooled by the hollow
        // interior wall's own (separate, out-of-scope) seam artifacts. For a hollow profile the two
        // caps are annuli of the same cross-section, so their areas must match.
        float minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (var sm in mesh.Submeshes)
            foreach (var p in sm.Positions) { minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z); }

        double topCapArea = PlanarFaceArea(mesh, maxZ);
        double bottomCapArea = PlanarFaceArea(mesh, minZ);
        Assert.True(topCapArea > 0, $"{label}: no closed cap at top (+Z)");
        Assert.True(bottomCapArea > 0, $"{label}: no closed cap at bottom (-Z)");
        Assert.Equal(topCapArea, bottomCapArea, precision: 2);
    }

    /// <summary>Area of the first submesh that lies entirely in the plane <c>z == z</c>, or 0 if
    /// none does. A wall (or any face spanning both Z extremes) never qualifies.</summary>
    private static double PlanarFaceArea(MeshData mesh, float z)
    {
        foreach (var sm in mesh.Submeshes)
        {
            if (sm.Positions.Length < 3) continue;
            bool planar = true;
            foreach (var p in sm.Positions)
            {
                if (Math.Abs(p.Z - z) > 1e-4f) { planar = false; break; }
            }
            if (!planar) continue;

            double area = 0;
            for (int t = 0; t + 2 < sm.Indices.Length; t += 3)
            {
                var a = sm.Positions[sm.Indices[t]];
                var b = sm.Positions[sm.Indices[t + 1]];
                var c = sm.Positions[sm.Indices[t + 2]];
                area += Vector3.Cross(b - a, c - a).Length() / 2.0;
            }
            if (area > 1e-6) return area;
        }
        return 0;
    }
}
