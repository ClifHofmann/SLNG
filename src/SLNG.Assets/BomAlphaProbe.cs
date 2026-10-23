using System;
using System.Numerics;

namespace SLNG.Assets;

/// <summary>
/// BUG-RENDER-50 follow-up: measures where a face's UVs land in a decoded texture's alpha -- for a
/// Bakes-on-Mesh face, how much of it the wearer's bake (alpha layers composited in) cuts away at the face's
/// alpha cutoff. Numbers only: on a Linden grid another resident's bake must never be written out as an image
/// (TPV §2.b, FEAT-SL-01), and a cut fraction answers "why is this face drawn / not drawn" just as well.
/// </summary>
public static class BomAlphaProbe
{
    /// <summary>Samples <paramref name="rgba"/> (top-origin rows, 4 bytes a texel) at every vertex and every
    /// triangle centre of a face, after the face's placement (<c>uv * scale + offset</c>, the shader's
    /// <c>slng_place_uv</c> without rotation), wrapping like a repeat sampler. A texel counts as cut when its
    /// alpha is below <paramref name="threshold"/> (0..1).</summary>
    public static FaceAlphaCoverage Measure(byte[] rgba, int width, int height, ReadOnlySpan<Vector2> uvs,
        ReadOnlySpan<int> indices, Vector2 scale, Vector2 offset, float threshold)
    {
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4 || uvs.Length == 0)
            return default;
        int samples = 0, below = 0, min = 255;
        long sum = 0;

        void Sample(Vector2 uv)
        {
            var p = uv * scale + offset;
            float u = p.X - MathF.Floor(p.X);
            float v = p.Y - MathF.Floor(p.Y);
            int x = Math.Clamp((int)(u * width), 0, width - 1);
            int y = Math.Clamp((int)(v * height), 0, height - 1);
            int a = rgba[(y * width + x) * 4 + 3];
            samples++;
            sum += a;
            if (a < min) min = a;
            if (a / 255f < threshold) below++; // the viewer's own comparison: texel alpha < cutoff/255
        }

        foreach (var uv in uvs) Sample(uv);
        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            int i0 = indices[t], i1 = indices[t + 1], i2 = indices[t + 2];
            if ((uint)i0 >= (uint)uvs.Length || (uint)i1 >= (uint)uvs.Length || (uint)i2 >= (uint)uvs.Length) continue;
            Sample((uvs[i0] + uvs[i1] + uvs[i2]) / 3f);
        }
        return new FaceAlphaCoverage(samples, below, min, samples == 0 ? 0f : (float)sum / samples);
    }
}
