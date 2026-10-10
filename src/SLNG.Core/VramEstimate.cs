namespace SLNG.Core;

/// <summary>
/// FEAT-PERF-25: what the render buffers outside the texture cache cost, estimated from their sizes.
///
/// <para>Godot reports only totals (texture, buffer and video memory). To say WHERE the non-cache part
/// of a small card's budget goes, the one-shot <c>[VramBreakdown]</c> line puts these estimates next
/// to the measured totals; the difference is printed as "unexplained" rather than hidden. The per-pixel
/// figures follow the formats Godot 4.7's Forward+ renderer allocates (RenderSceneBuffersRD, the SSAO /
/// SSIL / SSR / glow effects, LightStorage's atlases) but ignore alignment and the allocator's own
/// blocks -- they are an order-of-magnitude guide, labelled "est." wherever they are printed.</para>
/// </summary>
public static class VramEstimate
{
    // Resolved buffers every 3D viewport has: colour RGBA16F (8), depth (4), normal-roughness RGBA8 (4),
    // plus the back-buffer / depth copies the screen-reading shaders and effects use (~4).
    private const double BaseBytesPerPixel = 20;

    // Per extra MSAA sample: colour (8) + depth (4) + normal-roughness (4).
    private const double MsaaBytesPerSample = 16;

    // Effects, per FULL-resolution pixel, at the resolution Godot renders them.
    private const double SsaoHalfBytesPerPixel = 3;     // quarter-area AO + importance + depth mips
    private const double SsaoFullBytesPerPixel = 12;
    private const double SsilHalfBytesPerPixel = 7;     // quarter-area RGBA16F x3 (+ history, edges)
    private const double SsilFullBytesPerPixel = 28;
    private const double SsrBytesPerPixel = 12;         // RGBA16F + blur mips
    private const double GlowBytesPerPixel = 6;         // half-area RGBA16F mip chain, twice (blur)

    /// <summary>Samples per pixel for a Viewport.Msaa value (0 off, 1 2x, 2 4x, 3 8x).</summary>
    public static int MsaaSamples(int msaa) => msaa switch
    {
        <= 0 => 1,
        1 => 2,
        2 => 4,
        _ => 8,
    };

    /// <summary>Render buffers of one 3D viewport of <paramref name="width"/> x <paramref name="height"/>
    /// rendered pixels.</summary>
    public static long RenderBuffers(
        int width, int height, int msaa, bool ssao, bool ssaoHalf, bool ssil, bool ssilHalf, bool ssr, bool glow)
    {
        if (width <= 0 || height <= 0) return 0;
        double pixels = (double)width * height;
        int samples = MsaaSamples(msaa);
        double perPixel = BaseBytesPerPixel
            + (samples > 1 ? samples * MsaaBytesPerSample : 0)
            + (ssao ? (ssaoHalf ? SsaoHalfBytesPerPixel : SsaoFullBytesPerPixel) : 0)
            + (ssil ? (ssilHalf ? SsilHalfBytesPerPixel : SsilFullBytesPerPixel) : 0)
            + (ssr ? SsrBytesPerPixel : 0)
            + (glow ? GlowBytesPerPixel : 0);
        return (long)(pixels * perPixel);
    }

    /// <summary>The MSAA share of <see cref="RenderBuffers"/> alone.</summary>
    public static long MsaaBuffers(int width, int height, int msaa)
    {
        int samples = MsaaSamples(msaa);
        if (width <= 0 || height <= 0 || samples <= 1) return 0;
        return (long)((double)width * height * samples * MsaaBytesPerSample);
    }

    /// <summary>A square depth-only shadow atlas.</summary>
    public static long ShadowAtlas(int size, bool sixteenBits = true)
        => size <= 0 ? 0 : (long)size * size * (sixteenBits ? 2 : 4);

    /// <summary>The scenario's reflection atlas (Godot 4.7 LightStorage): an octahedral RGBA16F array of
    /// side 2 x size + 2 x padding with a mip chain, one layer per probe slot, plus a cube colour buffer
    /// and a depth buffer at the probe resolution.</summary>
    public static long ReflectionAtlas(int size, int count, int mipmaps = 8)
    {
        if (size <= 0 || count <= 0) return 0;
        long padding = 1L << System.Math.Max(0, mipmaps - 1);
        long side = 2L * size + 2 * padding;
        long octahedral = side * side * count * 8 * 4 / 3;
        long cube = 6L * size * size * 8;
        long depth = (long)size * size * 4;
        return octahedral + cube + depth;
    }
}
