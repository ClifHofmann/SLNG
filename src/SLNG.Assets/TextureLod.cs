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
    /// Converts a discard level into a JPEG-2000 decoder resolution step — how many halvings of
    /// each dimension to leave out of the reconstruction.
    /// </summary>
    /// <remarks>
    /// One discard level is one halving, and one J2K resolution level is one halving too, so this
    /// is the identity. It stays a named function because the two things are different ideas that
    /// happen to share a number, and because this is where the last mapping went wrong.
    ///
    /// <para><b>It used to divide by two</b>, on the measured belief that ImageMagick's
    /// <c>jp2:reduce-factor=N</c> scaled by 4^N so one factor covered two discard levels. The
    /// dimensions did come out that way — but only because that define returns the top-left
    /// <c>1/2^N</c> CROP downscaled by 2^N, never the whole image (BUG-RENDER-36: vendor panels
    /// showed a zoomed close-up until you flew near enough to force a full re-decode). The
    /// decoder is now CoreJ2K's real resolution-level reconstruction, where one step is one
    /// halving and the picture stays whole.</para>
    /// </remarks>
    public static int ResolutionStepsFor(int discardLevel) => System.Math.Max(0, discardLevel);

    /// <summary>The dimension a given number of halvings produces, so a caller can tell a
    /// correctly reduced decode from a truncated one.</summary>
    public static int ReducedDimension(int dimension, int steps)
    {
        if (steps <= 0) return dimension;
        return System.Math.Max(1, dimension >> steps);
    }
}
