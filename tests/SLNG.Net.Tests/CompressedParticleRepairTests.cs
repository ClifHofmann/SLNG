using LibreMetaverse;
using SLNG.Core.Components;
using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>
/// Recovery of the particle block from an <c>ObjectUpdateCompressed</c> object.
///
/// <para>The blocks here are assembled byte by byte to the layout LibreMetaverse itself walks
/// (ObjectManager.PacketHandlers.cs:674-806), because that layout is the whole content of the
/// fix: everything ahead of the particle block is fixed-size except an optional media URL, and
/// getting that offset wrong is the difference between a particle system and 86 bytes of
/// someone else's data.</para>
/// </summary>
public class CompressedParticleRepairTests
{
    private const float Tol = 0.02f;

    /// <summary>Fixed-size prefix: UUID(16) LocalID(4) PCode(1) State(1) CRC(4) Material(1)
    /// ClickAction(1) Scale(12) Position(12) Rotation(12) = 64 bytes, then flags(4), owner(16).</summary>
    private const int PrefixSize = 64;

    private static byte[] LegacyFountainBlock()
    {
        var sys = new Primitive.ParticleSystem
        {
            CRC = 1,
            PartFlags = (uint)Primitive.ParticleSystem.ParticleFlags.UseNewAngle,
            Pattern = Primitive.ParticleSystem.SourcePattern.Explode,
            MaxAge = 0f,
            BurstRate = 0.1f,
            BurstRadius = 0.5f,
            BurstSpeedMin = 1f,
            BurstSpeedMax = 3f,
            BurstPartCount = 10,
            PartDataFlags = Primitive.ParticleSystem.ParticleDataFlags.InterpColor
                | Primitive.ParticleSystem.ParticleDataFlags.Emissive,
            PartMaxAge = 3f,
            PartStartColor = new Color4(1f, 0.5f, 0f, 1f),
            PartEndColor = new Color4(1f, 0f, 0f, 1f),
            PartStartScaleX = 0.2f,
            PartStartScaleY = 0.2f,
            PartEndScaleX = 1f,
            PartEndScaleY = 1f,
            BlendFuncSource = (byte)Primitive.ParticleSystem.BlendFunc.SourceAlpha,
            BlendFuncDest = (byte)Primitive.ParticleSystem.BlendFunc.OneMinusSourceAlpha,
        };

        byte[] bytes = sys.GetBytes();
        Assert.Equal(CompressedParticleRepair.LegacyBlockSize, bytes.Length);
        return bytes;
    }

    /// <summary>
    /// Assembles a compressed object block. <paramref name="trailing"/> stands for everything the
    /// simulator packs after the particles -- extra params, sound, name values, the texture entry
    /// -- which is exactly what LibreMetaverse mistakes for part of the particle block.
    /// </summary>
    /// <remarks>
    /// The optional sections are emitted in the decoder's own order, and each one is filled with
    /// a distinctive byte so a slice taken at the wrong offset comes back visibly wrong rather
    /// than subtly wrong.
    /// </remarks>
    private static byte[] CompressedBlock(
        uint localId, uint flags, byte[]? particles,
        string? mediaUrl = null, string? text = null, byte scratchPadSize = 0, int trailing = 40)
    {
        var data = new List<byte>();
        data.AddRange(new byte[16]);                                   // UUID
        data.AddRange(BitConverter.GetBytes(localId));                 // LocalID, little endian
        data.Add((byte)PCode.Prim);
        data.Add(0);                                                   // State
        data.AddRange(new byte[4]);                                    // CRC
        data.Add(0);                                                   // Material
        data.Add(0);                                                   // ClickAction
        data.AddRange(new byte[12]);                                   // Scale
        data.AddRange(new byte[12]);                                   // Position
        data.AddRange(new byte[12]);                                   // Rotation
        Assert.Equal(PrefixSize, data.Count);

        data.AddRange(BitConverter.GetBytes(flags));
        data.AddRange(new byte[16]);                                   // OwnerID

        if ((flags & CompressedParticleRepair.HasAngularVelocity) != 0)
        {
            data.AddRange(Enumerable.Repeat((byte)0x11, 12));
        }

        if ((flags & CompressedParticleRepair.HasParent) != 0)
        {
            data.AddRange(Enumerable.Repeat((byte)0x22, 4));
        }

        if ((flags & CompressedParticleRepair.IsTree) != 0)
        {
            data.Add(0x33);
        }
        else if ((flags & CompressedParticleRepair.HasScratchPad) != 0)
        {
            data.Add(scratchPadSize);
            data.AddRange(Enumerable.Repeat((byte)0x44, scratchPadSize));
        }

        if ((flags & CompressedParticleRepair.HasText) != 0)
        {
            data.AddRange(System.Text.Encoding.ASCII.GetBytes(text ?? "hover text"));
            data.Add(0);
            data.AddRange(Enumerable.Repeat((byte)0x55, 4));           // text colour
        }

        if ((flags & CompressedParticleRepair.HasMediaUrl) != 0)
        {
            data.AddRange(System.Text.Encoding.ASCII.GetBytes(mediaUrl ?? "http://example.invalid/"));
            data.Add(0);
        }

        if (particles is not null)
        {
            data.AddRange(particles);
        }

        // Distinctive filler, so a slice that runs past the block is obvious rather than plausible.
        data.AddRange(Enumerable.Repeat((byte)0xAB, trailing));
        return data.ToArray();
    }

    [Fact]
    public void Reads_the_local_id()
    {
        byte[] block = CompressedBlock(17027153, CompressedParticleRepair.HasParticlesLegacy, LegacyFountainBlock());

        Assert.True(CompressedParticleRepair.TryReadLocalId(block, out uint localId));
        Assert.Equal(17027153u, localId);
    }

    [Fact]
    public void Extracts_exactly_the_legacy_block()
    {
        byte[] particles = LegacyFountainBlock();
        byte[] block = CompressedBlock(1, CompressedParticleRepair.HasParticlesLegacy, particles);

        byte[]? extracted = CompressedParticleRepair.ExtractParticleBlock(block);

        Assert.NotNull(extracted);
        Assert.Equal(particles, extracted);
    }

    /// <summary>
    /// This is the bug, pinned. LibreMetaverse passes the offset into the whole object blob and
    /// lets the parser take its length from what remains (ObjectManager.PacketHandlers.cs:810),
    /// so the length never matches the 86-byte block, no branch runs, and every field stays at
    /// its default -- a particle system indistinguishable from having none. If LibreMetaverse
    /// ever fixes this, this assertion is what will say so.
    /// </summary>
    [Fact]
    public void The_library_decode_of_the_same_block_yields_nothing()
    {
        byte[] block = CompressedBlock(1, CompressedParticleRepair.HasParticlesLegacy, LegacyFountainBlock());
        int particleOffset = PrefixSize + 4 + 16;

        var asLibraryDecodesIt = new Primitive.ParticleSystem(block, particleOffset);

        Assert.Equal(0u, asLibraryDecodesIt.CRC);
        Assert.Equal(0f, asLibraryDecodesIt.PartMaxAge);
        Assert.Equal(Primitive.ParticleSystem.SourcePattern.None, asLibraryDecodesIt.Pattern);
        Assert.Null(ParticleSystemConverter.FromWire(asLibraryDecodesIt));
    }

    [Fact]
    public void The_extracted_block_converts_to_a_usable_system()
    {
        byte[] block = CompressedBlock(1, CompressedParticleRepair.HasParticlesLegacy, LegacyFountainBlock());

        byte[] extracted = CompressedParticleRepair.ExtractParticleBlock(block)!;
        ParticleSystemData? data = ParticleSystemConverter.FromWire(new Primitive.ParticleSystem(extracted, 0));

        Assert.NotNull(data);
        Assert.Equal(SlParticlePattern.Explode, data!.Pattern);
        Assert.Equal(3f, data.PartMaxAge, Tol);
        Assert.Equal(10, data.BurstPartCount);
        Assert.Equal(
            SlParticleDataFlags.InterpColor | SlParticleDataFlags.Emissive,
            data.PartDataFlags);
        Assert.False(data.IsInert);
    }

    /// <summary>
    /// Five optional sections sit between the owner id and the particle block, and every one of
    /// them moves it. This is the case that matters most, because a missed section does not fail
    /// -- it shifts the read, and 86 bytes taken at the wrong offset still decode into a complete,
    /// plausible particle system. A real linkset child (parent id, four bytes) reported a
    /// 3-second emitter as 1.15 s and its texture id shifted by four bytes.
    /// </summary>
    [Theory]
    [InlineData(CompressedParticleRepair.HasAngularVelocity)]
    [InlineData(CompressedParticleRepair.HasParent)]
    [InlineData(CompressedParticleRepair.IsTree)]
    [InlineData(CompressedParticleRepair.HasScratchPad)]
    [InlineData(CompressedParticleRepair.HasText)]
    [InlineData(CompressedParticleRepair.HasMediaUrl)]
    // Tree wins over scratch pad in the decoder, so both bits together must still skip one byte.
    [InlineData(CompressedParticleRepair.IsTree | CompressedParticleRepair.HasScratchPad)]
    // Everything at once, in the decoder's own order.
    [InlineData(CompressedParticleRepair.HasAngularVelocity | CompressedParticleRepair.HasParent
        | CompressedParticleRepair.HasScratchPad | CompressedParticleRepair.HasText
        | CompressedParticleRepair.HasMediaUrl)]
    public void Every_optional_section_moves_the_block_and_is_accounted_for(uint extraFlags)
    {
        byte[] particles = LegacyFountainBlock();
        byte[] block = CompressedBlock(
            1,
            CompressedParticleRepair.HasParticlesLegacy | extraFlags,
            particles,
            mediaUrl: "http://example.invalid/stream",
            text: "a hovering label",
            scratchPadSize: 7);

        byte[]? extracted = CompressedParticleRepair.ExtractParticleBlock(block);

        Assert.NotNull(extracted);
        Assert.Equal(particles, extracted);
    }

    /// <summary>
    /// The shift that was actually shipped: a child prim in a linkset, read four bytes early.
    /// It produced no error at all -- just a different, entirely believable particle system.
    /// </summary>
    [Fact]
    public void A_four_byte_shift_decodes_into_a_plausible_wrong_system_rather_than_failing()
    {
        byte[] block = CompressedBlock(
            1,
            CompressedParticleRepair.HasParticlesLegacy | CompressedParticleRepair.HasParent,
            LegacyFountainBlock());
        int shiftedOffset = PrefixSize + 4 + 16; // the parent id skipped

        var shifted = new Primitive.ParticleSystem(
            block[shiftedOffset..(shiftedOffset + CompressedParticleRepair.LegacyBlockSize)], 0);
        ParticleSystemData? wrong = ParticleSystemConverter.FromWire(shifted);

        // It converts. It is not inert. It is simply not what the script asked for -- which is
        // why only a byte-for-byte comparison against the source block can catch this class of bug.
        Assert.NotNull(wrong);
        Assert.NotEqual(3f, wrong!.PartMaxAge, Tol);

        byte[] correct = CompressedParticleRepair.ExtractParticleBlock(block)!;
        Assert.Equal(3f, ParticleSystemConverter.FromWire(new Primitive.ParticleSystem(correct, 0))!.PartMaxAge, Tol);
    }

    [Fact]
    public void An_object_without_particles_yields_null()
    {
        byte[] block = CompressedBlock(1, flags: 0, particles: null);

        Assert.Null(CompressedParticleRepair.ExtractParticleBlock(block));
    }

    /// <summary>
    /// A block whose flags promise particles it is too short to hold must be refused rather than
    /// read past the end -- these arrive from the network.
    /// </summary>
    [Fact]
    public void A_truncated_block_yields_null_instead_of_reading_past_the_end()
    {
        byte[] full = CompressedBlock(1, CompressedParticleRepair.HasParticlesLegacy, LegacyFountainBlock());
        byte[] truncated = full[..(PrefixSize + 4 + 16 + 20)];

        Assert.Null(CompressedParticleRepair.ExtractParticleBlock(truncated));
        Assert.Null(CompressedParticleRepair.ExtractParticleBlock(Array.Empty<byte>()));
        Assert.Null(CompressedParticleRepair.ExtractParticleBlock(new byte[PrefixSize]));
    }

    /// <summary>
    /// The extended block (glow or a custom blend function) has to come back longer than the
    /// legacy size, or LibreMetaverse's parser takes the legacy branch and misreads it.
    /// </summary>
    [Fact]
    public void An_extended_block_comes_back_above_the_legacy_size()
    {
        byte[] block = CompressedBlock(
            1, CompressedParticleRepair.HasParticlesNew, new byte[94], trailing: 40);

        byte[]? extracted = CompressedParticleRepair.ExtractParticleBlock(block);

        Assert.NotNull(extracted);
        Assert.InRange(extracted!.Length, CompressedParticleRepair.LegacyBlockSize + 1, CompressedParticleRepair.MaxBlockSize);
    }
}
