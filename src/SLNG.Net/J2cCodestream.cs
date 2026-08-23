namespace SLNG.Net;

/// <summary>
/// Minimal JPEG2000 codestream header reader — just enough to answer "did all of this asset
/// actually arrive?" without decoding a single pixel.
///
/// <para>This exists because none of the transport-level truncation checks can answer that
/// question reliably. Content-Length only helps when the server sends it and does not lie; an
/// EOC marker is routinely absent from perfectly good SL assets (see
/// GridSession.FetchTextureViaHttpRangeAsync, where rejecting on a missing EOC blanked 14
/// textures that Firestorm drew fine); and a short body with a matching Content-Length looks
/// identical to a small asset. The codestream, though, states its own length: every tile-part
/// begins with an SOT marker segment carrying <c>Psot</c>, the byte count from the first byte of
/// that SOT to the last byte of the tile-part (ISO/IEC 15444-1, A.4.2). If the bytes in hand end
/// before the offset Psot points at, data is missing, whatever the HTTP layer claimed.</para>
///
/// <para>Measured case that motivated this: an OSGrid sculpt map (512x512, 4 components) arrived
/// as a 33,600-byte body whose single tile-part declares Psot = 113,049. Both decoders produced a
/// gap-filled ("degraded") image, which for a sculpt map means every vertex position is guessed,
/// so the renderer refused it and drew a placeholder solid — the rock rendered as a flat disc.
/// The HTTP layer had reported success and the retry logic re-asked HTTP for the same bytes.</para>
/// </summary>
public static class J2cCodestream
{
    private const byte MarkerPrefix = 0xFF;
    private const byte Soc = 0x4F;   // start of codestream
    private const byte Siz = 0x51;   // image and tile size
    private const byte Sot = 0x90;   // start of tile-part

    /// <summary>
    /// True when <paramref name="bytes"/> is a raw J2C codestream whose first tile-part declares
    /// more data than is present.
    ///
    /// <para>Deliberately conservative — it returns false for everything it cannot prove:</para>
    /// <list type="bullet">
    /// <item>not a raw J2C (no SOC): JP2-wrapped streams box the codestream up differently, and a
    /// non-image body is somebody else's error to report.</item>
    /// <item>no SOT found in the header: nothing declared its length, so nothing is contradicted.</item>
    /// <item><c>Psot == 0</c>: the spec's "this tile-part runs to the EOC or the next SOT"
    /// encoding (A.4.2), which by definition cannot overrun.</item>
    /// </list>
    /// <para>Being wrong in this direction costs nothing; being wrong the other way would reject
    /// a complete asset and send a working texture down the slower fallback path.</para>
    /// </summary>
    public static bool IsTruncated(byte[] bytes)
    {
        if (bytes.Length < 4) return false;
        if (bytes[0] != MarkerPrefix || bytes[1] != Soc) return false;

        // Walk the main header's marker segments. Every marker after SOC is FF xx followed by a
        // 2-byte segment length that includes those two length bytes themselves -- except SOC
        // and the delimiting markers, none of which appear before the first SOT.
        int pos = 2;
        while (pos + 3 < bytes.Length)
        {
            if (bytes[pos] != MarkerPrefix) return false;   // not a marker where one must be: give up rather than guess

            byte marker = bytes[pos + 1];
            int segmentLength = (bytes[pos + 2] << 8) | bytes[pos + 3];
            if (segmentLength < 2) return false;

            if (marker == Sot)
            {
                // SOT: Lsot(2) Isot(2) Psot(4) TPsot(1) TNsot(1). Psot is measured from the first
                // byte of the SOT MARKER, i.e. from pos.
                if (pos + 9 >= bytes.Length) return false;
                long psot = ((long)bytes[pos + 6] << 24) | ((long)bytes[pos + 7] << 16)
                          | ((long)bytes[pos + 8] << 8) | bytes[pos + 9];
                if (psot == 0) return false;
                return pos + psot > bytes.Length;
            }

            // SIZ is not needed to answer the question, but its presence is what makes this a
            // well-formed main header; anything before the first SOT is skipped uniformly.
            if (marker == Siz && pos + 3 + segmentLength > bytes.Length) return true;

            pos += 2 + segmentLength;
        }

        return false;
    }
}
