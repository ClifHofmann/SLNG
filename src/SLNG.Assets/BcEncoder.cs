namespace SLNG.Assets;

/// <summary>
/// FEAT-PERF-17 Part B: a BC1 / BC3 (DXT1 / DXT5) block compressor in plain C#, for the texture
/// workers.
///
/// <para>Why our own: Godot's <c>Image.Compress</c> has no compressor in the stock 4.7 export
/// templates. etcpak registers its BC/ETC encoders only under <c>TOOLS_ENABLED</c>
/// (modules/etcpak/register_types.cpp), cvtt and betsy build only for the editor unless the
/// <c>cvtt_export_templates</c> / <c>betsy_export_templates</c> SCons options are set (their
/// config.py, 4.7-stable), and a release template run of <c>Image.compress(COMPRESS_S3TC)</c>
/// returns ERR_UNAVAILABLE ("_image_compress_bc_func is null"). Decompression (bcdec) is in the
/// templates. No new package: the alternatives were BCnEncoder.NET (MIT, a NuGet dependency with its
/// own dependency, BC7 far too slow for a worker) and a native library per platform.</para>
///
/// <para>The algorithm is the one of stb_dxt by Fabian Giesen (public domain / MIT, the same choice as
/// this repository's licence), written anew for spans: optimal endpoints from a table for a single-colour
/// block; otherwise the principal axis of the block's colours by power iteration, its two extreme
/// texels as endpoints, every texel matched to the nearest of the four palette colours, then up to two
/// least-squares refits of the endpoints, keeping a fit only if it lowers the error. The colour block is
/// always written in 4-colour mode (c0 &gt; c1): Godot samples BC1 as RGB with alpha forced to 1, where
/// the 3-colour mode's fourth entry would turn a texel black, and BC3's colour block has no 3-colour
/// mode at all. The alpha block tries both of BC3's modes (8 interpolated values, or 6 plus exact 0
/// and 255) and keeps the smaller error, so cutout edges stay exact.</para>
/// </summary>
public static class BcEncoder
{
    private static readonly byte[] Expand5 = BuildExpand(5);
    private static readonly byte[] Expand6 = BuildExpand(6);
    // For each 8-bit value, the (c0, c1) endpoint pair whose 2/3 interpolant comes closest -- a
    // single-colour block encoded as all-index-2 hits the colour far better than one rounded endpoint.
    private static readonly byte[] OMatch5 = BuildOptimalTable(Expand5);
    private static readonly byte[] OMatch6 = BuildOptimalTable(Expand6);

    // The weight of c0, in thirds, of each 4-colour palette index: c0, c1, (2c0+c1)/3, (c0+2c1)/3.
    private static readonly int[] C0Weight = { 3, 0, 2, 1 };

    /// <summary>
    /// Encodes a whole RGBA8 chain (Godot's layout: the levels one after another, down to 1x1 when
    /// <paramref name="mipmaps"/>) into the same chain in <paramref name="format"/>, laid out as
    /// <see cref="BcLayout"/> says. Sides need not be multiples of 4; partial blocks repeat the edge.
    /// </summary>
    public static byte[] EncodeChain(GpuTextureFormat format, ReadOnlySpan<byte> rgbaChain, int width, int height, bool mipmaps)
    {
        if (format == GpuTextureFormat.Rgba8) throw new ArgumentException("not a block format", nameof(format));
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        long needed = BcLayout.ChainBytes(GpuTextureFormat.Rgba8, width, height, mipmaps);
        if (rgbaChain.Length < needed)
            throw new ArgumentException($"RGBA chain is {rgbaChain.Length} bytes, {width}x{height} mipmaps={mipmaps} needs {needed}", nameof(rgbaChain));

        var dst = new byte[BcLayout.ChainBytes(format, width, height, mipmaps)];
        int levels = BcLayout.LevelCount(width, height, mipmaps);
        int src = 0, dstOffset = 0, w = width, h = height;
        for (int level = 0; level < levels; level++)
        {
            int srcLen = w * h * 4;
            int dstLen = (int)BcLayout.LevelBytes(format, w, h);
            EncodeLevel(format, rgbaChain.Slice(src, srcLen), w, h, dst.AsSpan(dstOffset, dstLen));
            src += srcLen;
            dstOffset += dstLen;
            w = Math.Max(1, w >> 1);
            h = Math.Max(1, h >> 1);
        }
        return dst;
    }

    /// <summary>Encodes one level of tightly packed RGBA8 into <paramref name="dst"/>
    /// (<see cref="BcLayout.LevelBytes"/> long), blocks row by row.</summary>
    public static void EncodeLevel(GpuTextureFormat format, ReadOnlySpan<byte> rgba, int width, int height, Span<byte> dst)
    {
        int blockBytes = BcLayout.BlockBytes(format);
        if (blockBytes == 0) throw new ArgumentException("not a block format", nameof(format));
        int blocksX = (width + 3) / 4, blocksY = (height + 3) / 4;
        if (dst.Length < blocksX * blocksY * blockBytes) throw new ArgumentException("destination too small", nameof(dst));

        Span<byte> block = stackalloc byte[64];
        int stride = width * 4;
        int o = 0;
        for (int by = 0; by < blocksY; by++)
        {
            int y0 = by * 4;
            for (int bx = 0; bx < blocksX; bx++)
            {
                int x0 = bx * 4;
                if (x0 + 4 <= width && y0 + 4 <= height)
                {
                    for (int py = 0; py < 4; py++)
                        rgba.Slice((y0 + py) * stride + x0 * 4, 16).CopyTo(block.Slice(py * 16, 16));
                }
                else
                {
                    // A partial block at the right / bottom edge: repeat the last row and column.
                    for (int py = 0; py < 4; py++)
                    {
                        int row = Math.Min(y0 + py, height - 1) * stride;
                        for (int px = 0; px < 4; px++)
                        {
                            int s = row + Math.Min(x0 + px, width - 1) * 4;
                            rgba.Slice(s, 4).CopyTo(block.Slice((py * 4 + px) * 4, 4));
                        }
                    }
                }

                if (format == GpuTextureFormat.Bc3)
                {
                    EncodeAlphaBlock(block, dst.Slice(o, 8));
                    EncodeColorBlock(block, dst.Slice(o + 8, 8));
                }
                else
                {
                    EncodeColorBlock(block, dst.Slice(o, 8));
                }
                o += blockBytes;
            }
        }
    }

    private static void EncodeColorBlock(ReadOnlySpan<byte> px, Span<byte> dst)
    {
        int minR = 255, minG = 255, minB = 255, maxR = 0, maxG = 0, maxB = 0;
        int sumR = 0, sumG = 0, sumB = 0;
        for (int i = 0; i < 64; i += 4)
        {
            int r = px[i], g = px[i + 1], b = px[i + 2];
            sumR += r; sumG += g; sumB += b;
            if (r < minR) minR = r;
            if (r > maxR) maxR = r;
            if (g < minG) minG = g;
            if (g > maxG) maxG = g;
            if (b < minB) minB = b;
            if (b > maxB) maxB = b;
        }

        if (minR == maxR && minG == maxG && minB == maxB)
        {
            int c0 = (OMatch5[minR * 2] << 11) | (OMatch6[minG * 2] << 5) | OMatch5[minB * 2];
            int c1 = (OMatch5[minR * 2 + 1] << 11) | (OMatch6[minG * 2 + 1] << 5) | OMatch5[minB * 2 + 1];
            WriteColorBlock(dst, c0, c1, 0xAAAAAAAAu); // every texel on index 2, (2 c0 + c1) / 3
            return;
        }

        // The colours' covariance, and its principal axis by power iteration from the bounding box's diagonal.
        float meanR = sumR / 16f, meanG = sumG / 16f, meanB = sumB / 16f;
        float c00 = 0, c01 = 0, c02 = 0, c11 = 0, c12 = 0, c22 = 0;
        for (int i = 0; i < 64; i += 4)
        {
            float r = px[i] - meanR, g = px[i + 1] - meanG, b = px[i + 2] - meanB;
            c00 += r * r; c01 += r * g; c02 += r * b;
            c11 += g * g; c12 += g * b; c22 += b * b;
        }
        float vr = maxR - minR, vg = maxG - minG, vb = maxB - minB;
        for (int iter = 0; iter < 4; iter++)
        {
            float r = vr * c00 + vg * c01 + vb * c02;
            float g = vr * c01 + vg * c11 + vb * c12;
            float b = vr * c02 + vg * c12 + vb * c22;
            float m = Math.Max(Math.Abs(r), Math.Max(Math.Abs(g), Math.Abs(b)));
            if (m < 1e-6f) break;
            vr = r / m; vg = g / m; vb = b / m;
        }
        int ar, ag, ab;
        float mag = Math.Max(Math.Abs(vr), Math.Max(Math.Abs(vg), Math.Abs(vb)));
        if (mag < 1e-6f)
        {
            ar = 299; ag = 587; ab = 114; // no usable axis: luminance
        }
        else
        {
            ar = (int)(vr / mag * 512f); ag = (int)(vg / mag * 512f); ab = (int)(vb / mag * 512f);
        }

        int minDot = int.MaxValue, maxDot = int.MinValue, minAt = 0, maxAt = 0;
        for (int i = 0; i < 64; i += 4)
        {
            int dot = px[i] * ar + px[i + 1] * ag + px[i + 2] * ab;
            if (dot < minDot) { minDot = dot; minAt = i; }
            if (dot > maxDot) { maxDot = dot; maxAt = i; }
        }

        int best0 = As565(px[maxAt], px[maxAt + 1], px[maxAt + 2]);
        int best1 = As565(px[minAt], px[minAt + 1], px[minAt + 2]);
        uint bestMask = MatchColors(px, best0, best1, out int bestErr);

        for (int iter = 0; iter < 2 && bestErr > 0; iter++)
        {
            if (!RefineEndpoints(px, bestMask, out int n0, out int n1)) break;
            if (n0 == best0 && n1 == best1) break;
            uint mask = MatchColors(px, n0, n1, out int err);
            if (err >= bestErr) break;
            best0 = n0; best1 = n1; bestMask = mask; bestErr = err;
        }
        WriteColorBlock(dst, best0, best1, bestMask);
    }

    /// <summary>Each texel's nearest of the four palette colours of the 565 endpoints, and the summed
    /// squared RGB error.</summary>
    private static uint MatchColors(ReadOnlySpan<byte> px, int c0, int c1, out int error)
    {
        int r0 = Expand5[c0 >> 11], g0 = Expand6[(c0 >> 5) & 63], b0 = Expand5[c0 & 31];
        int r1 = Expand5[c1 >> 11], g1 = Expand6[(c1 >> 5) & 63], b1 = Expand5[c1 & 31];
        int r2 = Lerp13(r0, r1), g2 = Lerp13(g0, g1), b2 = Lerp13(b0, b1);
        int r3 = Lerp13(r1, r0), g3 = Lerp13(g1, g0), b3 = Lerp13(b1, b0);

        // The four palette colours lie on one line (to within the decoder's rounding), so the nearest
        // one is the nearest along that line: one dot product per texel against the midpoints between
        // neighbours, in the line's order c1, c3, c2, c0 (stb_dxt's matcher).
        int dr = r0 - r1, dg = g0 - g1, db = b0 - b1;
        int s0 = r0 * dr + g0 * dg + b0 * db, s1 = r1 * dr + g1 * dg + b1 * db;
        int s2 = r2 * dr + g2 * dg + b2 * db, s3 = r3 * dr + g3 * dg + b3 * db;
        int cut13 = s1 + s3, cut32 = s3 + s2, cut20 = s2 + s0;

        uint mask = 0;
        int err = 0;
        for (int i = 0; i < 16; i++)
        {
            int r = px[i * 4], g = px[i * 4 + 1], b = px[i * 4 + 2];
            int dot2 = 2 * (r * dr + g * dg + b * db);
            int idx, d;
            if (dot2 < cut13) { idx = 1; d = Sq(r - r1) + Sq(g - g1) + Sq(b - b1); }
            else if (dot2 < cut32) { idx = 3; d = Sq(r - r3) + Sq(g - g3) + Sq(b - b3); }
            else if (dot2 < cut20) { idx = 2; d = Sq(r - r2) + Sq(g - g2) + Sq(b - b2); }
            else { idx = 0; d = Sq(r - r0) + Sq(g - g0) + Sq(b - b0); }
            err += d;
            mask |= (uint)idx << (2 * i);
        }
        error = err;
        return mask;
    }

    /// <summary>The least-squares endpoints for the texels' current palette indices. False when every
    /// texel is on one index (the system has no unique solution), which leaves the fit as it is.</summary>
    private static bool RefineEndpoints(ReadOnlySpan<byte> px, uint mask, out int c0, out int c1)
    {
        int aa = 0, bb = 0, ab = 0;
        int axR = 0, axG = 0, axB = 0, bxR = 0, bxG = 0, bxB = 0;
        for (int i = 0; i < 16; i++)
        {
            int a = C0Weight[(int)(mask >> (2 * i)) & 3], b = 3 - a;
            int r = px[i * 4], g = px[i * 4 + 1], bl = px[i * 4 + 2];
            aa += a * a; bb += b * b; ab += a * b;
            axR += a * r; axG += a * g; axB += a * bl;
            bxR += b * r; bxG += b * g; bxB += b * bl;
        }
        int det = aa * bb - ab * ab;
        if (det == 0)
        {
            c0 = c1 = 0;
            return false;
        }
        float f = 3f / det;
        c0 = (Quantize((bb * axR - ab * bxR) * f, 31) << 11) | (Quantize((bb * axG - ab * bxG) * f, 63) << 5) | Quantize((bb * axB - ab * bxB) * f, 31);
        c1 = (Quantize((aa * bxR - ab * axR) * f, 31) << 11) | (Quantize((aa * bxG - ab * axG) * f, 63) << 5) | Quantize((aa * bxB - ab * axB) * f, 31);
        return true;
    }

    /// <summary>Writes a colour block in 4-colour mode: endpoints swapped (and the indices with them)
    /// when c0 &lt; c1; all on index 0 when they are equal.</summary>
    private static void WriteColorBlock(Span<byte> dst, int c0, int c1, uint mask)
    {
        if (c0 < c1)
        {
            (c0, c1) = (c1, c0);
            mask ^= 0x55555555u; // 0 <-> 1, 2 <-> 3
        }
        else if (c0 == c1)
        {
            mask = 0;
        }
        dst[0] = (byte)c0;
        dst[1] = (byte)(c0 >> 8);
        dst[2] = (byte)c1;
        dst[3] = (byte)(c1 >> 8);
        dst[4] = (byte)mask;
        dst[5] = (byte)(mask >> 8);
        dst[6] = (byte)(mask >> 16);
        dst[7] = (byte)(mask >> 24);
    }

    private static void EncodeAlphaBlock(ReadOnlySpan<byte> px, Span<byte> dst)
    {
        int min = 255, max = 0, innerMin = 255, innerMax = 0;
        bool hasExtreme = false;
        for (int i = 3; i < 64; i += 4)
        {
            int a = px[i];
            if (a < min) min = a;
            if (a > max) max = a;
            if (a == 0 || a == 255)
            {
                hasExtreme = true;
            }
            else
            {
                if (a < innerMin) innerMin = a;
                if (a > innerMax) innerMax = a;
            }
        }

        if (min == max)
        {
            // a0 <= a1: 6-value mode, every texel on index 0 = a0 exactly.
            dst[0] = (byte)min;
            dst[1] = (byte)min;
            dst.Slice(2, 6).Clear();
            return;
        }

        // 8 interpolated values: a0 > a1.
        Span<int> palette = stackalloc int[8];
        palette[0] = max;
        palette[1] = min;
        for (int k = 2; k < 8; k++) palette[k] = ((8 - k) * max + (k - 1) * min + 3) / 7;
        ulong bits = MatchAlpha(px, palette, out int err);
        int a0 = max, a1 = min;

        // 6 interpolated values between the inner texels, plus exact 0 and 255: a0 <= a1. Better for a
        // cutout edge, where a few mid values sit next to 0 and 255.
        if (hasExtreme && innerMin <= innerMax && err > 0)
        {
            palette[0] = innerMin;
            palette[1] = innerMax;
            for (int k = 2; k < 6; k++) palette[k] = ((6 - k) * innerMin + (k - 1) * innerMax + 2) / 5;
            palette[6] = 0;
            palette[7] = 255;
            ulong bits6 = MatchAlpha(px, palette, out int err6);
            if (err6 < err)
            {
                bits = bits6;
                a0 = innerMin;
                a1 = innerMax;
            }
        }

        dst[0] = (byte)a0;
        dst[1] = (byte)a1;
        for (int j = 0; j < 6; j++) dst[2 + j] = (byte)(bits >> (8 * j));
    }

    private static ulong MatchAlpha(ReadOnlySpan<byte> px, ReadOnlySpan<int> palette, out int error)
    {
        ulong bits = 0;
        int err = 0;
        for (int i = 0; i < 16; i++)
        {
            int a = px[i * 4 + 3];
            int idx = 0, best = int.MaxValue;
            for (int k = 0; k < 8; k++)
            {
                int d = Math.Abs(a - palette[k]);
                if (d < best) { best = d; idx = k; }
            }
            err += best * best;
            bits |= (ulong)idx << (3 * i);
        }
        error = err;
        return bits;
    }

    private static int Sq(int v) => v * v;

    /// <summary>(2a + b) / 3, the decoder's 2/3 interpolant.</summary>
    private static int Lerp13(int a, int b) => (2 * a + b) / 3;

    /// <summary>a * b / 255 with rounding (stb_dxt's Mul8Bit).</summary>
    private static int Mul8Bit(int a, int b)
    {
        int t = a * b + 128;
        return (t + (t >> 8)) >> 8;
    }

    private static int As565(int r, int g, int b) => (Mul8Bit(r, 31) << 11) | (Mul8Bit(g, 63) << 5) | Mul8Bit(b, 31);

    private static int Quantize(float v, int max)
    {
        int q = (int)(v * max / 255f + 0.5f);
        return q < 0 ? 0 : q > max ? max : q;
    }

    private static byte[] BuildExpand(int bits)
    {
        var table = new byte[1 << bits];
        for (int v = 0; v < table.Length; v++)
            table[v] = (byte)(bits == 5 ? (v << 3) | (v >> 2) : (v << 2) | (v >> 4));
        return table;
    }

    private static byte[] BuildOptimalTable(byte[] expand)
    {
        var table = new byte[256 * 2];
        for (int i = 0; i < 256; i++)
        {
            int bestErr = int.MaxValue;
            for (int mn = 0; mn < expand.Length; mn++)
            {
                for (int mx = 0; mx < expand.Length; mx++)
                {
                    int mine = expand[mn], maxe = expand[mx];
                    // A small penalty for far-apart endpoints: hardware interpolates with some
                    // tolerance, and close endpoints keep that tolerance small.
                    int err = Math.Abs(Lerp13(maxe, mine) - i) * 100 + Math.Abs(maxe - mine) * 3;
                    if (err < bestErr)
                    {
                        bestErr = err;
                        table[i * 2] = (byte)mx;
                        table[i * 2 + 1] = (byte)mn;
                    }
                }
            }
        }
        return table;
    }
}
