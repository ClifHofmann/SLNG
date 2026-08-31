using System;
using System.IO;
using System.Linq;
using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>
/// FEAT-AVATAR-01: proves the built-in bake layers end up at the size LibreMetaverse's baker
/// composites at, without logging in.
///
/// <para><b>What went wrong.</b> <c>Baker</c> allocates 1024x1024 for every channel except Eyes,
/// but the resource TGAs shipped with LibreMetaverse are 512x512. <c>DrawLayer</c> walks one flat
/// index over <c>bakeWidth * bakeHeight</c> = 1,048,576 and reads the source array at the same
/// index; against a 262,144-entry source it lays 512 source pixels across each 1024-pixel bake row
/// and then its bounds check quietly stops writing a quarter of the way down. Measured in-world:
/// the head bake came back holding the face twice, sheared, with a diagonal seam across it.</para>
///
/// <para>Only the head bake draws these unconditionally (head_color, head_alpha, head_skingrain,
/// head_hair); upperbody_color and lowerbody_color are drawn only when no skin texture is worn,
/// which is why the upper body composited perfectly while the head did not.</para>
///
/// <para>Sizes are read straight out of the TGA header rather than through
/// <c>Baker.LoadResourceLayer</c>, because that loader caches: it keeps reporting 512x512 after the
/// file on disk has become 4,194,322 bytes, so it cannot witness its own fix.</para>
/// </summary>
public class BakeResourceLayerTests : IDisposable
{
    private readonly string _dir;

    public BakeResourceLayerTests()
    {
        // A private copy of the shipped layers, so the test neither depends on nor disturbs
        // whatever the running client has already rewritten.
        _dir = Path.Combine(Path.GetTempPath(), "slng_bake_res_" + Guid.NewGuid().ToString("N")[..8]);

        var source = Path.Combine(AppContext.BaseDirectory, "linden", "character");
        foreach (var sub in new[] { "character", "static_assets" })
        {
            Directory.CreateDirectory(Path.Combine(_dir, sub));
            foreach (var tga in Directory.EnumerateFiles(source, "*.tga"))
                File.Copy(tga, Path.Combine(_dir, sub, Path.GetFileName(tga)), overwrite: true);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    public static TheoryData<string> Layers()
    {
        var data = new TheoryData<string>();
        foreach (var name in BakeResourceLayers.Layers) data.Add(name);
        return data;
    }

    private (int Width, int Height, int Type, int Depth) Header(string name, string sub = "character")
    {
        var b = File.ReadAllBytes(Path.Combine(_dir, sub, name));
        return (b[12] | (b[13] << 8), b[14] | (b[15] << 8), b[2], b[16]);
    }

    /// <summary>Builds a run-length-encoded TGA, the form the layers actually ship in — a two-pixel
    /// run then two literal pixels, repeated per row, so both packet kinds are exercised.</summary>
    private static byte[] RleTga(int size, byte depth)
    {
        int bpp = depth / 8;
        var body = new System.Collections.Generic.List<byte>();

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x += 4)
            {
                body.Add(0x81);                                             // run of 2
                for (int b = 0; b < bpp; b++) body.Add((byte)(x + b));
                body.Add(0x01);                                             // 2 literals
                for (int i = 0; i < 2; i++)
                    for (int b = 0; b < bpp; b++) body.Add((byte)(y + i + b));
            }
        }

        var tga = new byte[18 + body.Count];
        tga[2] = (byte)(bpp == 1 ? 11 : 10);
        tga[12] = (byte)(size & 0xFF); tga[13] = (byte)(size >> 8);
        tga[14] = (byte)(size & 0xFF); tga[15] = (byte)(size >> 8);
        tga[16] = depth;
        body.CopyTo(tga, 18);
        return tga;
    }

    /// <summary>Scaling works on the compressed form the layers ship in, at both depths, and lands
    /// exactly on the bake size. Uses a synthetic image rather than a shipped file because the
    /// shipped files are themselves rewritten by this fix — asserting on them would only hold until
    /// the first client session.</summary>
    [Theory]
    [InlineData((byte)32)]
    [InlineData((byte)8)]
    public void Upscale_expands_a_run_length_encoded_layer(byte depth)
    {
        var result = BakeResourceLayers.Upscale(RleTga(64, depth), BakeResourceLayers.BakeSize);

        Assert.NotNull(result);
        int bpp = depth / 8;
        Assert.Equal(18 + BakeResourceLayers.BakeSize * BakeResourceLayers.BakeSize * bpp, result!.Length);
        Assert.Equal(BakeResourceLayers.BakeSize, result[12] | (result[13] << 8));
        Assert.Equal(depth, result[16]);
        // Content survives: a decoded gradient has many distinct values, a lost one would be flat.
        Assert.True(result.Skip(18).Distinct().Count() > 8);
    }

    /// <summary>An image already at the bake size reports "nothing to do", which is what keeps the
    /// rewrite idempotent.</summary>
    [Fact]
    public void Upscale_returns_null_when_the_layer_already_matches()
    {
        Assert.Null(BakeResourceLayers.Upscale(
            RleTga(BakeResourceLayers.BakeSize, 32), BakeResourceLayers.BakeSize));
    }

    /// <summary>THE FIX: every layer ends up square at the bake size, so DrawLayer's flat index
    /// addresses the same pixel in source and destination — the assumption it makes and never
    /// checks. Both directory layouts are rewritten, since which one resolves depends on the
    /// LibreMetaverse version.</summary>
    [Theory]
    [MemberData(nameof(Layers))]
    public void Upscaled_layers_are_square_at_the_bake_size(string name)
    {
        BakeResourceLayers.UpscaleAll(_dir);

        foreach (var sub in new[] { "character", "static_assets" })
        {
            var (width, height, _, _) = Header(name, sub);
            Assert.Equal(BakeResourceLayers.BakeSize, width);
            Assert.Equal(BakeResourceLayers.BakeSize, height);
        }
    }

    /// <summary>The rewritten file must stay the same kind of image. An 8-bit alpha layer re-encoded
    /// as 32-bit colour would change the channel set LibreMetaverse infers, and the baker picks its
    /// blend path from exactly that.</summary>
    [Theory]
    [MemberData(nameof(Layers))]
    public void Upscaling_preserves_the_pixel_depth(string name)
    {
        int shippedDepth = Header(name).Depth;

        BakeResourceLayers.UpscaleAll(_dir);

        var (_, _, type, depth) = Header(name);
        Assert.Equal(shippedDepth, depth);
        Assert.Equal(depth == 8 ? 3 : 2, type); // uncompressed grey / true-colour
    }

    /// <summary>A file that loads at the right size but has lost its picture would bake just as
    /// wrong, only less visibly. head_color is mostly transparent with eyes and a mouth, so its
    /// variety of values is what has to survive the scale.</summary>
    [Fact]
    public void Upscaling_preserves_the_layer_content()
    {
        var path = Path.Combine(_dir, "character", "head_color.tga");
        int before = File.ReadAllBytes(path).Skip(18).Distinct().Count();

        BakeResourceLayers.UpscaleAll(_dir);

        var after = File.ReadAllBytes(path);
        Assert.Equal(18 + BakeResourceLayers.BakeSize * BakeResourceLayers.BakeSize * 4, after.Length);
        Assert.True(after.Skip(18).Distinct().Count() >= before / 2,
            $"head_color lost its detail: {after.Skip(18).Distinct().Count()} distinct byte values " +
            $"after upscaling, {before} before");
    }

    /// <summary>Runs on every session construction, so it has to be safe to repeat: a layer already
    /// at the bake size is left byte-for-byte alone.</summary>
    [Fact]
    public void Upscaling_is_idempotent()
    {
        BakeResourceLayers.UpscaleAll(_dir);
        var path = Path.Combine(_dir, "character", "head_color.tga");
        var first = File.ReadAllBytes(path);

        BakeResourceLayers.UpscaleAll(_dir);

        Assert.Equal(first, File.ReadAllBytes(path));
    }

    /// <summary>A corrupt or unfamiliar layer must not take out the session before login — this
    /// runs in the constructor, and a throw there loses the client entirely.</summary>
    [Fact]
    public void A_broken_layer_is_reported_not_thrown()
    {
        File.WriteAllBytes(Path.Combine(_dir, "character", "head_color.tga"), new byte[] { 1, 2, 3 });
        var problems = new System.Collections.Generic.List<string>();

        BakeResourceLayers.UpscaleAll(_dir, problems.Add);

        Assert.Contains(problems, p => p.Contains("head_color.tga"));
        // …and the rest of the set is still processed.
        Assert.Equal(BakeResourceLayers.BakeSize, Header("head_alpha.tga").Width);
    }
}
