namespace SLNG.Net.ObjectCache;

/// <summary>What the head of an <c>ObjectUpdateCompressed</c> block says about the object.</summary>
internal readonly record struct CompressedObjectHead(uint LocalId, uint Crc, byte PCode);

/// <summary>
/// The fixed head of a compressed object block: <c>FullID(16) LocalID(4) PCode(1) State(1) CRC(4)</c>,
/// little-endian, then the rest of the object. The same layout LibreMetaverse reads in
/// <c>ObjectUpdateCompressedHandler</c> and the simulator writes; the CRC is the one a later
/// <c>ObjectUpdateCached</c> probe will quote.
/// </summary>
internal static class CompressedObjectBlock
{
    private const int LocalIdOffset = 16;
    private const int PCodeOffset = 20;
    private const int CrcOffset = 22;

    /// <summary>The smallest block that has a whole head.</summary>
    public const int HeadLength = 26;

    public static bool TryRead(byte[]? data, out CompressedObjectHead head)
    {
        if (data is null || data.Length < HeadLength)
        {
            head = default;
            return false;
        }

        head = new CompressedObjectHead(
            BitConverter.ToUInt32(data, LocalIdOffset),
            BitConverter.ToUInt32(data, CrcOffset),
            data[PCodeOffset]);
        return true;
    }
}
