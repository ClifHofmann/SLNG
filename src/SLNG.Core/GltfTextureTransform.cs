using System;
using System.Numerics;

namespace SLNG.Core;

/// <summary>One slot's KHR_texture_transform, in the viewer's convention (see FEAT-RENDER-26).</summary>
public readonly record struct GltfTextureTransform(Vector2 Offset, Vector2 Scale, float Rotation)
{
    public static readonly GltfTextureTransform Identity = new(Vector2.Zero, Vector2.One, 0f);

    /// <summary>The viewer's khr_texture_transform (textureUtilV.glsl:36-49), written out: scale, then rotate, then
    /// offset, about the top-left corner. <paramref name="uv"/> is SLNG's mesh UV (top-origin V); the result is the
    /// coordinate to sample at. A C# MIRROR of slng_khr_uv in prim_common.gdshaderinc: change both together.</summary>
    public Vector2 Apply(Vector2 uv)
    {
        var sc = uv * Scale;
        float s = MathF.Sin(Rotation), c = MathF.Cos(Rotation);
        return new Vector2(c * sc.X + s * sc.Y + Offset.X, -s * sc.X + c * sc.Y + Offset.Y);
    }
}
