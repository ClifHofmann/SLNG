using LibreMetaverse;
using SLNG.Core;
using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>
/// FEAT-ANIMESH-01: the animated-mesh flag inside an <c>ObjectUpdateCompressed</c> object.
///
/// <para>A compressed object is one blob, and the ExtraParams sit in the MIDDLE of it, behind up
/// to five optional sections whose presence is in the compressed-flags word. The blocks here are
/// assembled in the order the simulator writes them (OpenSim
/// <c>LLClientView.CreateCompressedUpdateBlockZC</c>, LLClientView.cs:8038-8135) and the viewer
/// reads them (<c>LLViewerObject::processUpdateMessage</c> OUT_FULL_COMPRESSED,
/// llviewerobject.cpp:1803-1926), with a distinctive filler byte in every optional section so
/// that an offset that misses one reads garbage -- an implausible extra-param count, not a
/// subtly wrong flag.</para>
///
/// <para>Note where the extended particle block (flag 0x400) goes: at the very END of the object,
/// behind the texture entry (llvovolume.cpp:520), not at the legacy position. It therefore does
/// not move the ExtraParams, which is one of the things pinned below.</para>
/// </summary>
public class CompressedExtendedMeshTests
{
    private const ushort Light = 0x20;
    private const ushort ExtendedMesh = 0x70;

    /// <summary>UUID(16) LocalID(4) PCode(1) State(1) CRC(4) Material(1) ClickAction(1) Scale(12)
    /// Position(12) Rotation(12), then flags(4) and owner(16) -- where the optional sections start.</summary>
    private const int SectionsStart = 64 + 4 + 16;

    private const uint HasSound = 0x10;
    private const uint HasNameValues = 0x100;

    internal static byte[] ExtraParams(params (ushort Type, byte[] Payload)[] blocks)
    {
        var bytes = new List<byte> { (byte)blocks.Length };
        foreach (var (type, payload) in blocks)
        {
            bytes.AddRange(BitConverter.GetBytes(type));
            bytes.AddRange(BitConverter.GetBytes(payload.Length));
            bytes.AddRange(payload);
        }
        return bytes.ToArray();
    }

    internal static byte[] AnimeshOn() => ExtraParams((ExtendedMesh, BitConverter.GetBytes(ExtendedMeshParams.FlagAnimatedMesh)));

    /// <summary>Assembles a compressed object, section by section in wire order. Returns the offset
    /// at which the ExtraParams count byte was written through <paramref name="extraParamsAt"/>.</summary>
    internal static byte[] Compressed(
        uint flags, byte[] extraParams, out int extraParamsAt,
        bool withLegacyParticles = false, byte[]? newParticles = null,
        byte[]? textureEntry = null, byte[]? textureAnim = null)
    {
        var data = new List<byte>();
        data.AddRange(new byte[16]);                       // UUID
        data.AddRange(BitConverter.GetBytes(77u));         // LocalID
        data.Add((byte)PCode.Prim);
        data.Add(0);                                       // State
        data.AddRange(new byte[4]);                        // CRC
        data.Add(0);                                       // Material
        data.Add(0);                                       // ClickAction
        data.AddRange(new byte[12 * 3]);                   // Scale, Position, Rotation

        uint all = flags | (withLegacyParticles ? CompressedParticleRepair.HasParticlesLegacy : 0)
            | (newParticles is not null ? CompressedParticleRepair.HasParticlesNew : 0)
            | (textureAnim is not null ? CompressedParticleRepair.HasTextureAnimation : 0);
        data.AddRange(BitConverter.GetBytes(all));
        data.AddRange(new byte[16]);                       // Owner
        Assert.Equal(SectionsStart, data.Count);

        if ((all & CompressedParticleRepair.HasAngularVelocity) != 0) data.AddRange(Enumerable.Repeat((byte)0x11, 12));
        if ((all & CompressedParticleRepair.HasParent) != 0) data.AddRange(Enumerable.Repeat((byte)0x22, 4));
        if ((all & CompressedParticleRepair.IsTree) != 0) data.Add(0x33);
        if ((all & CompressedParticleRepair.HasText) != 0)
        {
            data.AddRange(System.Text.Encoding.ASCII.GetBytes("a hovering label"));
            data.Add(0);
            data.AddRange(Enumerable.Repeat((byte)0x55, 4)); // text colour
        }
        if ((all & CompressedParticleRepair.HasMediaUrl) != 0)
        {
            data.AddRange(System.Text.Encoding.ASCII.GetBytes("http://example.invalid/stream"));
            data.Add(0);
        }
        if (withLegacyParticles) data.AddRange(Enumerable.Repeat((byte)0x66, CompressedParticleRepair.LegacyBlockSize));

        extraParamsAt = data.Count;
        data.AddRange(extraParams);

        // Everything the simulator writes after the extra params. Filler is 0xAB so a scan that
        // runs on past the extra params would find nothing it could mistake for a 0x70 block.
        if ((all & HasSound) != 0) data.AddRange(Enumerable.Repeat((byte)0xAB, 25));
        if ((all & HasNameValues) != 0)
        {
            data.AddRange(System.Text.Encoding.ASCII.GetBytes("AttachItemID STRING RW SV 00000000-0000-0000-0000-000000000000"));
            data.Add(0);
        }
        data.AddRange(Enumerable.Repeat((byte)0xAB, 23));  // path + profile
        // Texture entry and texture animation: each an S32 size then that many bytes
        // (LLPrimitive::unpackTEMessage / LLTextureAnim::unpackTAMessage, both unpackBinaryData).
        data.AddRange(BitConverter.GetBytes(textureEntry?.Length ?? 0));
        if (textureEntry is not null) data.AddRange(textureEntry);
        if (textureAnim is not null)
        {
            data.AddRange(BitConverter.GetBytes(textureAnim.Length));
            data.AddRange(textureAnim);
        }
        if (newParticles is not null) data.AddRange(newParticles);
        return data.ToArray();
    }

    [Fact]
    public void AnimeshRoot_IsDetected()
    {
        byte[] block = Compressed(0, AnimeshOn(), out int at);

        Assert.Equal(SectionsStart, at); // nothing optional: straight after the owner id
        Assert.True(GridSession.CompressedAnimatedMesh(block));
    }

    [Fact]
    public void ExtraParamsLocator_ReturnsTheOffsetOfTheCountByte()
    {
        byte[] block = Compressed(
            CompressedParticleRepair.HasParent | CompressedParticleRepair.HasText, AnimeshOn(), out int at);

        Assert.True(CompressedParticleRepair.TryFindExtraParams(block, out int offset));
        Assert.Equal(at, offset);
        Assert.Equal(1, block[offset]); // the count byte of the one block we wrote
    }

    [Fact]
    public void ObjectWithoutExtraParamsBlock_IsNotAnimesh_ButIsAnAnswer()
    {
        // The sim writes a single 0 count byte when there are no extra params. That is a firm
        // "not animesh" -- distinct from "could not tell", which must leave the latch alone.
        byte[] block = Compressed(0, new byte[] { 0 }, out _);

        Assert.False(GridSession.CompressedAnimatedMesh(block));
    }

    [Fact]
    public void FlagClearBlock_IsNotAnimesh()
    {
        byte[] block = Compressed(0, ExtraParams((ExtendedMesh, BitConverter.GetBytes(0u))), out _);

        Assert.False(GridSession.CompressedAnimatedMesh(block));
    }

    [Fact]
    public void BlockAfterALightBlock_IsFound()
    {
        byte[] block = Compressed(0, ExtraParams(
            (Light, new byte[16]),
            (ExtendedMesh, BitConverter.GetBytes(ExtendedMeshParams.FlagAnimatedMesh))), out _);

        Assert.True(GridSession.CompressedAnimatedMesh(block));
    }

    /// <summary>
    /// Every optional section ahead of the ExtraParams moves them, and a miss does not fail -- it
    /// reads the filler as a count byte. A linkset child (parent id) is the common case: an
    /// animesh in a linkset has a root with no parent, but its rigged CHILDREN do, and they have
    /// to be read right too even though only the root's flag counts.
    /// </summary>
    [Theory]
    [InlineData(CompressedParticleRepair.HasAngularVelocity)]
    [InlineData(CompressedParticleRepair.HasParent)]
    [InlineData(CompressedParticleRepair.IsTree)]
    [InlineData(CompressedParticleRepair.HasText)]
    [InlineData(CompressedParticleRepair.HasMediaUrl)]
    [InlineData(HasSound)]
    [InlineData(HasNameValues)]
    [InlineData(CompressedParticleRepair.HasAngularVelocity | CompressedParticleRepair.HasParent
        | CompressedParticleRepair.HasText | CompressedParticleRepair.HasMediaUrl)]
    public void EveryOptionalSectionAheadOfTheExtraParamsIsAccountedFor(uint optional)
    {
        byte[] block = Compressed(optional, AnimeshOn(), out int at);

        Assert.True(CompressedParticleRepair.TryFindExtraParams(block, out int offset));
        Assert.Equal(at, offset);
        Assert.True(GridSession.CompressedAnimatedMesh(block));
    }

    [Fact]
    public void ALegacyParticleBlockSitsAheadOfTheExtraParams()
    {
        // The 86-byte legacy block is written BEFORE the extra params (OpenSim 8067-8071,
        // viewer 1888-1890), so it has to be skipped -- unlike the extended block below.
        byte[] block = Compressed(CompressedParticleRepair.HasParent, AnimeshOn(), out int at, withLegacyParticles: true);

        Assert.True(CompressedParticleRepair.TryFindExtraParams(block, out int offset));
        Assert.Equal(at, offset);
        Assert.True(GridSession.CompressedAnimatedMesh(block));
    }

    [Fact]
    public void AnExtendedParticleBlockIsAtTheEndAndDoesNotMoveTheExtraParams()
    {
        // 0x400: the block follows the texture entry (llvovolume.cpp:520). Treating it as sitting
        // at the legacy position would skip nothing here (the flag is "new", not "legacy") -- but
        // an implementation that skipped an 86-98 byte block for it would run past the extra
        // params and fail to find the flag. The decoy bytes make a stray read obvious.
        byte[] decoy = Enumerable.Repeat((byte)0x01, 94).ToArray();
        byte[] block = Compressed(CompressedParticleRepair.HasText, AnimeshOn(), out int at, newParticles: decoy);

        Assert.True(CompressedParticleRepair.TryFindExtraParams(block, out int offset));
        Assert.Equal(at, offset);
        Assert.True(GridSession.CompressedAnimatedMesh(block));
    }

    [Fact]
    public void AScratchPadObject_IsUnknown_NotGuessed()
    {
        // The two decoders we have disagree on this section's width: LibreMetaverse (and the
        // particle repair) read a one-byte length, the reference viewer reads a U32 then an
        // S32-prefixed blob (llviewerobject.cpp:1836-1839). Nothing in the wild sends it. A wrong
        // offset is worse than a missing case, so the answer is "could not tell" (null).
        byte[] block = Compressed(CompressedParticleRepair.HasScratchPad, AnimeshOn(), out _);

        Assert.Null(GridSession.CompressedAnimatedMesh(block));
        Assert.False(CompressedParticleRepair.TryFindExtraParams(block, out _));
    }

    [Fact]
    public void AScratchPadFlagBesideTheTreeFlag_FollowsTheTreeBranch()
    {
        // The tree branch wins in both decoders (one byte), so this layout IS well defined.
        byte[] block = Compressed(
            CompressedParticleRepair.IsTree | CompressedParticleRepair.HasScratchPad, AnimeshOn(), out int at);

        Assert.True(CompressedParticleRepair.TryFindExtraParams(block, out int offset));
        Assert.Equal(at, offset);
    }

    [Fact]
    public void ABlockTooShortToHoldItsOwnHeader_IsUnknown()
    {
        Assert.Null(GridSession.CompressedAnimatedMesh(null));
        Assert.Null(GridSession.CompressedAnimatedMesh(Array.Empty<byte>()));
        Assert.Null(GridSession.CompressedAnimatedMesh(new byte[SectionsStart - 1]));
        // Header only: no room for even the count byte.
        Assert.Null(GridSession.CompressedAnimatedMesh(new byte[SectionsStart]));
    }

    [Fact]
    public void ABlockCutOffInsideAnOptionalSection_IsUnknown()
    {
        byte[] full = Compressed(
            CompressedParticleRepair.HasText | CompressedParticleRepair.HasMediaUrl, AnimeshOn(), out int at);

        // Cut in the middle of the floating text, and just before the extra params.
        Assert.Null(GridSession.CompressedAnimatedMesh(full[..(SectionsStart + 5)]));
        Assert.Null(GridSession.CompressedAnimatedMesh(full[..at]));
    }

    [Fact]
    public void ALegacyParticleFlagWithoutTheBytesToHoldIt_IsUnknown()
    {
        byte[] full = Compressed(0, AnimeshOn(), out _, withLegacyParticles: true);

        Assert.Null(GridSession.CompressedAnimatedMesh(full[..(SectionsStart + 40)]));
    }

    [Fact]
    public void ExtraParamsCutOffMidEntry_IsAnAnswer_NotAnimesh()
    {
        // Located fine, but the entry inside is truncated: same verdict as the full-update path
        // gives a lying size -- "not animesh", never garbage.
        byte[] full = Compressed(0, AnimeshOn(), out int at);

        Assert.False(GridSession.CompressedAnimatedMesh(full[..(at + 1 + 2 + 4 + 2)])); // 2 of the 4 flag bytes
    }
}
