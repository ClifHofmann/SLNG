using System;
using System.Numerics;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>
/// The client half of an SL touch: which face was clicked, and where on it. A vendor panel with
/// one product per face reads exactly this back (<c>llDetectedTouchFace</c>,
/// <c>llDetectedTouchST</c>), so "face 0 at (0,0)" — what SLNG sent before — is a wrong answer
/// rather than a missing one.
/// </summary>
public class SurfacePickTests
{
    /// <summary>A unit quad in the XY plane at the given Z, wound CCW like SL geometry, with UVs
    /// running 0..1 across it.</summary>
    private static PickSubmesh Quad(float z, int faceIndex) => new(
        Positions: new[]
        {
            new Vector3(0, 0, z), new Vector3(1, 0, z), new Vector3(1, 1, z), new Vector3(0, 1, z),
        },
        Normals: new[] { Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ },
        Uvs: new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) },
        Indices: new[] { 0, 1, 2, 0, 2, 3 },
        FaceIndex: faceIndex);

    [Fact]
    public void ReportsTheFaceThatWasHit()
    {
        // Three panels stacked along Z, each its own SL face. A ray down the Z axis must name the
        // NEAREST one -- the whole point of the pick is telling a vendor's products apart.
        var mesh = new[] { Quad(0f, 4), Quad(1f, 5), Quad(2f, 6) };

        Assert.True(SurfacePick.TryPick(mesh, new Vector3(0.5f, 0.5f, 3f), -Vector3.UnitZ, out var hit));

        Assert.Equal(6, hit.FaceIndex);
        Assert.Equal(1f, hit.Distance, 3);
    }

    [Fact]
    public void ReportsTheFaceIndexTheSubmeshCarries()
    {
        // The SL face number is the submesh's own, never its position in the list: a mesh can
        // leave faces empty, and renumbering them here would put every click on the wrong product.
        var mesh = new[] { Quad(0f, 7) };

        Assert.True(SurfacePick.TryPick(mesh, new Vector3(0.5f, 0.5f, 1f), -Vector3.UnitZ, out var hit));

        Assert.Equal(7, hit.FaceIndex);
    }

    [Theory]
    [InlineData(0.25f, 0.25f)]
    [InlineData(0.5f, 0.5f)]
    [InlineData(0.9f, 0.1f)]
    public void SurfaceCoordinateFollowsTheHitPoint(float x, float y)
    {
        var mesh = new[] { Quad(0f, 0) };

        Assert.True(SurfacePick.TryPick(mesh, new Vector3(x, y, 1f), -Vector3.UnitZ, out var hit));

        // The quad's UVs run 0..1 across it, so surface coordinate == position on the quad.
        Assert.Equal(x, hit.St.X, 3);
        Assert.Equal(y, hit.St.Y, 3);
    }

    [Fact]
    public void MissesWhatTheRayDoesNotCross()
    {
        var mesh = new[] { Quad(0f, 0) };

        Assert.False(SurfacePick.TryPick(mesh, new Vector3(5f, 5f, 1f), -Vector3.UnitZ, out _));
    }

    [Fact]
    public void IgnoresGeometryBehindTheRay()
    {
        // Pointing away from the quad is a miss, not a hit at negative distance -- otherwise a
        // click would report a face behind the camera.
        var mesh = new[] { Quad(0f, 0) };

        Assert.False(SurfacePick.TryPick(mesh, new Vector3(0.5f, 0.5f, 1f), Vector3.UnitZ, out _));
    }

    [Fact]
    public void HitsBackFacesToo()
    {
        // The object is already known to have been hit by the physics ray. Refusing a back-facing
        // triangle would turn a real click into no answer at all, and single-sided SL geometry
        // seen from behind is ordinary content.
        var mesh = new[] { Quad(0f, 3) };

        Assert.True(SurfacePick.TryPick(mesh, new Vector3(0.5f, 0.5f, -1f), Vector3.UnitZ, out var hit));

        Assert.Equal(3, hit.FaceIndex);
    }

    [Fact]
    public void NormalAndBinormalArePerpendicularUnitVectors()
    {
        var mesh = new[] { Quad(0f, 0) };

        Assert.True(SurfacePick.TryPick(mesh, new Vector3(0.5f, 0.5f, 1f), -Vector3.UnitZ, out var hit));

        Assert.Equal(1f, hit.Normal.Length(), 3);
        Assert.Equal(1f, hit.Binormal.Length(), 3);
        Assert.Equal(0f, Vector3.Dot(hit.Normal, hit.Binormal), 3);
        Assert.Equal(Vector3.UnitZ.Z, hit.Normal.Z, 3);
    }

    [Fact]
    public void SurvivesIndicesPastTheVertexArray()
    {
        // A malformed asset can index past its own vertices. Dropping that triangle is what the
        // collision-shape builder does; throwing here would make the object unclickable instead.
        var broken = new PickSubmesh(
            Positions: new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY },
            Normals: null,
            Uvs: null,
            Indices: new[] { 0, 1, 9 },
            FaceIndex: 0);

        Assert.False(SurfacePick.TryPick(new[] { broken }, new Vector3(0.1f, 0.1f, 1f), -Vector3.UnitZ, out _));
    }

    [Fact]
    public void FallsBackToTheFacetNormalWithoutVertexNormals()
    {
        var noNormals = new PickSubmesh(
            Positions: new[] { Vector3.Zero, Vector3.UnitX, new Vector3(1, 1, 0), Vector3.UnitY },
            Normals: null,
            Uvs: null,
            Indices: new[] { 0, 1, 2, 0, 2, 3 },
            FaceIndex: 2);

        Assert.True(SurfacePick.TryPick(new[] { noNormals }, new Vector3(0.5f, 0.5f, 1f), -Vector3.UnitZ, out var hit));

        Assert.Equal(2, hit.FaceIndex);
        Assert.Equal(1f, hit.Normal.Z, 3);
    }

    [Fact]
    public void EmptyMeshIsAMiss()
    {
        Assert.False(SurfacePick.TryPick(Array.Empty<PickSubmesh>(), Vector3.Zero, Vector3.UnitZ, out _));
    }

    [Fact]
    public void ZeroLengthDirectionIsAMiss()
    {
        var mesh = new[] { Quad(0f, 0) };

        Assert.False(SurfacePick.TryPick(mesh, new Vector3(0.5f, 0.5f, 1f), Vector3.Zero, out _));
    }
}
