namespace SLNG.Net;

/// <summary>
/// Recovers the particle block from an <c>ObjectUpdateCompressed</c> object, which
/// LibreMetaverse decodes incorrectly.
///
/// <para><b>The bug.</b> LibreMetaverse's compressed handler does
/// <c>prim.ParticleSys = new Primitive.ParticleSystem(block.Data, i)</c>
/// (ObjectManager.PacketHandlers.cs:810), handing the constructor the offset of the particle
/// block inside the whole compressed object blob. That constructor takes its block length from
/// <c>data.Length - pos</c> -- so it is told the particle block runs to the end of the object,
/// which it never does: extra params, sound, name values and the texture entry all follow it.
/// The length therefore never matches the 86-byte legacy block, no branch of the constructor
/// runs, and every field stays at its default. The result is a particle system whose CRC is 0
/// and whose every value is zero, which is indistinguishable from an object that has none.</para>
///
/// <para><b>Why it is invisible.</b> The handler still advances its own cursor by the correct 86
/// bytes, so nothing else in the object decodes wrong -- the failure is silent and confined to
/// particles. And the full-update path is fine, because there the block arrives in its own
/// message field (<c>block.PSBlock</c>) whose length is exactly the block's
/// (ObjectManager.PacketHandlers.cs:348). So an emitter works right up until it is streamed as a
/// compressed update, which on OpenSim is how objects arrive when you log in
/// (LLClientView.cs:5263, CreateCompressedUpdateBlockZC) -- while re-running the script sends a
/// full update and makes it work again. That is exactly the "it worked the first time and never
/// again" shape this was found in.</para>
///
/// <para>The offset is recomputed by walking the same layout LibreMetaverse itself walks
/// (ObjectManager.PacketHandlers.cs:674-806). A fixed 84-byte header, then FIVE optional
/// sections -- angular velocity, parent id, tree/scratch pad, floating text, media URL -- each
/// of which moves the particle block by its own width. Missing one does not fail: it shifts the
/// read, and 86 bytes read at the wrong offset decode into a complete and entirely plausible
/// particle system. The first version of this class handled only the media URL, and a child prim
/// in a linkset (parent id, 4 bytes) came out with a 3-second emitter reading as 1.15 s, a burst
/// of 100 as 128, and a texture id shifted by four bytes.</para>
/// </summary>
internal static class CompressedParticleRepair
{
    /// <summary>Object has a legacy (86-byte) particle block. LibreMetaverse calls the same bit
    /// <c>HasParticles</c> (ObjectManager.cs:74); OpenSim calls it <c>HasParticlesLegacy</c>
    /// (LLClientView.cs:7570).</summary>
    internal const uint HasParticlesLegacy = 0x08;

    /// <summary>Object has a media URL, a null-terminated string ahead of the particle block.</summary>
    internal const uint HasMediaUrl = 0x200;

    /// <summary>Sections that sit between the owner id and the particle block. Every one of them
    /// moves the block, and getting any of them wrong shifts the read by its own width -- which
    /// decodes into a complete, plausible, wrong particle system rather than into an error.</summary>
    internal const uint HasScratchPad = 0x01;
    internal const uint IsTree = 0x02;
    internal const uint HasText = 0x04;
    internal const uint HasParent = 0x20;
    internal const uint HasAngularVelocity = 0x80;

    /// <summary>Object has an extended particle block (glow and/or a custom blend function), which
    /// OpenSim sends whenever the system is longer than the legacy 86 bytes
    /// (LLClientView.cs:7645-7649). LibreMetaverse does not handle this flag in the object handler
    /// at all -- so on top of losing the particles it also fails to skip the block, and every
    /// field it decodes after it for that object is read from the wrong offset. Recovering the
    /// particles here does not repair that; it is a separate upstream bug.</summary>
    internal const uint HasParticlesNew = 0x400;

    internal const int LegacyBlockSize = 86;

    /// <summary>Largest block LibreMetaverse's own parser accepts
    /// (<c>Primitive.ParticleSystem.MaxDataBlockSize</c>).</summary>
    internal const int MaxBlockSize = 98;

    /// <summary>Offset of the compressed flags word: UUID(16) + LocalID(4) + PCode(1) + State(1)
    /// + CRC(4) + Material(1) + ClickAction(1) + Scale(12) + Position(12) + Rotation(12).</summary>
    private const int FlagsOffset = 64;

    /// <summary>Offset of the LocalID, immediately after the object's UUID.</summary>
    private const int LocalIdOffset = 16;

    /// <summary>The flags word and the owner UUID that follows it.</summary>
    private const int OwnerIdSize = 16;

    /// <summary>Reads the object's local id out of a compressed object block.</summary>
    internal static bool TryReadLocalId(byte[] data, out uint localId)
    {
        localId = 0;
        if (data.Length < LocalIdOffset + 4)
        {
            return false;
        }

        localId = (uint)(data[LocalIdOffset]
            | (data[LocalIdOffset + 1] << 8)
            | (data[LocalIdOffset + 2] << 16)
            | (data[LocalIdOffset + 3] << 24));
        return true;
    }

    /// <summary>
    /// Returns the object's particle block, or null if it carries none (or the data is too short
    /// to hold what its flags claim).
    /// </summary>
    /// <remarks>
    /// The extended block is self-describing but its length header is BitPack-encoded, so rather
    /// than decode that here the slice is taken up to the parser's own maximum. Trailing bytes are
    /// harmless: the parser reads its fields in order and simply never reaches them. The legacy
    /// block is taken at exactly 86 bytes, because that length is what selects the legacy branch.
    /// </remarks>
    internal static byte[]? ExtractParticleBlock(byte[] data)
    {
        if (data.Length < FlagsOffset + 4 + OwnerIdSize)
        {
            return null;
        }

        uint flags = (uint)(data[FlagsOffset]
            | (data[FlagsOffset + 1] << 8)
            | (data[FlagsOffset + 2] << 16)
            | (data[FlagsOffset + 3] << 24));

        // Nothing below is worth doing for an object that has no particles anyway.
        if ((flags & (HasParticlesLegacy | HasParticlesNew)) == 0)
        {
            return null;
        }

        int offset = FlagsOffset + 4 + OwnerIdSize;

        if ((flags & HasAngularVelocity) != 0)
        {
            offset += 12;
        }

        if ((flags & HasParent) != 0)
        {
            offset += 4;
        }

        // Tree and scratch pad are mutually exclusive in the decoder, in this order.
        if ((flags & IsTree) != 0)
        {
            offset += 1;
        }
        else if ((flags & HasScratchPad) != 0)
        {
            if (offset >= data.Length)
            {
                return null;
            }
            offset += 1 + data[offset];
        }

        if ((flags & HasText) != 0)
        {
            if (!TrySkipString(data, ref offset))
            {
                return null;
            }
            offset += 4; // text colour
        }

        if ((flags & HasMediaUrl) != 0 && !TrySkipString(data, ref offset))
        {
            return null;
        }

        int available = data.Length - offset;
        if (offset < 0 || available <= 0)
        {
            return null;
        }

        if ((flags & HasParticlesLegacy) != 0)
        {
            if (available < LegacyBlockSize)
            {
                return null;
            }
            return data[offset..(offset + LegacyBlockSize)];
        }

        if ((flags & HasParticlesNew) != 0)
        {
            // Must land above the legacy size or the parser takes the wrong branch.
            int length = Math.Min(available, MaxBlockSize);
            if (length <= LegacyBlockSize)
            {
                return null;
            }
            return data[offset..(offset + length)];
        }

        return null;
    }

    /// <summary>Advances past a null-terminated string, terminator included. False if the data
    /// runs out first -- these blocks come off the network and a missing terminator must not walk
    /// off the end.</summary>
    private static bool TrySkipString(byte[] data, ref int offset)
    {
        while (offset < data.Length && data[offset] != 0)
        {
            offset++;
        }

        if (offset >= data.Length)
        {
            return false;
        }

        offset++;
        return true;
    }
}
