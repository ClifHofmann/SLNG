using System;
using System.Numerics;
using SLNG.Assets;
using Xunit;

namespace SLNG.Assets.Tests;

/// <summary>BUG-RENDER-50 follow-up: the [BomAlpha] measurement -- how much of a face a texture's alpha cuts
/// away at the face's cutoff, sampled at the face's own UVs.</summary>
public class BomAlphaProbeTests
{
    // 4x2 texels, top-origin rows: the left half alpha 0 (an alpha layer's hole), the right half opaque, and
    // one texel exactly at the cutoff of a legacy MASK 10.
    private static byte[] Texture()
    {
        var rgba = new byte[4 * 2 * 4];
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 4; x++)
                rgba[(y * 4 + x) * 4 + 3] = x < 2 ? (byte)0 : (byte)255;
        rgba[(1 * 4 + 3) * 4 + 3] = 10;
        return rgba;
    }

    private const float Mask10 = 10f / 255f;

    [Fact]
    public void A_face_inside_the_hole_is_cut_away_entirely()
    {
        var uvs = new[] { new Vector2(0.05f, 0.1f), new Vector2(0.4f, 0.1f), new Vector2(0.2f, 0.9f) };
        var c = BomAlphaProbe.Measure(Texture(), 4, 2, uvs, new[] { 0, 1, 2 }, Vector2.One, Vector2.Zero, Mask10);
        Assert.Equal(4, c.Samples);
        Assert.Equal(1f, c.CutFraction);
        Assert.Equal(0, c.MinAlpha);
    }

    [Fact]
    public void A_face_on_the_opaque_half_is_kept_and_alpha_at_the_cutoff_is_not_cut()
    {
        // The viewer discards below minimum_alpha (materialF.glsl), so a texel AT the cutoff stays.
        var uvs = new[] { new Vector2(0.6f, 0.1f), new Vector2(0.9f, 0.1f), new Vector2(0.9f, 0.9f) };
        var c = BomAlphaProbe.Measure(Texture(), 4, 2, uvs, new[] { 0, 1, 2 }, Vector2.One, Vector2.Zero, Mask10);
        Assert.Equal(0f, c.CutFraction);
        Assert.Equal(10, c.MinAlpha);
    }

    [Fact]
    public void Placement_and_wrapping_move_the_samples_like_the_shader()
    {
        // uv * scale + offset, wrapped: an offset of half a texture moves a left-half face onto the right half.
        var uvs = new[] { new Vector2(0.1f, 0.1f), new Vector2(0.3f, 0.1f), new Vector2(0.2f, 0.4f) };
        var c = BomAlphaProbe.Measure(Texture(), 4, 2, uvs, new[] { 0, 1, 2 }, Vector2.One, new Vector2(1.5f, 0f), Mask10);
        Assert.Equal(0f, c.CutFraction);
    }

    [Fact]
    public void Nothing_to_measure_is_reported_as_nothing()
    {
        Assert.Equal(0, BomAlphaProbe.Measure(Array.Empty<byte>(), 4, 2, new[] { Vector2.Zero }, Array.Empty<int>(),
            Vector2.One, Vector2.Zero, Mask10).Samples);
    }
}
