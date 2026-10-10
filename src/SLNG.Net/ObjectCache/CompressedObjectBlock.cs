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

    private const int PositionOffset = 40; // after Material(1) ClickAction(1) Scale(12)
    private const int PositionEnd = PositionOffset + 12;

    /// <summary>Where the object sits, region-local, for ordering and nothing else: for a child prim
    /// this is an offset from its parent, which is close enough to sort by.</summary>
    public static bool TryReadPosition(byte[]? data, out float x, out float y, out float z)
    {
        if (data is null || data.Length < PositionEnd)
        {
            x = y = z = 0f;
            return false;
        }
        x = BitConverter.ToSingle(data, PositionOffset);
        y = BitConverter.ToSingle(data, PositionOffset + 4);
        z = BitConverter.ToSingle(data, PositionOffset + 8);
        return true;
    }

    private const int FlagsOffset = 64; // after Rotation(12)
    private const uint HasParent = 0x20;
    private const uint HasNameValues = 0x100;

    /// <summary>Something an avatar wears: a child prim that carries name-values, which is how the
    /// simulator sends an attachment and how LibreMetaverse tells one. It belongs to the avatar, not
    /// to the region; replayed without its avatar it would be an orphan.</summary>
    public static bool IsAttachment(byte[]? data)
    {
        if (data is null || data.Length < FlagsOffset + 4) return false;
        uint flags = BitConverter.ToUInt32(data, FlagsOffset);
        return (flags & HasNameValues) != 0 && (flags & HasParent) != 0;
    }

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

    /// <summary>
    /// Checks whether an ObjectUpdateCompressed block contains an unconfigured, incomplete, or
    /// untrusted shape (BUG-RENDER-47).
    ///
    /// <para>Newly rezzed prims or scripted displays (like FURWARE/XyzzyText chalkboards) begin as
    /// default uncut equilateral triangles (ProfileCurve 0x03 or 0x23, Cut 0..1, Hollow 0) before
    /// a script or edit configures their real shape. If such an early state was captured in the
    /// cache, it would render as grey sawtooth triangles until manually selected. Similarly,
    /// an all-zero shape or a non-zero hole type with 0 hollow is unconfigured.</para>
    /// </summary>
    public static bool IsUntrustedShape(byte[]? data)
    {
        if (data is null) return true;
        if (!CompressedParticleRepair.TryFindVolumeParams(data, out int at))
        {
            return true;
        }

        byte pathCurve = data[at];
        if (pathCurve == 0) return true;

        byte profileCurve = data[at + 16];
        byte baseProfile = (byte)(profileCurve & 0x0F);
        if (baseProfile == 0) return true;

        ushort profileBegin = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 17, 2));
        ushort profileEnd = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 19, 2));
        ushort profileHollow = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 21, 2));

        byte holeType = (byte)(profileCurve & 0xF0);

        // A declared hole type (Square=0x20, Circle=0x10, Triangle=0x30) with 0 hollow is unconfigured
        if (holeType != 0 && profileHollow == 0) return true;

        // Default uncut, unhollowed equilateral triangle (0x03)
        if (baseProfile == 3)
        {
            bool isDefaultCut = profileBegin == 0 && (profileEnd == 0 || profileEnd >= 49500);
            if (isDefaultCut && profileHollow == 0)
            {
                return true;
            }
        }

        return false;
    }
}
