namespace SLNG.Assets;

/// <summary>
/// How much of a texture is worth decoding and uploading for the screen area it covers.
///
/// <para>Lives here, engine-agnostic, because BOTH ends of the pipeline need the same answer from
/// the same formula: <c>AssetService</c> uses it to pick a JPEG-2000 decoder reduce level before
/// decoding, and the renderer uses it again on the decoded image to decide the remaining local
/// downsample. Two copies of this arithmetic that disagreed would make a texture decode small and
/// then be treated as full resolution (permanently blurry) or the reverse.</para>
/// </summary>
public static class TextureLod
{
    /// <summary>Never reduce past this. Mirrors <c>SLNG.Net.J2kByteSizeEstimator.MaxDiscardLevel</c>;
    /// duplicated rather than referenced so this file stays free of a Net dependency for a constant.</summary>
    public const int MaxDiscardLevel = 5;

    /// <summary>FEAT-PERF-04: extra discard levels the renderer's VRAM back-pressure adds to every
    /// world-texture upload while the GPU cache is over its budget (raised/lowered with hysteresis
    /// by <c>GpuCache</c>). One level is a 4x cut in texel count. Added only when a real screen area
    /// is known — avatar and bake textures pass <c>screenPixelArea: 0</c> and are exempt by
    /// construction, so a parcel full of scenery never softens faces. Written on the main thread,
    /// read on decode workers.</summary>
    public static volatile int GlobalLodBias;

    /// <summary>
    /// The real viewer's texel-to-screen-pixel criterion: how many halvings of each dimension a
    /// texture can take before it stops carrying more detail than the screen can show. One discard
    /// level is a 4x change in texel COUNT, hence log base 4.
    ///
    /// <para>Returns 0 for an unknown/zero <paramref name="screenPixelArea"/> — "don't reduce" is
    /// always the safe answer, and it is what every caller that has no LOD information passes.</para>
    /// </summary>
    public static int DiscardLevelFor(int width, int height, float screenPixelArea)
    {
        if (screenPixelArea <= 0f || width <= 0 || height <= 0) return 0;
        double texels = (double)width * height;
        int discard = (int)System.Math.Floor(System.Math.Log(texels / System.Math.Max(screenPixelArea, 1f)) / System.Math.Log(4.0));
        // FEAT-PERF-04: fold in the VRAM back-pressure bias. After the screenPixelArea guard above,
        // so a caller with no LOD info (avatar / bake, screenPixelArea 0) is never affected.
        return System.Math.Clamp(discard + GlobalLodBias, 0, MaxDiscardLevel);
    }

    /// <summary>
    /// Converts a discard level into a JPEG-2000 decoder reduce factor.
    ///
    /// <para>MEASURED, not assumed: ImageMagick's <c>jp2:reduce-factor=N</c> divides each dimension
    /// by <b>4^N</b>, not the 2^N the name suggests (verified per-file over 40 real cached assets,
    /// 2026-09-03: 1024 -> 256 at N=1, -> 64 at N=2, -> 16 at N=3). A discard level is one halving,
    /// so one reduce factor covers TWO discard levels and an odd level leaves one halving for the
    /// caller's own resize.</para>
    ///
    /// <para>Decode cost over those same assets: 47.6 ms at N=0, 13.1 ms at N=1, 3.8 ms at N=2.
    /// This is a decoder option applied to a COMPLETE codestream and has nothing to do with the
    /// disabled network-side truncation — see <c>AssetService.FetchAndDecodeTextureAsync</c>.</para>
    /// </summary>
    public static int ReduceFactorFor(int discardLevel) => System.Math.Max(0, discardLevel) / 2;

    /// <summary>The dimension a reduce factor produces, so a caller can tell a correctly reduced
    /// decode from a truncated one.</summary>
    public static int ReducedDimension(int dimension, int reduceFactor)
    {
        if (reduceFactor <= 0) return dimension;
        int divisor = 1 << (2 * reduceFactor);
        return System.Math.Max(1, (dimension + divisor - 1) / divisor);
    }
}
