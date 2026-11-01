namespace SLNG.Net;

/// <summary>FEAT-PERF-29: what one texture Range response delivered.</summary>
/// <param name="Data">The body: the asset's bytes from <paramref name="DataOffset"/> on.</param>
/// <param name="DataOffset">Where <paramref name="Data"/> starts in the asset. 0 for a 200, which carries the
/// whole asset whatever was asked, so it replaces the bytes in hand.</param>
/// <param name="Complete">The asset ends with this body: no further byte exists.</param>
/// <param name="TotalSize">The asset's size when the response said it, else -1.</param>
/// <param name="Gone">The region refused the texture for good (403/401 on HTTP and nothing over UDP).</param>
public readonly record struct TextureRangeResult(byte[] Data, long DataOffset, bool Complete, long TotalSize, bool Gone = false);

/// <summary>
/// FEAT-PERF-29: the byte range a streamed texture asks for, and how a response is read. Pure, so the
/// rules are testable without a server.
/// </summary>
public static class TextureRange
{
    /// <summary>The range to ask for when the first <paramref name="heldBytes"/> of the asset are in hand and the
    /// first <paramref name="wantedBytes"/> are wanted (0 = everything). <c>End</c> is inclusive; null leaves the
    /// range open. A refinement starts one byte early, as the reference viewer does, so the range is always partly
    /// satisfiable and a cache such as Varnish never answers with the whole asset (lltexturefetch.cpp:1428-1441).</summary>
    public static (long Start, long? End) For(int heldBytes, int wantedBytes)
    {
        long start = heldBytes > 0 ? heldBytes - 1 : 0;
        return (start, wantedBytes > 0 ? wantedBytes - 1 : null);
    }

    /// <summary>Reads a response to a Range request for <paramref name="requestStart"/>..<paramref name="requestEnd"/>
    /// (inclusive, null = open). <paramref name="rangeStart"/> and <paramref name="rangeTotal"/> come from the
    /// Content-Range header when there was one. Null for a status that is not an answer to a Range request.</summary>
    public static TextureRangeResult? Interpret(int statusCode, long? rangeStart, long? rangeTotal, long requestStart, long? requestEnd, byte[] body)
    {
        switch (statusCode)
        {
            case 200:
                // The server ignored the Range: this is the whole asset.
                return new TextureRangeResult(body, 0, true, body.Length);

            case 206:
                {
                    long offset = rangeStart ?? requestStart;
                    long total = rangeTotal ?? -1;
                    bool complete = total >= 0
                        ? offset + body.Length >= total
                        // No total: a body shorter than asked is the end of the asset, and so is any answer to an open range.
                        : requestEnd is not long end || offset + body.Length < end + 1;
                    return new TextureRangeResult(body, offset, complete, total);
                }

            case 416:
                // Nothing at or after the start: the bytes in hand were the whole asset.
                return new TextureRangeResult(Array.Empty<byte>(), requestStart, true, rangeTotal ?? requestStart);

            default:
                return null;
        }
    }
}
