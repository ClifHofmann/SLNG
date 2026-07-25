namespace SLNG.Net;

/// <summary>
/// Estimates how many leading bytes of a JPEG2000 (J2C) codestream are needed to reach a given
/// SL/OpenSim discard level (0 = full resolution, 5 = coarsest), so <see cref="GridSession"/> can
/// request only that many bytes via an HTTP Range fetch instead of downloading the whole asset.
///
/// Ported directly from the real Second Life viewer's <c>LLImageJ2C::calcDataSizeJ2C</c>
/// (vendored at <c>scratch/slviewer/indra/llimage/llimagej2c.cpp:269-300</c>, constants from
/// <c>llimage.h</c>/<c>llimagej2c.h</c>) -- verified against source as part of FEAT-PERF-02 Phase
/// 2.1, not guessed. It is a deliberate over-estimate ("an efficient approximation, as the true
/// discard level boundary would be in general too big for fast fetching" -- original comment),
/// which is exactly what we want for a Range request: asking for a few KB more than the true
/// layer boundary is harmless (the server just returns what's available), asking for too few
/// bytes would truncate mid-layer and produce a worse decode than the requested discard level.
/// </summary>
public static class J2kByteSizeEstimator
{
    /// <summary>SL/OpenSim JPEG2000 discard levels run 0 (full resolution) .. 5 (coarsest).</summary>
    public const int MaxDiscardLevel = 5;

    private const int MaxBlockSize = 64;
    private const int MinLayerSize = 2000;
    private const int FirstPacketSize = 600; // header-size estimate (must be > MinLayerSize threshold in the original)
    private const float DefaultCompressionRate = 1f / 8f;
    private const int Precision = 8; // assumed bits per component channel
    private const int MaxComponents = 4; // three color channels + alpha

    /// <summary>
    /// Estimated byte count needed to fetch <paramref name="discardLevel"/> worth of quality
    /// layers, including the header. <paramref name="width"/>/<paramref name="height"/> may be 0
    /// (unknown -- e.g. before the texture's own header has ever been read), in which case this
    /// falls back to the same conservative worst-case assumption the reference implementation
    /// uses (2048x2048) rather than under-requesting.
    /// </summary>
    public static int CalcDataSizeJ2C(int width, int height, int discardLevel, float rate = DefaultCompressionRate)
    {
        int w = width > 0 ? width : 2048;
        int h = height > 0 ? height : 2048;
        int maxDimension = System.Math.Max(w, h);

        int blockArea = MaxBlockSize * MaxBlockSize;
        int maxLayers = (int)System.Math.Max(
            System.Math.Round(System.Math.Log2(maxDimension) - System.Math.Log2(MaxBlockSize)), 4);
        blockArea *= System.Math.Max(maxLayers, 1);

        int totalBytes = MinLayerSize * MaxComponents * Precision;
        int blockLayers = 0;
        while (blockLayers <= maxLayers)
        {
            if (blockLayers <= (MaxDiscardLevel - discardLevel))
            {
                totalBytes += (int)(blockArea * MaxComponents * Precision * rate);
            }
            blockLayers++;
            blockArea *= 4;
        }

        totalBytes /= 8; // bits -> bytes
        totalBytes += FirstPacketSize; // header
        return totalBytes;
    }
}
