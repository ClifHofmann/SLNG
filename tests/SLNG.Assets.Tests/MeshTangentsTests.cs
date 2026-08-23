using System.Numerics;
using SLNG.Assets;
using Xunit;

namespace SLNG.Assets.Tests;

/// <summary>
/// Tangent generation, with the emphasis on the property that made it necessary to write our own:
/// every output must be finite. Godot's SurfaceTool.GenerateTangents() was disabled in
/// ObjectRenderer because it produced NaNs on degenerate triangles and those reached the Vulkan
/// driver, and SL content is full of them.
/// </summary>
public class MeshTangentsTests
{
    private const float Tol = 1e-4f;

    private static void AssertFinite(Vector4 v)
    {
        Assert.True(float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) && float.IsFinite(v.W),
            $"non-finite tangent {v}");
    }

    [Fact]
    public void AQuadInThePlaneGetsTheUAxisAsItsTangent()
    {
        // A unit quad in XY with matching UVs: the tangent must point along +X, because that is
        // the direction U increases in.
        var positions = new[]
        {
            new Vector3(0, 0, 0), new Vector3(1, 0, 0),
            new Vector3(1, 1, 0), new Vector3(0, 1, 0),
        };
        var normals = new[] { Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ };
        var uvs = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
        var indices = new[] { 0, 1, 2, 0, 2, 3 };

        var tangents = MeshTangents.Compute(positions, normals, uvs, indices);

        Assert.Equal(4, tangents.Length);
        foreach (var t in tangents)
        {
            AssertFinite(t);
            Assert.Equal(1f, t.X, Tol);
            Assert.Equal(0f, t.Y, Tol);
            Assert.Equal(0f, t.Z, Tol);
        }
    }

    [Fact]
    public void TangentsAreUnitLengthAndPerpendicularToTheNormal()
    {
        var positions = new[] { new Vector3(0, 0, 0), new Vector3(2, 0, 0), new Vector3(0, 3, 1) };
        var normals = new[] { Vector3.Normalize(new Vector3(0.2f, -0.3f, 1f)) };
        normals = new[] { normals[0], normals[0], normals[0] };
        var uvs = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1) };

        var tangents = MeshTangents.Compute(positions, normals, uvs, new[] { 0, 1, 2 });

        for (int i = 0; i < tangents.Length; i++)
        {
            var t = new Vector3(tangents[i].X, tangents[i].Y, tangents[i].Z);
            Assert.Equal(1f, t.Length(), Tol);
            Assert.Equal(0f, Vector3.Dot(t, normals[i]), Tol);
            Assert.True(tangents[i].W == 1f || tangents[i].W == -1f);
        }
    }

    [Fact]
    public void MirroredUvsFlipTheHandedness()
    {
        // Two triangles sharing geometry but with V running the other way. Handedness is what
        // tells the shader the bitangent is flipped -- without it a mirrored UV island lights
        // inside out, which on a sculpt (whose halves share one map) is half the object.
        var positions = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0) };
        var normals = new[] { Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ };
        var indices = new[] { 0, 1, 2 };

        var normal = MeshTangents.Compute(positions, normals,
            new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1) }, indices);
        var mirrored = MeshTangents.Compute(positions, normals,
            new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, -1) }, indices);

        Assert.NotEqual(normal[0].W, mirrored[0].W);
    }

    [Fact]
    public void DegenerateTrianglesProduceFiniteTangents()
    {
        // The exact family that crashed the driver: zero-area in space, zero-area in UV space,
        // and duplicated vertices. None may yield a NaN, an infinity or a zero-length tangent.
        var positions = new[]
        {
            new Vector3(0, 0, 0), new Vector3(0, 0, 0), new Vector3(0, 0, 0),  // collapsed in space
            new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(2, 0, 0),  // collinear
        };
        var normals = new[]
        {
            Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ,
            Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ,
        };
        var uvs = new[]
        {
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), // same UV
            new Vector2(0, 0), new Vector2(1, 0), new Vector2(2, 0),                   // no V extent
        };

        var tangents = MeshTangents.Compute(positions, normals, uvs, new[] { 0, 1, 2, 3, 4, 5 });

        Assert.Equal(6, tangents.Length);
        foreach (var t in tangents)
        {
            AssertFinite(t);
            Assert.Equal(1f, new Vector3(t.X, t.Y, t.Z).Length(), Tol);
        }
    }

    [Fact]
    public void UnreferencedAndZeroNormalVerticesStillGetAValidTangent()
    {
        var positions = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(5, 5, 5) };
        var normals = new[] { Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.Zero };
        var uvs = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(0, 0) };

        var tangents = MeshTangents.Compute(positions, normals, uvs, new[] { 0, 1, 2 });

        AssertFinite(tangents[3]);
        Assert.Equal(1f, new Vector3(tangents[3].X, tangents[3].Y, tangents[3].Z).Length(), Tol);
    }

    [Fact]
    public void OutOfRangeIndicesAreIgnoredRatherThanThrowing()
    {
        // Decoded assets are not trusted input; a bad index must not take the renderer down.
        var positions = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY };
        var normals = new[] { Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ };
        var uvs = new[] { Vector2.Zero, Vector2.UnitX, Vector2.UnitY };

        var tangents = MeshTangents.Compute(positions, normals, uvs, new[] { 0, 1, 99, 0, 1, 2 });

        Assert.Equal(3, tangents.Length);
        foreach (var t in tangents) AssertFinite(t);
    }
}
