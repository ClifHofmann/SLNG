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
        ReadOnlySpan<int> indices, Vector2 scale, Vector2 offset, float threshold) =>
        Measure(rgba, width, height, 4, 3, uvs, indices, scale, offset, threshold);

    /// <summary><see cref="Measure(byte[], int, int, ReadOnlySpan{Vector2}, ReadOnlySpan{int}, Vector2, Vector2, float)"/>
    /// over an alpha-only map (one byte a texel), e.g. one made by <see cref="DownsampleAlpha"/>.</summary>
    public static FaceAlphaCoverage MeasureAlpha(byte[] alpha, int width, int height, ReadOnlySpan<Vector2> uvs,
        ReadOnlySpan<int> indices, Vector2 scale, Vector2 offset, float threshold) =>
        Measure(alpha, width, height, 1, 0, uvs, indices, scale, offset, threshold);

    /// <summary>An alpha-only copy of <paramref name="rgba"/> at most <paramref name="maxSide"/> texels a side,
    /// each texel the mean of the block it covers -- what a mip of that size would hold. Small enough to keep
    /// in memory for every bake a session sees; never meant to leave it.</summary>
    public static (byte[] Alpha, int Width, int Height) DownsampleAlpha(byte[] rgba, int width, int height, int maxSide)
    {
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4 || maxSide <= 0)
            return (Array.Empty<byte>(), 0, 0);
        int stepX = Math.Max(1, (width + maxSide - 1) / maxSide);
        int stepY = Math.Max(1, (height + maxSide - 1) / maxSide);
        int w = (width + stepX - 1) / stepX, h = (height + stepY - 1) / stepY;
        var alpha = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int sum = 0, n = 0;
                for (int sy = y * stepY; sy < Math.Min(height, (y + 1) * stepY); sy++)
                    for (int sx = x * stepX; sx < Math.Min(width, (x + 1) * stepX); sx++, n++)
                        sum += rgba[(sy * width + sx) * 4 + 3];
                alpha[y * w + x] = (byte)((sum + n / 2) / Math.Max(1, n));
            }
        }
        return (alpha, w, h);
    }

    private static FaceAlphaCoverage Measure(byte[] data, int width, int height, int stride, int alphaOffset,
        ReadOnlySpan<Vector2> uvs, ReadOnlySpan<int> indices, Vector2 scale, Vector2 offset, float threshold)
    {
        if (width <= 0 || height <= 0 || data.Length < width * height * stride || uvs.Length == 0)
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
            int a = data[(y * width + x) * stride + alphaOffset];
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
