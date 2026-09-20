using System;
using System.Numerics;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>
/// A touch carries two coordinates: where on the FACE the click landed, and where on the TEXTURE
/// that is. A texture-driven menu — a button strip, a colour grid — reads the second one
/// (<c>llDetectedTouchUV</c>), so sending the untransformed surface coordinate for both puts
/// every click in the wrong cell. This is <c>LLFace::xform</c>.
/// </summary>
public class TextureSurfaceTests
{
    private static FaceTexture Face(
        float repeatU = 1f, float repeatV = 1f, float offsetU = 0f, float offsetV = 0f,
        float rotation = 0f, byte texGen = 0) =>
        new(Guid.Empty, Guid.Empty, Guid.Empty, Vector4.One,
            repeatU, repeatV, offsetU, offsetV, rotation, texGen);

    [Fact]
    public void DefaultPlacementLeavesTheCoordinateAlone()
    {
        var uv = TextureSurface.SurfaceToTexture(new Vector2(0.25f, 0.75f), Face());

        Assert.Equal(0.25f, uv.X, 4);
        Assert.Equal(0.75f, uv.Y, 4);
    }

    [Fact]
    public void RepeatScalesAboutTheFaceCentre()
    {
        // SL scales texture placement about the middle of the face, not its corner: the centre
        // must stay put, and the edge moves out by the repeat.
        var face = Face(repeatU: 2f, repeatV: 2f);

        var centre = TextureSurface.SurfaceToTexture(new Vector2(0.5f, 0.5f), face);
        Assert.Equal(0.5f, centre.X, 4);
        Assert.Equal(0.5f, centre.Y, 4);

        var corner = TextureSurface.SurfaceToTexture(new Vector2(1f, 1f), face);
        Assert.Equal(1.5f, corner.X, 4);
        Assert.Equal(1.5f, corner.Y, 4);
    }

    [Fact]
    public void OffsetShiftsTheWholeCoordinate()
    {
        var uv = TextureSurface.SurfaceToTexture(new Vector2(0.5f, 0.5f), Face(offsetU: 0.25f, offsetV: -0.1f));

        Assert.Equal(0.75f, uv.X, 4);
        Assert.Equal(0.4f, uv.Y, 4);
    }

    [Fact]
    public void RotationTurnsAboutTheFaceCentre()
    {
        // A quarter turn takes the +U edge to the -V edge (the viewer's xform rotates the
        // coordinate, which is the opposite sense to rotating the texture).
        var uv = TextureSurface.SurfaceToTexture(new Vector2(1f, 0.5f), Face(rotation: MathF.PI / 2f));

        Assert.Equal(0.5f, uv.X, 4);
        Assert.Equal(0f, uv.Y, 4);
    }

    [Fact]
    public void NonDefaultTexGenKeepsTheSurfaceCoordinate()
    {
        // Planar and friends project from the face's plane before the transform, which needs the
        // prim's volume rather than the face. Reporting the untransformed surface coordinate is
        // what the viewer itself falls back to when it cannot do the projection.
        var planar = Face(repeatU: 4f, texGen: 2);

        var uv = TextureSurface.SurfaceToTexture(new Vector2(0.25f, 0.75f), planar);

        Assert.Equal(0.25f, uv.X, 4);
        Assert.Equal(0.75f, uv.Y, 4);
    }

    [Fact]
    public void NegativeRepeatMirrors()
    {
        // A negative repeat is how SL flips a texture on a face; the coordinate has to flip with
        // it or a mirrored button strip reads back the button on the other side.
        var uv = TextureSurface.SurfaceToTexture(new Vector2(1f, 0.5f), Face(repeatU: -1f));

        Assert.Equal(0f, uv.X, 4);
        Assert.Equal(0.5f, uv.Y, 4);
    }
}
