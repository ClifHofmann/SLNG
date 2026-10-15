using System;
using System.Numerics;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

public class GltfTextureTransformTests
{
    [Fact]
    public void Identity_Leaves_Uv_Unchanged()
    {
        var t = GltfTextureTransform.Identity;
        var uv = new Vector2(0.3f, 0.7f);
        var res = t.Apply(uv);
        Assert.Equal(0.3f, res.X, 5);
        Assert.Equal(0.7f, res.Y, 5);
    }

    [Fact]
    public void Scale_Scales_Uv()
    {
        var t = new GltfTextureTransform(Vector2.Zero, new Vector2(2f, 3f), 0f);
        var res = t.Apply(new Vector2(0.5f, 0.5f));
        Assert.Equal(1.0f, res.X, 5);
        Assert.Equal(1.5f, res.Y, 5);
    }

    [Fact]
    public void Offset_Offsets_Uv()
    {
        var t = new GltfTextureTransform(new Vector2(0.25f, -0.5f), Vector2.One, 0f);
        var res = t.Apply(new Vector2(0f, 0f));
        Assert.Equal(0.25f, res.X, 5);
        Assert.Equal(-0.5f, res.Y, 5);
    }

    [Fact]
    public void Rotation_PiOver2()
    {
        var t = new GltfTextureTransform(Vector2.Zero, Vector2.One, MathF.PI / 2f);
        var res1 = t.Apply(new Vector2(1f, 0f));
        Assert.Equal(0f, res1.X, 5);
        Assert.Equal(-1f, res1.Y, 5);

        var res2 = t.Apply(new Vector2(0f, 1f));
        Assert.Equal(1f, res2.X, 5);
        Assert.Equal(0f, res2.Y, 5);
    }

    [Fact]
    public void Scale_Then_Rotate_Order()
    {
        // scale (2,1), rotation π/2: (1,0) -> (0,-2) (pins "scale first, then rotate")
        var t = new GltfTextureTransform(Vector2.Zero, new Vector2(2f, 1f), MathF.PI / 2f);
        var res = t.Apply(new Vector2(1f, 0f));
        Assert.Equal(0f, res.X, 5);
        Assert.Equal(-2f, res.Y, 5);
    }

    [Fact]
    public void Combined_Scale_Rotate_Offset()
    {
        // scale (2,2), rotation π, offset (1,1): (0.25,0.5) -> (0.5,0)
        var t = new GltfTextureTransform(new Vector2(1f, 1f), new Vector2(2f, 2f), MathF.PI);
        var res = t.Apply(new Vector2(0.25f, 0.5f));
        Assert.Equal(0.5f, res.X, 5);
        Assert.Equal(0.0f, res.Y, 5);
    }
}
