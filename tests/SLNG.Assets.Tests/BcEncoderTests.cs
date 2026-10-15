using System.Diagnostics;
using ImageMagick;
using Xunit;
using Xunit.Abstractions;

namespace SLNG.Assets.Tests;

/// <summary>FEAT-PERF-17 Part B: the BC1/BC3 encoder against a decoder written from the format
/// description (not from the encoder), against ImageMagick's DDS reader, and against quality floors.</summary>
public class BcEncoderTests
{
    private readonly ITestOutputHelper _out;

    public BcEncoderTests(ITestOutputHelper output) => _out = output;

    // ---- an independent decoder (D3D BC1/BC3 description) ----------------------------------------

    // 5 and 6 bit fields widened by bit replication, as the format description specifies.
    private static (int R, int G, int B) Unpack565(int c)
    {
        int r = (c >> 11) & 31, g = (c >> 5) & 63, b = c & 31;
        return ((r << 3) | (r >> 2), (g << 2) | (g >> 4), (b << 3) | (b >> 2));
    }

    private static void DecodeColorBlock(ReadOnlySpan<byte> blk, bool forceFourColor, Span<byte> outRgba16, bool writeAlpha)
    {
        int c0 = blk[0] | (blk[1] << 8), c1 = blk[2] | (blk[3] << 8);
        uint idx = (uint)(blk[4] | (blk[5] << 8) | (blk[6] << 16) | (blk[7] << 24));
        var (r0, g0, b0) = Unpack565(c0);
        var (r1, g1, b1) = Unpack565(c1);
        var pal = new (int R, int G, int B, int A)[4];
        pal[0] = (r0, g0, b0, 255);
        pal[1] = (r1, g1, b1, 255);
        if (forceFourColor || c0 > c1)
        {
            pal[2] = ((int)Math.Round((2.0 * r0 + r1) / 3), (int)Math.Round((2.0 * g0 + g1) / 3), (int)Math.Round((2.0 * b0 + b1) / 3), 255);
            pal[3] = ((int)Math.Round((r0 + 2.0 * r1) / 3), (int)Math.Round((g0 + 2.0 * g1) / 3), (int)Math.Round((b0 + 2.0 * b1) / 3), 255);
        }
        else
        {
            pal[2] = ((r0 + r1) / 2, (g0 + g1) / 2, (b0 + b1) / 2, 255);
            pal[3] = (0, 0, 0, 0);
        }
        for (int i = 0; i < 16; i++)
        {
            var p = pal[(idx >> (2 * i)) & 3];
            outRgba16[i * 4] = (byte)p.R;
            outRgba16[i * 4 + 1] = (byte)p.G;
            outRgba16[i * 4 + 2] = (byte)p.B;
            if (writeAlpha) outRgba16[i * 4 + 3] = 255;
        }
    }

    private static void DecodeAlphaBlock(ReadOnlySpan<byte> blk, Span<byte> outRgba16)
    {
        int a0 = blk[0], a1 = blk[1];
        var pal = new int[8];
        pal[0] = a0;
        pal[1] = a1;
        if (a0 > a1)
        {
            for (int k = 2; k < 8; k++) pal[k] = (int)Math.Round(((8 - k) * a0 + (k - 1) * a1) / 7.0);
        }
        else
        {
            for (int k = 2; k < 6; k++) pal[k] = (int)Math.Round(((6 - k) * a0 + (k - 1) * a1) / 5.0);
            pal[6] = 0;
            pal[7] = 255;
        }
        ulong bits = 0;
        for (int j = 0; j < 6; j++) bits |= (ulong)blk[2 + j] << (8 * j);
        for (int i = 0; i < 16; i++) outRgba16[i * 4 + 3] = (byte)pal[(int)((bits >> (3 * i)) & 7)];
    }

    /// <summary>One level back to RGBA8. BC1 as Godot samples it (RGB, alpha 1).</summary>
    internal static byte[] DecodeLevel(GpuTextureFormat format, ReadOnlySpan<byte> data, int width, int height)
    {
        var rgba = new byte[width * height * 4];
        int bw = (width + 3) / 4, bh = (height + 3) / 4, bb = BcLayout.BlockBytes(format);
        Span<byte> px = stackalloc byte[64];
        for (int by = 0; by < bh; by++)
        {
            for (int bx = 0; bx < bw; bx++)
            {
                var blk = data.Slice((by * bw + bx) * bb, bb);
                if (format == GpuTextureFormat.Bc3)
                {
                    DecodeColorBlock(blk.Slice(8), forceFourColor: true, px, writeAlpha: false);
                    DecodeAlphaBlock(blk, px);
                }
                else
                {
                    DecodeColorBlock(blk, forceFourColor: false, px, writeAlpha: true);
                }
                for (int py = 0; py < 4; py++)
                {
                    for (int pxx = 0; pxx < 4; pxx++)
                    {
                        int x = bx * 4 + pxx, y = by * 4 + py;
                        if (x >= width || y >= height) continue;
                        px.Slice((py * 4 + pxx) * 4, 4).CopyTo(rgba.AsSpan((y * width + x) * 4, 4));
                    }
                }
            }
        }
        return rgba;
    }

    // ---- test images ---------------------------------------------------------------------------

    /// <summary>Smooth colour gradients with a little texture: the case BC1 bands worst on.</summary>
    internal static byte[] Gradient(int w, int h, bool alpha)
    {
        var rgba = new byte[w * h * 4];
        var rng = new Random(12345);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                rgba[i] = (byte)(x * 255 / Math.Max(1, w - 1));
                rgba[i + 1] = (byte)(y * 255 / Math.Max(1, h - 1));
                rgba[i + 2] = (byte)Math.Clamp(128 + (int)(60 * Math.Sin(x * 0.05) * Math.Cos(y * 0.07)) + rng.Next(-3, 4), 0, 255);
                rgba[i + 3] = 255;
                if (alpha)
                {
                    // A cutout disc with an anti-aliased rim, like foliage or lace.
                    double d = Math.Sqrt(Math.Pow(x - w / 2.0, 2) + Math.Pow(y - h / 2.0, 2)) - w * 0.35;
                    rgba[i + 3] = (byte)Math.Clamp((int)(128 - d * 64), 0, 255);
                }
            }
        }
        return rgba;
    }

    private static double Psnr(byte[] a, byte[] b, int channelFrom, int channelTo)
    {
        double se = 0;
        long n = 0;
        for (int i = 0; i < a.Length; i += 4)
        {
            for (int c = channelFrom; c <= channelTo; c++)
            {
                int d = a[i + c] - b[i + c];
                se += d * d;
                n++;
            }
        }
        if (se == 0) return 99;
        double mse = se / n;
        return 10 * Math.Log10(255.0 * 255.0 / mse);
    }

    // ---- tests ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(GpuTextureFormat.Bc1, 4, 4, 8)]
    [InlineData(GpuTextureFormat.Bc1, 1024, 1024, 524288)]
    [InlineData(GpuTextureFormat.Bc3, 1024, 1024, 1048576)]
    [InlineData(GpuTextureFormat.Bc1, 6, 10, 2 * 3 * 8)]   // padded to whole blocks
    [InlineData(GpuTextureFormat.Rgba8, 6, 10, 240)]
    public void LevelBytes_AreWholeBlocks(GpuTextureFormat f, int w, int h, long expected)
        => Assert.Equal(expected, BcLayout.LevelBytes(f, w, h));

    [Fact]
    public void ChainBytes_MatchGodotsLayout()
    {
        // Godot 4.7 Image::_get_dst_image_size: levels to 1x1, each padded to 4x4 blocks.
        Assert.Equal(699064, BcLayout.ChainBytes(GpuTextureFormat.Bc1, 1024, 1024, mipmaps: true));
        Assert.Equal(1398128, BcLayout.ChainBytes(GpuTextureFormat.Bc3, 1024, 1024, mipmaps: true));
        Assert.Equal(11, BcLayout.LevelCount(1024, 1024, true));
        Assert.Equal(11, BcLayout.LevelCount(1024, 64, true));
        Assert.Equal(1, BcLayout.LevelCount(1024, 64, false));
        // 1024x64 -> ... -> 16x1 -> 8x1 -> 4x1 -> 2x1 -> 1x1
        long manual = 0;
        int w = 1024, h = 64;
        for (int i = 0; i < 11; i++)
        {
            manual += (long)((w + 3) / 4) * ((h + 3) / 4) * 8;
            w = Math.Max(1, w >> 1);
            h = Math.Max(1, h >> 1);
        }
        Assert.Equal(manual, BcLayout.ChainBytes(GpuTextureFormat.Bc1, 1024, 64, true));
    }

    [Fact]
    public void Bc1_SmoothGradient_KeepsQuality_AndUsesFourColourMode()
    {
        const int S = 256;
        var src = Gradient(S, S, alpha: false);
        var bc = BcEncoder.EncodeChain(GpuTextureFormat.Bc1, src, S, S, mipmaps: false);
        Assert.Equal(S * S / 2, bc.Length);

        for (int b = 0; b < bc.Length; b += 8)
        {
            int c0 = bc[b] | (bc[b + 1] << 8), c1 = bc[b + 2] | (bc[b + 3] << 8);
            uint idx = BitConverter.ToUInt32(bc, b + 4);
            // 4-colour mode, or equal endpoints with every texel on index 0 (never the black entry).
            Assert.True(c0 > c1 || (c0 == c1 && idx == 0), $"block {b / 8}: c0={c0} c1={c1} idx={idx:X8}");
        }

        var back = DecodeLevel(GpuTextureFormat.Bc1, bc, S, S);
        double psnr = Psnr(src, back, 0, 2);
        _out.WriteLine($"BC1 gradient PSNR {psnr:0.00} dB");
        Assert.True(psnr > 38, $"PSNR {psnr:0.00} dB");
        for (int i = 3; i < back.Length; i += 4) Assert.Equal(255, back[i]);
    }

    [Fact]
    public void Bc3_CutoutEdges_StayExact_AndColourHoldsUp()
    {
        const int S = 256;
        var src = Gradient(S, S, alpha: true);
        var bc = BcEncoder.EncodeChain(GpuTextureFormat.Bc3, src, S, S, mipmaps: false);
        Assert.Equal(S * S, bc.Length);
        var back = DecodeLevel(GpuTextureFormat.Bc3, bc, S, S);

        int wrongExtremes = 0, maxErr = 0;
        for (int i = 3; i < src.Length; i += 4)
        {
            if ((src[i] == 0 || src[i] == 255) && back[i] != src[i]) wrongExtremes++;
            maxErr = Math.Max(maxErr, Math.Abs(src[i] - back[i]));
        }
        double alphaPsnr = Psnr(src, back, 3, 3), colourPsnr = Psnr(src, back, 0, 2);
        _out.WriteLine($"BC3 alpha PSNR {alphaPsnr:0.00} dB (max error {maxErr}), colour PSNR {colourPsnr:0.00} dB");
        Assert.Equal(0, wrongExtremes);  // fully clear stays clear, fully opaque stays opaque
        // 8 values over a full 0..255 ramp are 36.4 apart: 18 is the format's own limit in such a block.
        Assert.True(maxErr <= 19, $"max alpha error {maxErr}");
        Assert.True(alphaPsnr > 45, $"alpha PSNR {alphaPsnr:0.00}");
        Assert.True(colourPsnr > 38, $"colour PSNR {colourPsnr:0.00}");
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(255, 255, 255)]
    [InlineData(127, 64, 200)]
    [InlineData(13, 250, 99)]
    public void SolidBlock_IsWithinTwoLevels(int r, int g, int b)
    {
        var src = new byte[64];
        for (int i = 0; i < 64; i += 4) { src[i] = (byte)r; src[i + 1] = (byte)g; src[i + 2] = (byte)b; src[i + 3] = 255; }
        var bc = BcEncoder.EncodeChain(GpuTextureFormat.Bc1, src, 4, 4, false);
        var back = DecodeLevel(GpuTextureFormat.Bc1, bc, 4, 4);
        for (int i = 0; i < 64; i += 4)
        {
            Assert.InRange(back[i], r - 2, r + 2);
            Assert.InRange(back[i + 1], g - 2, g + 2);
            Assert.InRange(back[i + 2], b - 2, b + 2);
        }
    }

    [Fact]
    public void OddSizes_PadWithTheEdge()
    {
        // 6x10: two blocks across, three down; the partial blocks repeat the last row and column.
        var src = Gradient(6, 10, alpha: true);
        var bc = BcEncoder.EncodeChain(GpuTextureFormat.Bc3, src, 6, 10, mipmaps: false);
        Assert.Equal(BcLayout.ChainBytes(GpuTextureFormat.Bc3, 6, 10, false), bc.Length);

        // The same pixels padded by hand to 8x12 encode to the very same blocks.
        var padded = new byte[8 * 12 * 4];
        for (int y = 0; y < 12; y++)
            for (int x = 0; x < 8; x++)
                src.AsSpan((Math.Min(y, 9) * 6 + Math.Min(x, 5)) * 4, 4).CopyTo(padded.AsSpan((y * 8 + x) * 4, 4));
        var bcPadded = BcEncoder.EncodeChain(GpuTextureFormat.Bc3, padded, 8, 12, mipmaps: false);
        Assert.Equal(bcPadded, bc);
    }

    [Theory]
    [InlineData(GpuTextureFormat.Bc1)]
    [InlineData(GpuTextureFormat.Bc3)]
    public void ImageMagickDdsReader_DecodesTheSameTexels(GpuTextureFormat format)
    {
        // An outside decoder: if the block layout or bit order were non-standard, ImageMagick would
        // read something else than our decoder does.
        const int S = 64;
        bool alpha = format == GpuTextureFormat.Bc3;
        var src = Gradient(S, S, alpha);
        var bc = BcEncoder.EncodeChain(format, src, S, S, mipmaps: false);
        var ours = DecodeLevel(format, bc, S, S);

        using var image = new MagickImage(WrapDds(bc, S, S, format), new MagickReadSettings { Format = MagickFormat.Dds });
        Assert.Equal((uint)S, image.Width);
        var theirs = image.GetPixels().ToByteArray(PixelMapping.RGBA)!;

        int maxDiff = 0;
        for (int i = 0; i < ours.Length; i++)
        {
            if (!alpha && (i & 3) == 3) continue;
            maxDiff = Math.Max(maxDiff, Math.Abs(ours[i] - theirs[i]));
        }
        _out.WriteLine($"{format}: ImageMagick vs reference decoder max difference {maxDiff}");
        Assert.True(maxDiff <= 3, $"max difference {maxDiff}");
        Assert.True(Psnr(src, theirs, 0, alpha ? 3 : 2) > 36);
    }

    [Fact]
    public void MipChain_EachLevelEncodesItsOwnPixels_AndSliceIsTheTail()
    {
        const int S = 64;
        // A chain: level k is a flat colour of value 20*k, so a level mix-up shows at once.
        int levels = BcLayout.LevelCount(S, S, true);
        var chain = new byte[BcLayout.ChainBytes(GpuTextureFormat.Rgba8, S, S, true)];
        int o = 0;
        for (int k = 0; k < levels; k++)
        {
            var (w, h) = BcLayout.LevelSize(S, S, k);
            for (int i = 0; i < w * h; i++)
            {
                chain[o++] = (byte)(20 * k); chain[o++] = (byte)(20 * k); chain[o++] = (byte)(20 * k); chain[o++] = 255;
            }
        }
        var bc = BcEncoder.EncodeChain(GpuTextureFormat.Bc1, chain, S, S, true);
        var data = new CompressedTextureData(GpuTextureFormat.Bc1, S, S, true, bc, S, S, default);

        var sliced = data.Slice(16, 16);
        Assert.NotNull(sliced);
        Assert.Equal(BcLayout.ChainBytes(GpuTextureFormat.Bc1, 16, 16, true), sliced!.Data.Length);
        var top = DecodeLevel(GpuTextureFormat.Bc1, sliced.Data, 16, 16);
        Assert.InRange(top[0], 40 - 2, 40 + 2); // level 2 of a 64 chain is 16x16
        Assert.Null(data.Slice(16, 8));          // no level of that shape
        Assert.Same(data, data.Slice(S, S));
    }

    [Fact]
    public void EncodeCost_Per1024Texture_IsReported()
    {
        const int S = 1024;
        var src = Gradient(S, S, alpha: false);
        var srcA = Gradient(S, S, alpha: true);
        BcEncoder.EncodeChain(GpuTextureFormat.Bc1, src.AsSpan(0, 64 * 64 * 4), 64, 64, false); // warm the tables / JIT

        // Steady state, not the first (tier-0 JIT) run: the best of five.
        double bc1 = double.MaxValue, bc3 = double.MaxValue;
        for (int run = 0; run < 5; run++)
        {
            var sw = Stopwatch.StartNew();
            BcEncoder.EncodeChain(GpuTextureFormat.Bc1, src, S, S, mipmaps: false);
            bc1 = Math.Min(bc1, sw.Elapsed.TotalMilliseconds);
            sw.Restart();
            BcEncoder.EncodeChain(GpuTextureFormat.Bc3, srcA, S, S, mipmaps: false);
            bc3 = Math.Min(bc3, sw.Elapsed.TotalMilliseconds);
        }
        _out.WriteLine($"1024x1024 top level: BC1 {bc1:0.0} ms, BC3 {bc3:0.0} ms (one thread)");
        // A loose ceiling: catches a pathological slowdown, not a slow CI machine.
        Assert.True(bc1 < 5000 && bc3 < 5000);
    }

    private static byte[] WrapDds(byte[] level, int w, int h, GpuTextureFormat format)
    {
        var header = new byte[128];
        void U32(int at, uint v) => BitConverter.TryWriteBytes(header.AsSpan(at, 4), v);
        U32(0, 0x20534444);                 // "DDS "
        U32(4, 124);                        // dwSize
        U32(8, 0x1 | 0x2 | 0x4 | 0x1000 | 0x80000); // CAPS | HEIGHT | WIDTH | PIXELFORMAT | LINEARSIZE
        U32(12, (uint)h);
        U32(16, (uint)w);
        U32(20, (uint)level.Length);
        U32(76, 32);                        // ddspf.dwSize
        U32(80, 0x4);                       // DDPF_FOURCC
        header[84] = (byte)'D'; header[85] = (byte)'X'; header[86] = (byte)'T';
        header[87] = (byte)(format == GpuTextureFormat.Bc1 ? '1' : '5');
        U32(108, 0x1000);                   // DDSCAPS_TEXTURE
        var file = new byte[128 + level.Length];
        header.CopyTo(file, 0);
        level.CopyTo(file, 128);
        return file;
    }
}
