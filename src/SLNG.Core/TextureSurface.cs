using System;
using System.Numerics;

namespace SLNG.Core;

/// <summary>
/// Turns a surface coordinate into a texture coordinate, the way a prim face places its texture.
/// </summary>
/// <remarks>
/// An SL touch carries both: STCoord is where on the FACE the click landed (the mesh's own UV),
/// UVCoord is where on the TEXTURE that is, after the face's repeat, offset and rotation have
/// been applied. Scripts read them as <c>llDetectedTouchST</c> and <c>llDetectedTouchUV</c>, and
/// a texture-driven menu — a HUD button strip, a colour picker — works off the second one, so
/// sending the raw surface coordinate for both would put every click in the wrong cell of the
/// grid the script thinks it has.
///
/// <para>This is <c>LLFace::surfaceToTexture</c> -&gt; <c>LLFace::xform</c> (llface.cpp), which
/// rotates and scales about the centre of the face rather than its origin — that is why the
/// 0.5 comes off before and goes back on after.</para>
///
/// <para>Non-default texgen (planar, spherical, cylindrical) is NOT applied: the viewer projects
/// the coordinate from the face's plane first, which needs the prim's volume geometry rather than
/// just the face. Those faces get the untransformed surface coordinate, which is what the viewer
/// itself falls back to when it has no texture entry at all.</para>
/// </remarks>
public static class TextureSurface
{
    /// <summary>SL's TEX_GEN_DEFAULT — the face's own UVs, no projection.</summary>
    public const byte TexGenDefault = 0;

    /// <summary>Applies <paramref name="face"/>'s placement to a surface coordinate.</summary>
    public static Vector2 SurfaceToTexture(Vector2 surface, FaceTexture face)
    {
        if (face.TexGen != TexGenDefault) return surface;

        return Xform(surface, face.Rotation, face.OffsetU, face.OffsetV, face.RepeatU, face.RepeatV);
    }

    /// <summary>Rotate, scale and offset about the face centre (<c>LLFace::xform</c>).</summary>
    public static Vector2 Xform(Vector2 coord, float rotation, float offsetS, float offsetT, float scaleS, float scaleT)
    {
        float cos = MathF.Cos(rotation);
        float sin = MathF.Sin(rotation);

        float s = coord.X - 0.5f;
        float t = coord.Y - 0.5f;

        float rotated = s * cos + t * sin;
        t = -s * sin + t * cos;
        s = rotated;

        s *= scaleS;
        t *= scaleT;

        return new Vector2(s + offsetS + 0.5f, t + offsetT + 0.5f);
    }
}
