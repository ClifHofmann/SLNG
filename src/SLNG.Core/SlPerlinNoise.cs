using System;

namespace SLNG.Core;

/// <summary>
/// Port of the Second Life viewer's classic Perlin noise (<c>indra/newview/noise.cpp</c> +
/// <c>noise.h</c>), which is what drives terrain detail-texture composition.
///
/// Two traps make this worth a careful port rather than reaching for any Perlin implementation:
///
/// 1. <c>indra/llmath/llperlin.cpp</c> looks like the same thing and is NOT — nothing in the
///    viewer tree references it, it is dead code, and (unlike this one) its table init never
///    seeds <c>rand()</c>, so its output would depend on ambient RNG state.
/// 2. This is <em>classic gradient</em> Perlin on a 256-entry lattice with the cubic ease
///    <c>3t^2-2t^3</c>. Simplex noise (e.g. the Ashima <c>snoise</c> in <c>scratch/noise2D.glsl</c>)
///    produces a visibly different pattern from the same constants.
///
/// The gradient/permutation tables come from <c>srand(42)</c> followed by the C runtime's
/// <c>rand()</c> — so the table, and therefore every SL region's terrain pattern, depends on
/// which CRT the viewer was built against. <see cref="MsvcRand"/> reproduces the Microsoft CRT
/// generator, which is what a Windows Firestorm/Linden viewer uses. A glibc-built viewer draws a
/// different table and genuinely paints the same region differently; Windows is our parity target.
/// </summary>
public static class SlPerlinNoise
{
    /// <summary>Lattice size (<c>B</c> in noise.h).</summary>
    public const int B = 0x100;

    /// <summary>Seed noise.h's <c>init()</c> uses, with the comment "we want repeatable noise
    /// (e.g. for stable terrain texturing), so seed with known value".</summary>
    public const int Seed = 42;

    private static readonly int[] _p = new int[B];
    private static readonly float[] _g2 = new float[B * 2];

    static SlPerlinNoise() => Init(_p, _g2);

    /// <summary>Permutation table, 256 entries. noise.h allocates <c>B+B+2</c> and mirrors the
    /// first half into the second so it can index past the end without a modulo; every such read
    /// is equivalent to <c>p[i &amp; 0xFF]</c>, so we keep only the 256 distinct values.</summary>
    public static ReadOnlySpan<int> Permutation => _p;

    /// <summary>Normalized 2D gradients, 256 pairs stored x0,y0,x1,y1,...</summary>
    public static ReadOnlySpan<float> Gradients2D => _g2;

    /// <summary>
    /// The Microsoft CRT <c>rand()</c>: a 32-bit LCG whose output is bits 30..16 of the state.
    /// </summary>
    public sealed class MsvcRand
    {
        private uint _state;

        public MsvcRand(int seed) => _state = (uint)seed;

        public int Next()
        {
            _state = unchecked(_state * 214013u + 2531011u);
            return (int)((_state >> 16) & 0x7FFF);
        }
    }

    /// <summary>
    /// Reproduces noise.h's <c>init()</c>. Exposed so the table can be regenerated (and asserted
    /// against) rather than pasted in as opaque data.
    /// </summary>
    public static void Init(int[] p, float[] g2)
    {
        if (p.Length != B) throw new ArgumentException($"p must have {B} entries", nameof(p));
        if (g2.Length != B * 2) throw new ArgumentException($"g2 must have {B * 2} entries", nameof(g2));

        var rng = new MsvcRand(Seed);

        int i, j;
        for (i = 0; i < B; i++)
        {
            p[i] = i;

            // g1[i] -- unused by noise2, but it consumes a draw and therefore shifts every
            // subsequent value. Dropping it would silently produce a different table.
            _ = (rng.Next() % (B + B) - B) / (float)B;

            float gx = (rng.Next() % (B + B) - B) / (float)B;
            float gy = (rng.Next() % (B + B) - B) / (float)B;
            // normalize2() in noise.h does the sqrt in double and narrows, so match that
            // rather than MathF.Sqrt, which can differ in the last ulp.
            float s = (float)(1.0 / Math.Sqrt((double)(gx * gx + gy * gy)));
            g2[i * 2] = gx * s;
            g2[i * 2 + 1] = gy * s;

            // g3[i] -- likewise unused here but part of the draw sequence.
            for (j = 0; j < 3; j++) _ = (rng.Next() % (B + B) - B) / (float)B;
        }

        // while (--i) : i enters at B and the body runs for i = 255 down to 1.
        while (--i > 0)
        {
            int k = p[i];
            j = rng.Next() % B;
            p[i] = p[j];
            p[j] = k;
        }
    }

    /// <summary>
    /// noise.h's <c>fast_setup</c>. The +4096 bias is what lets the lattice index be taken by
    /// truncating to 8 bits, and it is also why the fractional part is quantized: at SL's global
    /// coordinates the float32 sum has already lost low bits. The viewer computes this in F32 too,
    /// so keeping the bias is what preserves parity rather than what costs it.
    /// </summary>
    private static void FastSetup(float v, out int b0, out int b1, out float r0, out float r1)
    {
        float t = v + 4096f;
        int ti = (int)t;
        b0 = ti & 0xFF;          // (U8)t_S32
        b1 = (b0 + 1) & 0xFF;    // b1 is a U8 in the original, so it wraps
        r0 = t - ti;
        r1 = r0 - 1f;
    }

    private static float SCurve(float t) => t * t * (3f - 2f * t);

    private static float Lerp(float t, float a, float b) => a + t * (b - a);

    private static float At2(float rx, float ry, int gi) => rx * _g2[gi * 2] + ry * _g2[gi * 2 + 1];

    /// <summary>2D classic Perlin noise, matching <c>noise2()</c> in noise.cpp.</summary>
    public static float Noise2(float x, float y)
    {
        FastSetup(x, out int bx0, out int bx1, out float rx0, out float rx1);
        FastSetup(y, out int by0, out int by1, out float ry0, out float ry1);

        int i = _p[bx0];
        int j = _p[bx1];

        int b00 = _p[(i + by0) & 0xFF];
        int b10 = _p[(j + by0) & 0xFF];
        int b01 = _p[(i + by1) & 0xFF];
        int b11 = _p[(j + by1) & 0xFF];

        float sx = SCurve(rx0);
        float sy = SCurve(ry0);

        float a = Lerp(sx, At2(rx0, ry0, b00), At2(rx1, ry0, b10));
        float b = Lerp(sx, At2(rx0, ry1, b01), At2(rx1, ry1, b11));

        return Lerp(sy, a, b);
    }

    /// <summary>
    /// <c>turbulence2()</c> from noise.h: octaves from <paramref name="freq"/> down to 1, each
    /// contributing <c>noise2(freq*v)/freq</c>. Terrain calls it with freq = 2, i.e. exactly two
    /// octaves — the base and one at double frequency and half amplitude.
    /// </summary>
    public static float Turbulence2(float x, float y, float freq)
    {
        float t = 0f;
        for (; freq >= 1f; freq *= 0.5f)
        {
            t += Noise2(freq * x, freq * y) / freq;
        }
        return t;
    }
}
