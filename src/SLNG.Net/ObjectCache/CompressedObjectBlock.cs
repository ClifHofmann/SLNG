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
    /// Whether an ObjectUpdateCompressed block cannot be trusted to build the right shape from the cache
    /// (BUG-RENDER-47): no block, a layout the shape cannot be located in, or a path curve of 0, which no
    /// real prim has (the path curves start at LL_PCODE_PATH_LINE = 0x10).
    ///
    /// <para>Deliberately NOT a guess about "unconfigured" shapes. A profile curve of 0 is
    /// LL_PCODE_PROFILE_CIRCLE (llvolume.h:143: cylinders, spheres, tori), a plain uncut prism is a real
    /// prism, and a hole type with no hollow is a valid state; flagging them kept every such object out of
    /// the cache and re-requested it on every visit. The stale chalkboard shapes came from cached
    /// compressed blocks that a later UNCOMPRESSED update had superseded; that update now evicts the
    /// cached block (GridSession.OnRawObjectUpdatePacket), and the cache file version was bumped so
    /// blocks cached before the fix are dropped once.</para>
    /// </summary>
    public static bool IsUntrustedShape(byte[]? data)
    {
        if (data is null) return true;
        if (!CompressedParticleRepair.TryFindVolumeParams(data, out int at)) return true;
        return data[at] == 0;   // path curve
    }
}
