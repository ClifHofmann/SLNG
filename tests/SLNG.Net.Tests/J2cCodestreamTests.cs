using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>
/// Truncation detection from the codestream's own declared tile-part length (ISO/IEC 15444-1
/// A.4.2). The streams here are hand-built headers rather than real assets: only the marker
/// segments are read, so a valid header followed by the right or wrong number of filler bytes
/// reproduces both cases exactly, without vendoring grid content into the repo.
/// </summary>
public class J2cCodestreamTests
{
    /// <summary>Builds SOC + SIZ + SOT with the given Psot, then <paramref name="bodyBytes"/> of
    /// tile data after the SOT segment.</summary>
    private static byte[] BuildStream(uint psot, int bodyBytes, bool includeSoc = true)
    {
        var stream = new List<byte>();
        if (includeSoc) { stream.Add(0xFF); stream.Add(0x4F); }

        // SIZ: length 0x0029 for a single-component image; the contents are never read, only
        // stepped over, so the body is filler of the declared length.
        stream.Add(0xFF); stream.Add(0x51);
        const int sizLength = 0x29;
        stream.Add(sizLength >> 8); stream.Add(sizLength & 0xFF);
        stream.AddRange(Enumerable.Repeat((byte)0, sizLength - 2));

        // SOT: Lsot = 10, Isot = 0, Psot, TPsot = 0, TNsot = 1.
        stream.Add(0xFF); stream.Add(0x90);
        stream.Add(0x00); stream.Add(0x0A);
        stream.Add(0x00); stream.Add(0x00);
        stream.Add((byte)(psot >> 24)); stream.Add((byte)(psot >> 16));
        stream.Add((byte)(psot >> 8)); stream.Add((byte)psot);
        stream.Add(0x00); stream.Add(0x01);

        stream.AddRange(Enumerable.Repeat((byte)0x5A, bodyBytes));
        return stream.ToArray();
    }

    [Fact]
    public void CompleteTilePart_IsNotTruncated()
    {
        // Psot counts from the first byte of the SOT marker: 12 bytes of SOT segment + 500 of
        // tile data, and exactly that much is present.
        var bytes = BuildStream(psot: 512, bodyBytes: 500);

        Assert.False(J2cCodestream.IsTruncated(bytes));
    }

    [Fact]
    public void ShortTilePart_IsTruncated()
    {
        // The real OSGrid case in miniature: the tile-part declares far more than arrived.
        var bytes = BuildStream(psot: 113049, bodyBytes: 500);

        Assert.True(J2cCodestream.IsTruncated(bytes));
    }

    [Fact]
    public void OneByteShort_IsTruncated()
    {
        Assert.True(J2cCodestream.IsTruncated(BuildStream(psot: 512, bodyBytes: 499)));
    }

    [Fact]
    public void TrailingBytesBeyondTheTilePart_AreNotTruncation()
    {
        // More data than the first tile-part declares is normal: further tile-parts, and the EOC.
        Assert.False(J2cCodestream.IsTruncated(BuildStream(psot: 512, bodyBytes: 5000)));
    }

    [Fact]
    public void PsotZero_MeansRunsToTheEnd_AndIsNeverTruncated()
    {
        // A.4.2's "unknown length" encoding. It cannot contradict the bytes in hand, so claiming
        // truncation here would reject complete assets.
        Assert.False(J2cCodestream.IsTruncated(BuildStream(psot: 0, bodyBytes: 10)));
    }

    [Fact]
    public void NonJ2cBody_IsLeftAlone()
    {
        // A JP2-wrapped stream (and anything that is not an image at all) is somebody else's
        // check; this must not claim truncation it cannot prove.
        Assert.False(J2cCodestream.IsTruncated(BuildStream(psot: 113049, bodyBytes: 10, includeSoc: false)));
        Assert.False(J2cCodestream.IsTruncated(new byte[] { 0x9E, 0xE9, 0x65 }));
        Assert.False(J2cCodestream.IsTruncated(Array.Empty<byte>()));
    }

    [Fact]
    public void HeaderCutOffBeforeAnySot_IsNotClaimedEitherWay()
    {
        // SOC + a SIZ whose declared length runs past the end of the buffer: the header itself is
        // incomplete, which IS truncation and is reported as such.
        var bytes = new byte[] { 0xFF, 0x4F, 0xFF, 0x51, 0x00, 0x29, 0x00, 0x00 };

        Assert.True(J2cCodestream.IsTruncated(bytes));
    }
}
