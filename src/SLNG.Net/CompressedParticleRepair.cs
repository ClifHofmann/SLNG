using System.Buffers.Binary;

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
///
/// <para><b>The extended block</b> (glow and/or a blend function, compressed flag 0x400) is a
/// different case: it is not ahead of the extra params but the LAST section of the object, behind
/// the texture entry and texture animation, and it is self-delimiting. The first version of this
/// class looked for it at the legacy position -- where the extra params are -- and handed those to
/// the particle parser, which saw a system size that was not 68 and returned an all-default system.
/// See <see cref="TryFindExtendedParticleBlock"/> (BUG-NET-26).</para>
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

    /// <summary>Sections that sit AFTER the extra params and ahead of the extended particle block.
    /// Not part of the walk to the particle block above; the extended block is found by walking on
    /// from the extra params past these (see <see cref="TryFindExtendedParticleBlock"/>).</summary>
    internal const uint HasSound = 0x10;
    internal const uint HasTextureAnimation = 0x40;
    internal const uint HasNameValues = 0x100;

    /// <summary>Object has an extended particle block (glow and/or a custom blend function), which
    /// OpenSim sends whenever the system is longer than the legacy 86 bytes
    /// (LLClientView.cs:7645-7649). Unlike the legacy block it is NOT ahead of the extra params:
    /// it is the very last section of the object, behind the texture entry and the texture
    /// animation (llvovolume.cpp:520-523; OpenSim LLClientView.cs `if (haspsnew)` at the end of
    /// CreateCompressedUpdateBlockZC). LibreMetaverse does not handle the flag at all, but since the
    /// block is last nothing it does decode is displaced by it -- the particles are simply lost.
    /// See <see cref="TryFindExtendedParticleBlock"/>.</summary>
    internal const uint HasParticlesNew = 0x400;

    internal const int LegacyBlockSize = 86;

    /// <summary>Largest block LibreMetaverse's own parser accepts
    /// (<c>Primitive.ParticleSystem.MaxDataBlockSize</c>) and the largest the viewer does
    /// (<c>PS_MAX_DATA_BLOCK_SIZE</c>, llpartdata.cpp:44): both size fields, the 68-byte system, the
    /// 18 bytes of part data, glow and a blend function.</summary>
    internal const int MaxBlockSize = 98;

    /// <summary>The system section of an extended block (<c>PS_SYS_DATA_BLOCK_SIZE</c>,
    /// llpartdata.cpp:43). It is preceded by its own S32 size, which must equal this.</summary>
    private const int SystemDataSize = 68;

    /// <summary>The part data every extended block carries (<c>PS_LEGACY_PART_DATA_BLOCK_SIZE</c>,
    /// llpartdata.cpp:42); glow and a blend function add two bytes each. Preceded by its own S32
    /// size.</summary>
    private const int MinPartDataSize = 18;

    /// <summary>Sound: UUID(16), gain F32(4), flags U8(1), radius F32(4)
    /// (llviewerobject.cpp:1928-1934).</summary>
    private const int SoundSize = 25;

    /// <summary>The shape: 16 path bytes and 7 profile bytes (llvolumemessage.cpp:394-450, 152-190).</summary>
    private const int VolumeParamsSize = 23;

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
    /// to hold what its flags claim, or the layout cannot be established).
    /// </summary>
    /// <remarks>
    /// <para>The bytes come back in the shape <c>new Primitive.ParticleSystem(bytes, 0)</c> parses,
    /// and that constructor decides the layout from the LENGTH of what it is given
    /// (<c>data.Length - pos</c>): exactly 86 selects the legacy layout, 87 to 98 the extended one.
    /// So the legacy block is taken at exactly 86 bytes, and the extended block at exactly its
    /// declared size (94 to 98).</para>
    ///
    /// <para>When both flags are set the extended block wins, as it does in the viewer, which
    /// unpacks the legacy block first and the extended one last into the same source
    /// (llviewerobject.cpp:1888-1891, llvovolume.cpp:520-523). If the extended block cannot be used
    /// the legacy one is still a good description of the emitter, so it is returned instead.</para>
    /// </remarks>
    internal static byte[]? ExtractParticleBlock(byte[] data)
    {
        if (data.Length < FlagsOffset + 4 + OwnerIdSize)
        {
            return null;
        }

        uint flags = ReadFlags(data);

        // Nothing below is worth doing for an object that has no particles anyway.
        if ((flags & (HasParticlesLegacy | HasParticlesNew)) == 0)
        {
            return null;
        }

        if ((flags & HasParticlesNew) != 0
            && TryFindExtendedParticleBlock(data, out int extendedAt, out int extendedLength))
        {
            return data[extendedAt..(extendedAt + extendedLength)];
        }

        if ((flags & HasParticlesLegacy) == 0)
        {
            return null;
        }

        if (!TryWalkToParticleBlock(data, flags, out int offset))
        {
            return null;
        }

        int available = data.Length - offset;
        if (offset < 0 || available < LegacyBlockSize)
        {
            return null;
        }

        return data[offset..(offset + LegacyBlockSize)];
    }

    /// <summary>
    /// Finds the extended particle block (compressed flag 0x400): where it starts and how long it is,
    /// or false when it cannot be located with confidence.
    /// </summary>
    /// <remarks>
    /// <para><b>Where it is.</b> Last in the object. The viewer reads, in order: owner, angular
    /// velocity, parent, tree/scratch pad, text, media URL, the legacy particle block, the extra
    /// params, sound, name values (llviewerobject.cpp:1803-1942), then in
    /// <c>LLVOVolume::processUpdateMessage</c> the volume params, the texture entry, the texture
    /// animation and, last, the extended particle block (llvovolume.cpp:445-523). OpenSim writes the
    /// same order (LLClientView.cs, CreateCompressedUpdateBlockZC: extra params 8072, sound 8078,
    /// name values 8085, shape 8090-8107, texture entry 8109-8120, texture animation 8121-8129,
    /// extended particles 8131-8135). The first version looked for it at the legacy position, where
    /// an object laid out this way has its extra params, and handed those to the particle parser.</para>
    ///
    /// <para><b>Why the start is determinable.</b> Nothing in front of it has to be PARSED, only
    /// stepped over: the extra params end where their entries say
    /// (<see cref="ExtraParamsScan.Length"/>), sound and the shape are fixed width, name values are
    /// null-terminated, and the texture entry and texture animation are each an S32 size followed by
    /// that many bytes (<c>unpackBinaryData</c>, lldatapacker.cpp:292-334).</para>
    ///
    /// <para><b>What delimits it.</b> Itself: an S32 system size (68), the system, an S32 part size
    /// (18, +2 with glow, +2 with a blend function), the part data -- 94 to 98 bytes in all
    /// (llpartdata.cpp:98-152, 280-309). It is also the last thing in the buffer, but the declared
    /// size is what is used; anything after it is not part of it. A size this viewer does not know
    /// (the viewer skips such a block and shows nothing, llpartdata.cpp:286-304) is refused rather
    /// than guessed at.</para>
    ///
    /// <para>False for a scratch-pad object (see <see cref="TryFindExtraParams"/>) and for any
    /// section that runs off the data.</para>
    /// </remarks>
    internal static bool TryFindExtendedParticleBlock(byte[] data, out int offset, out int length)
    {
        offset = 0;
        length = 0;

        if (!TryFindVolumeParams(data, out int at))
        {
            return false;
        }

        at += VolumeParamsSize;
        if (!TrySkipSized(data, ref at))
        {
            return false;
        }

        uint flags = ReadFlags(data);
        if ((flags & HasTextureAnimation) != 0 && !TrySkipSized(data, ref at))
        {
            return false;
        }

        // The block's own header: S32 system size, the system, S32 part size.
        const int HeaderSize = 4 + SystemDataSize + 4;
        if (data.Length - at < HeaderSize
            || BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at, 4)) != SystemDataSize)
        {
            return false;
        }

        int partSize = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at + 4 + SystemDataSize, 4));
        if (partSize < MinPartDataSize || partSize > MaxBlockSize - HeaderSize
            || data.Length - at < HeaderSize + partSize)
        {
            return false;
        }

        offset = at;
        length = HeaderSize + partSize;
        return true;
    }

    /// <summary>Steps over a fixed-width section; false if the data ends inside it.</summary>
    private static bool TryAdvance(byte[] data, ref int offset, int count)
    {
        if (count < 0 || data.Length - offset < count)
        {
            return false;
        }

        offset += count;
        return true;
    }

    /// <summary>Steps over an S32-size-prefixed section (the texture entry, the texture animation).
    /// False for a size that is negative or runs past the data: these come off the network.</summary>
    private static bool TrySkipSized(byte[] data, ref int offset)
    {
        if (data.Length - offset < 4)
        {
            return false;
        }

        int size = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
        if (size < 0 || size > data.Length - offset - 4)
        {
            return false;
        }

        offset += 4 + size;
        return true;
    }

    /// <summary>
    /// Finds where the VolumeParams (the 23-byte shape description: 16 path bytes and 7 profile bytes)
    /// begin inside an ObjectUpdateCompressed block, or false when the layout cannot be established
    /// with confidence.
    /// </summary>
    internal static bool TryFindVolumeParams(byte[]? data, out int offset)
    {
        offset = 0;
        if (data is null || !TryFindExtraParams(data, out int at))
        {
            return false;
        }

        uint flags = ReadFlags(data);

        var extraParams = ExtraParamsScan.Read(data.AsSpan(at));
        if (!extraParams.Complete || extraParams.Length < 1)
        {
            return false;
        }
        at += extraParams.Length;

        if ((flags & HasSound) != 0 && !TryAdvance(data, ref at, SoundSize))
        {
            return false;
        }

        if ((flags & HasNameValues) != 0 && !TrySkipString(data, ref at))
        {
            return false;
        }

        if (data.Length - at < VolumeParamsSize)
        {
            return false;
        }

        offset = at;
        return true;
    }

    /// <summary>
    /// Finds where the ExtraParams begin inside a compressed object -- the offset of their
    /// <c>U8 count</c> byte -- or false when the layout cannot be established with confidence.
    /// </summary>
    /// <remarks>
    /// <para>The ExtraParams sit behind the same optional sections the particle block does, plus the
    /// legacy particle block itself (viewer: llviewerobject.cpp:1888-1903; simulator:
    /// LLClientView.cs:8067-8077), so this reuses the one walk above and adds that block. The
    /// extended particle block (<see cref="HasParticlesNew"/>) is NOT skipped: it is written at
    /// the very end of the object, behind the texture entry (llvovolume.cpp:520), and moves nothing
    /// that precedes it.</para>
    ///
    /// <para>Refuses a scratch-pad object (<see cref="HasScratchPad"/> without
    /// <see cref="IsTree"/>). The two decoders we have disagree on that section's width --
    /// LibreMetaverse and <see cref="ExtractParticleBlock"/> read a one-byte length, the reference
    /// viewer reads a <c>U32</c> followed by an <c>S32</c>-prefixed blob (llviewerobject.cpp:1836-1839)
    /// -- and no simulator we know of sends it. The particle repair keeps its one-byte reading; a
    /// caller that needs a trustworthy offset gets "unknown" rather than a guess.</para>
    /// </remarks>
    internal static bool TryFindExtraParams(byte[]? data, out int offset)
    {
        offset = 0;
        if (data is null || data.Length < FlagsOffset + 4 + OwnerIdSize)
        {
            return false;
        }

        uint flags = ReadFlags(data);
        if ((flags & IsTree) == 0 && (flags & HasScratchPad) != 0)
        {
            return false;
        }

        if (!TryWalkToParticleBlock(data, flags, out int at))
        {
            return false;
        }

        if ((flags & HasParticlesLegacy) != 0)
        {
            at += LegacyBlockSize;
        }

        // The count byte has to exist. (A text section's colour is skipped unchecked by the walk,
        // so a block cut off right there can leave `at` past the end.)
        if (at < 0 || at >= data.Length)
        {
            return false;
        }

        offset = at;
        return true;
    }

    private static uint ReadFlags(byte[] data)
        => (uint)(data[FlagsOffset]
            | (data[FlagsOffset + 1] << 8)
            | (data[FlagsOffset + 2] << 16)
            | (data[FlagsOffset + 3] << 24));

    /// <summary>
    /// Walks the optional sections between the owner id and the particle block, in the decoder's
    /// own order, and returns where the particle block would start. False if a section's terminator
    /// or length runs off the data. The caller still has to check that the offset it gets is inside
    /// the data: the fixed-width skips are not individually bounds-checked.
    /// </summary>
    private static bool TryWalkToParticleBlock(byte[] data, uint flags, out int offset)
    {
        offset = FlagsOffset + 4 + OwnerIdSize;

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
                return false;
            }
            offset += 1 + data[offset];
        }

        if ((flags & HasText) != 0)
        {
            if (!TrySkipString(data, ref offset))
            {
                return false;
            }
            offset += 4; // text colour
        }

        if ((flags & HasMediaUrl) != 0 && !TrySkipString(data, ref offset))
        {
            return false;
        }

        return true;
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
