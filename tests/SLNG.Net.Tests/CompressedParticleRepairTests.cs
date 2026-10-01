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

    // ---------------------------------------------------------------------------------------------
    // BUG-NET-26: the EXTENDED particle block (compressed flag 0x400).
    //
    // It does not sit where the legacy block does. The reference viewer reads it LAST, after the
    // volume params, the texture entry and the texture animation (LLVOVolume::processUpdateMessage,
    // llvovolume.cpp:445-523: unpackVolumeParams, unpackTEMessage, then `if (value & 0x40)` the
    // texture animation, then `if (value & 0x400) unpackParticleSource(*dp, mOwnerID, false)`),
    // and OpenSim writes it last for the same reason (LLClientView.cs CreateCompressedUpdateBlockZC,
    // `if (haspsnew)` at the end of the method, behind `hastexanim`). What stands at the legacy
    // position in such an object is the ExtraParams.
    //
    // The block is self-delimiting: S32 system size (68), the 68-byte system, S32 part size
    // (18, +2 with glow, +2 with a blend function), the part data -- 94 to 98 bytes
    // (llpartdata.cpp:280-309, 98-152). The first version of ExtractParticleBlock took 98 bytes
    // from the legacy position, i.e. the extra params and whatever followed them, and handed that
    // to LibreMetaverse as a particle system.
    // ---------------------------------------------------------------------------------------------

    private const uint NoOptionalSections = 0;

    /// <summary>A real extended block as LibreMetaverse writes it: S32 68, system, S32 part size,
    /// part data, optionally glow and blend. 96 with one of them, 98 with both.</summary>
    private static byte[] ExtendedFountainBlock(bool glow, bool blend)
    {
        var sys = new Primitive.ParticleSystem
        {
            CRC = 1,
            PartFlags = (uint)Primitive.ParticleSystem.ParticleFlags.UseNewAngle,
            Pattern = Primitive.ParticleSystem.SourcePattern.Explode,
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
            PartStartGlow = glow ? 0.5f : 0f,
            PartEndGlow = glow ? 1f : 0f,
            BlendFuncSource = (byte)(blend ? Primitive.ParticleSystem.BlendFunc.One : Primitive.ParticleSystem.BlendFunc.SourceAlpha),
            BlendFuncDest = (byte)(blend ? Primitive.ParticleSystem.BlendFunc.One : Primitive.ParticleSystem.BlendFunc.OneMinusSourceAlpha),
        };

        byte[] bytes = sys.GetBytes();
        Assert.Equal(94 + (glow ? 2 : 0) + (blend ? 2 : 0), bytes.Length);
        return bytes;
    }

    /// <summary>The smallest extended block there is: the size fields around a legacy-shaped system
    /// and part, no glow, no blend function (94 bytes). LibreMetaverse never writes one -- it picks
    /// the legacy layout when it can -- but the format allows it (part size 18).</summary>
    private static byte[] MinimalExtendedBlock()
    {
        byte[] legacy = LegacyFountainBlock();
        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes(68));
        bytes.AddRange(legacy[..68]);
        bytes.AddRange(BitConverter.GetBytes(18));
        bytes.AddRange(legacy[68..]);
        Assert.Equal(94, bytes.Count);
        return bytes.ToArray();
    }

    /// <summary>An object laid out the way the viewer reads it and the simulator writes it, with
    /// the particle block where the 0x400 flag puts it: last.</summary>
    private static byte[] ViewerOrdered(
        uint flags, byte[] extended, bool withLegacy = false, byte[]? textureEntry = null, byte[]? textureAnim = null)
        => CompressedExtendedMeshTests.Compressed(
            flags, CompressedExtendedMeshTests.ExtraParams((0x20, new byte[16])), out _,
            withLegacyParticles: withLegacy, newParticles: extended,
            textureEntry: textureEntry, textureAnim: textureAnim);

    [Theory]
    [InlineData(false, false)]   // 94: the minimal layout (built by hand above)
    [InlineData(true, false)]    // 96
    [InlineData(false, true)]    // 96
    [InlineData(true, true)]     // 98
    public void The_extended_block_is_found_behind_the_texture_entry_and_comes_back_exactly(bool glow, bool blend)
    {
        byte[] extended = !glow && !blend ? MinimalExtendedBlock() : ExtendedFountainBlock(glow, blend);
        byte[] block = ViewerOrdered(NoOptionalSections, extended);

        byte[]? extracted = CompressedParticleRepair.ExtractParticleBlock(block);

        Assert.NotNull(extracted);
        Assert.Equal(extended, extracted);
    }

    [Fact]
    public void The_extended_block_parses_into_a_usable_system_with_its_glow_and_blend()
    {
        // End to end through the library's own parser: what the compressed handler does with the
        // bytes. Before the fix this was an all-default system -- "no particles" -- and, since the
        // handler compares it with what the library already holds, an emitter that a full update
        // had set up was overwritten with it.
        byte[] block = ViewerOrdered(NoOptionalSections, ExtendedFountainBlock(glow: true, blend: true));

        byte[] extracted = CompressedParticleRepair.ExtractParticleBlock(block)!;
        var system = new Primitive.ParticleSystem(extracted, 0);

        Assert.Equal(1u, system.CRC);
        Assert.Equal(3f, system.PartMaxAge, Tol);
        Assert.Equal(10, system.BurstPartCount);
        Assert.Equal(0.5f, system.PartStartGlow, 0.01f);
        Assert.Equal(1f, system.PartEndGlow, 0.01f);
        Assert.Equal((byte)Primitive.ParticleSystem.BlendFunc.One, system.BlendFuncSource);

        ParticleSystemData? data = ParticleSystemConverter.FromWire(system);
        Assert.NotNull(data);
        Assert.False(data!.IsInert);
        Assert.Equal(SlParticlePattern.Explode, data.Pattern);
        Assert.Equal(SlParticleBlendFunc.One, data.BlendFuncSource);
    }

    [Fact]
    public void The_extended_block_is_not_what_stands_at_the_legacy_position()
    {
        // In a viewer-ordered object the bytes at the legacy position are the ExtraParams. The old
        // extraction returned 98 bytes from there; that must never be what comes back.
        byte[] extended = ExtendedFountainBlock(glow: true, blend: true);
        byte[] block = ViewerOrdered(NoOptionalSections, extended);
        int legacyPosition = PrefixSize + 4 + 16;

        byte[] whatTheOldCodeReturned = block[legacyPosition..(legacyPosition + CompressedParticleRepair.MaxBlockSize)];
        byte[]? extracted = CompressedParticleRepair.ExtractParticleBlock(block);

        Assert.NotEqual(whatTheOldCodeReturned, extracted);
        Assert.Equal(extended, extracted);
    }

    [Fact]
    public void An_extended_block_behind_a_texture_entry_and_a_texture_animation_is_found()
    {
        // The two sections in front of it are each an S32 size then that many bytes; neither has to
        // be parsed, only stepped over -- which is why the start of the block IS determinable.
        byte[] extended = ExtendedFountainBlock(glow: false, blend: true);
        byte[] textureEntry = Enumerable.Repeat((byte)0xCD, 111).ToArray();
        byte[] textureAnim = Enumerable.Repeat((byte)0xEF, 16).ToArray();

        byte[] block = ViewerOrdered(NoOptionalSections, extended, textureEntry: textureEntry, textureAnim: textureAnim);

        Assert.Equal(extended, CompressedParticleRepair.ExtractParticleBlock(block));
    }

    [Theory]
    [InlineData(CompressedParticleRepair.HasAngularVelocity)]
    [InlineData(CompressedParticleRepair.HasParent)]
    [InlineData(CompressedParticleRepair.HasText)]
    [InlineData(CompressedParticleRepair.HasMediaUrl)]
    [InlineData(CompressedParticleRepair.HasSound)]
    [InlineData(CompressedParticleRepair.HasNameValues)]
    [InlineData(CompressedParticleRepair.HasAngularVelocity | CompressedParticleRepair.HasParent
        | CompressedParticleRepair.HasText | CompressedParticleRepair.HasMediaUrl
        | CompressedParticleRepair.HasSound | CompressedParticleRepair.HasNameValues)]
    public void Every_optional_section_ahead_of_the_extended_block_is_accounted_for(uint optional)
    {
        // Sound and name values sit AFTER the extra params, so they are new to the walk: a missed
        // one shifts the read by its own width and the size fields below read garbage.
        byte[] extended = ExtendedFountainBlock(glow: true, blend: true);

        byte[] block = ViewerOrdered(optional, extended, textureEntry: new byte[37]);

        Assert.Equal(extended, CompressedParticleRepair.ExtractParticleBlock(block));
    }

    [Fact]
    public void Several_extra_params_entries_are_stepped_over_by_their_declared_sizes()
    {
        byte[] extended = ExtendedFountainBlock(glow: true, blend: false);
        byte[] extra = CompressedExtendedMeshTests.ExtraParams(
            (0x10, new byte[16]), (0x20, new byte[16]), (0x70, BitConverter.GetBytes(1u)), (0x90, new byte[9]));

        byte[] block = CompressedExtendedMeshTests.Compressed(
            CompressedParticleRepair.HasParent, extra, out _, newParticles: extended, textureEntry: new byte[64]);

        Assert.Equal(extended, CompressedParticleRepair.ExtractParticleBlock(block));
    }

    [Fact]
    public void With_both_flags_the_extended_block_wins_as_it_does_in_the_viewer()
    {
        // The viewer unpacks the legacy block first and the extended one last, into the same
        // source, so the extended one is what the object ends up with. The legacy block in front
        // of the extra params still has to be skipped to find where those -- and so everything
        // behind them -- start.
        byte[] extended = ExtendedFountainBlock(glow: true, blend: true);

        byte[] block = ViewerOrdered(NoOptionalSections, extended, withLegacy: true, textureEntry: new byte[20]);

        Assert.Equal(extended, CompressedParticleRepair.ExtractParticleBlock(block));
    }

    [Fact]
    public void With_both_flags_and_an_unusable_extended_block_the_legacy_block_is_used()
    {
        // A part size this viewer does not know how to read. The legacy block is still a good
        // description of the emitter, so it is better than nothing.
        byte[] legacy = LegacyFountainBlock();
        byte[] unusable = ExtendedFountainBlock(glow: true, blend: true);
        BitConverter.GetBytes(0x7FFF).CopyTo(unusable, 72);
        byte[] block = ViewerOrdered(NoOptionalSections, unusable, withLegacy: true);
        legacy.CopyTo(block, PrefixSize + 4 + 16); // the helper fills the legacy slot with filler

        Assert.Equal(legacy, CompressedParticleRepair.ExtractParticleBlock(block));
    }

    [Fact]
    public void An_extended_block_cut_off_anywhere_yields_null_instead_of_reading_past_the_end()
    {
        byte[] extended = ExtendedFountainBlock(glow: true, blend: true);
        byte[] full = ViewerOrdered(NoOptionalSections, extended, textureEntry: new byte[50]);
        int blockStart = full.Length - extended.Length;

        // Mid-block, one byte short of complete, right before it, inside the texture entry, and
        // inside the extra params.
        foreach (int cut in new[] { blockStart + 40, full.Length - 1, blockStart, blockStart - 20, blockStart - 70 })
        {
            Assert.Null(CompressedParticleRepair.ExtractParticleBlock(full[..cut]));
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void A_texture_entry_that_lies_about_its_size_yields_null(int declared)
    {
        byte[] extended = ExtendedFountainBlock(glow: true, blend: true);
        byte[] block = ViewerOrdered(NoOptionalSections, extended, textureEntry: new byte[8]);
        // The TE size field sits right ahead of the 8 TE bytes, which sit right ahead of the block.
        int teSizeAt = block.Length - extended.Length - 8 - 4;
        BitConverter.GetBytes(declared).CopyTo(block, teSizeAt);

        Assert.Null(CompressedParticleRepair.ExtractParticleBlock(block));
    }

    [Fact]
    public void A_negative_texture_entry_size_cannot_steer_the_walk_back_onto_a_look_alike_block()
    {
        // A hostile size that is negative moves the cursor BACKWARDS. Put a perfectly valid
        // extended block inside an earlier section and aim the size at it: only the size check
        // stands between the walk and a "particle system" the object never had.
        byte[] decoy = ExtendedFountainBlock(glow: true, blend: true);
        byte[] extra = CompressedExtendedMeshTests.ExtraParams((0x10, decoy));
        byte[] real = ExtendedFountainBlock(glow: false, blend: true);
        byte[] block = CompressedExtendedMeshTests.Compressed(0, extra, out int extraAt, newParticles: real);

        int decoyAt = extraAt + 1 + 6;                                    // count byte + entry header
        int teSizeAt = extraAt + extra.Length + 23;                       // behind the extra params and the shape
        BitConverter.GetBytes(decoyAt - (teSizeAt + 4)).CopyTo(block, teSizeAt);

        Assert.Null(CompressedParticleRepair.ExtractParticleBlock(block));
    }

    [Fact]
    public void A_block_with_a_system_size_this_viewer_does_not_know_is_refused()
    {
        // The viewer skips such a block and shows nothing (llpartdata.cpp:286-304); LibreMetaverse
        // returns an all-default system. Neither is something to hand on as particles.
        byte[] extended = ExtendedFountainBlock(glow: true, blend: true);
        BitConverter.GetBytes(72).CopyTo(extended, 0);

        Assert.Null(CompressedParticleRepair.ExtractParticleBlock(ViewerOrdered(NoOptionalSections, extended)));
    }

    [Theory]
    [InlineData(17)]             // smaller than the part data every system carries
    [InlineData(23)]             // 98 + 1: newer than this viewer
    [InlineData(-1)]
    [InlineData(int.MaxValue)]   // would overflow 76 + size
    public void A_block_with_an_unusable_part_size_is_refused(int partSize)
    {
        // Followed by plenty of bytes, so that it is the size check that refuses it and not the
        // object simply ending.
        byte[] extended = ExtendedFountainBlock(glow: true, blend: true);
        BitConverter.GetBytes(partSize).CopyTo(extended, 72);
        byte[] withRoomToSpare = extended.Concat(Enumerable.Repeat((byte)0x99, 40)).ToArray();

        Assert.Null(CompressedParticleRepair.ExtractParticleBlock(ViewerOrdered(NoOptionalSections, withRoomToSpare)));
    }

    [Fact]
    public void Bytes_after_the_declared_end_of_the_block_are_not_part_of_it()
    {
        byte[] extended = ExtendedFountainBlock(glow: false, blend: true);
        byte[] block = ViewerOrdered(NoOptionalSections, extended.Concat(Enumerable.Repeat((byte)0x99, 12)).ToArray());

        Assert.Equal(extended, CompressedParticleRepair.ExtractParticleBlock(block));
    }

    [Fact]
    public void A_scratch_pad_object_with_an_extended_block_is_unknown_not_guessed()
    {
        // The two decoders disagree on that section's width (see TryFindExtraParams); the extended
        // block lies behind it, so there is no trustworthy start.
        byte[] block = ViewerOrdered(CompressedParticleRepair.HasScratchPad, ExtendedFountainBlock(glow: true, blend: true));

        Assert.Null(CompressedParticleRepair.ExtractParticleBlock(block));
    }

    [Fact]
    public void A_tree_with_the_extended_flag_set_and_no_block_behind_it_yields_null()
    {
        // OpenSim's compact tree/grass form ends after 28 zero bytes (extra params count, 23 path
        // and profile, 4 texture) and never carries particles.
        byte[] block = CompressedExtendedMeshTests.Compressed(
            CompressedParticleRepair.IsTree | CompressedParticleRepair.HasParticlesNew,
            new byte[] { 0 }, out _);

        Assert.Null(CompressedParticleRepair.ExtractParticleBlock(block));
    }
}
