using SLNG.Core;
using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>
/// FEAT-ANIMESH-01: the Extended Mesh (0x70) ExtraParams block, whose bit 0 says "this object is
/// an animated mesh". LibreMetaverse does not parse it (its ExtraParamType enum has no 0x70 and
/// <c>SetExtraParamsFromBytes</c> steps over the payload by length), so the flag is read off the
/// raw bytes here -- exactly the code AGENTS.md wants covered by tests instead of by looking at an
/// object lying on its side in-world.
///
/// Layout under test (secondlife/viewer): <c>U8 count</c>, then per entry <c>U16 type</c>,
/// <c>S32 size</c>, <c>size</c> payload bytes, all little-endian (lldatapacker.cpp:292-334);
/// the 0x70 payload is one <c>U32 flags</c> (<c>LLExtendedMeshParams::pack</c>,
/// llprimitive.cpp:2273-2285) with <c>ANIMATED_MESH_ENABLED_FLAG = 0x1</c> (llprimitive.h:357).
/// </summary>
public class ExtendedMeshExtraParamsTests
{
    private const ushort Flexible = 0x10;
    private const ushort Light = 0x20;
    private const ushort ExtendedMesh = 0x70;
    private const ushort ReflectionProbe = 0x90;

    private static byte[] Envelope(params (ushort Type, byte[] Payload)[] blocks)
    {
        var bytes = new List<byte> { (byte)blocks.Length };
        foreach (var (type, payload) in blocks)
        {
            bytes.AddRange(BitConverter.GetBytes(type));
            bytes.AddRange(BitConverter.GetBytes(payload.Length)); // S32
            bytes.AddRange(payload);
        }
        return bytes.ToArray();
    }

    private static byte[] Flags(uint flags) => BitConverter.GetBytes(flags);

    [Fact]
    public void TheFlagConstantIsBitZero()
    {
        // ANIMATED_MESH_ENABLED_FLAG = 0x1 (llprimitive.h:357). Pinned so a refactor of the
        // constant cannot silently change which bit means "animesh".
        Assert.Equal(0x1u, ExtendedMeshParams.FlagAnimatedMesh);
    }

    [Fact]
    public void FlagSet_IsAnimatedMesh()
    {
        Assert.True(GridSession.ExtraParamsAnimatedMesh(
            Envelope((ExtendedMesh, Flags(ExtendedMeshParams.FlagAnimatedMesh)))));
    }

    [Fact]
    public void FlagClear_IsNotAnimatedMesh()
    {
        // A 0x70 block with flags == 0 is what an object that was once animesh and has been
        // switched off may carry; it must read as "not animesh".
        Assert.False(GridSession.ExtraParamsAnimatedMesh(Envelope((ExtendedMesh, Flags(0)))));
    }

    [Fact]
    public void OtherBitsWithoutBitZero_IsNotAnimatedMesh()
    {
        // Every bit but the first. Reading "any non-zero flags" as animesh would hand a plain
        // rigged mesh a control avatar the day Linden Lab defines a second flag.
        Assert.False(GridSession.ExtraParamsAnimatedMesh(Envelope((ExtendedMesh, Flags(0xFFFFFFFE)))));
        Assert.False(GridSession.ExtraParamsAnimatedMesh(Envelope((ExtendedMesh, Flags(0x2)))));
    }

    [Fact]
    public void BitZeroAmongOtherBits_IsAnimatedMesh()
    {
        Assert.True(GridSession.ExtraParamsAnimatedMesh(Envelope((ExtendedMesh, Flags(0x80000003)))));
    }

    [Fact]
    public void BlockNotFirst_IsFoundAfterOtherBlocksAreSteppedOver()
    {
        // A real animesh root carries a mesh/sculpt block and often a light as well. The scan has
        // to step over the blocks it does not understand by their DECLARED size.
        Assert.True(GridSession.ExtraParamsAnimatedMesh(Envelope(
            (Flexible, new byte[16]),
            (Light, new byte[16]),
            (ExtendedMesh, Flags(ExtendedMeshParams.FlagAnimatedMesh)))));
    }

    [Fact]
    public void BlockBeforeOthers_IsStillFound()
    {
        Assert.True(GridSession.ExtraParamsAnimatedMesh(Envelope(
            (ExtendedMesh, Flags(ExtendedMeshParams.FlagAnimatedMesh)),
            (ReflectionProbe, new byte[9]))));
    }

    [Fact]
    public void BlockAbsent_IsNotAnimatedMesh()
    {
        // The latch case: the sim omits the block entirely once animesh is switched off, so
        // "absent" has to read as false rather than as "unchanged".
        Assert.False(GridSession.ExtraParamsAnimatedMesh(Envelope((Light, new byte[16]))));
        Assert.False(GridSession.ExtraParamsAnimatedMesh(Envelope(
            (Flexible, new byte[16]), (ReflectionProbe, new byte[9]))));
        Assert.False(GridSession.ExtraParamsAnimatedMesh(new byte[] { 0 }));
    }

    [Fact]
    public void NullOrEmpty_IsNotAnimatedMesh()
    {
        Assert.False(GridSession.ExtraParamsAnimatedMesh(null));
        Assert.False(GridSession.ExtraParamsAnimatedMesh(Array.Empty<byte>()));
    }

    [Fact]
    public void DeclaredSizeLargerThanTheRemainingBytes_IsRefused()
    {
        // Declares 8 payload bytes, carries 4. Taking the flags from "whatever is there" would
        // work here, but a block that lies about its own length cannot be trusted to say anything.
        var truncated = new List<byte> { 1 };
        truncated.AddRange(BitConverter.GetBytes(ExtendedMesh));
        truncated.AddRange(BitConverter.GetBytes(8));
        truncated.AddRange(Flags(ExtendedMeshParams.FlagAnimatedMesh));

        Assert.False(GridSession.ExtraParamsAnimatedMesh(truncated.ToArray()));
    }

    [Fact]
    public void DeclaredSizeButNoPayloadAtAll_IsRefused()
    {
        var headerOnly = new List<byte> { 1 };
        headerOnly.AddRange(BitConverter.GetBytes(ExtendedMesh));
        headerOnly.AddRange(BitConverter.GetBytes(4));

        Assert.False(GridSession.ExtraParamsAnimatedMesh(headerOnly.ToArray()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void SizeBelowFour_IsRefused(int size)
    {
        // The payload is one U32; fewer than four bytes cannot hold it. The bytes ARE present
        // here (the envelope is well-formed), so this fails on the size check, not on the
        // length check -- and a 0xFF in the bytes after it must not be read as the flag.
        var block = new List<byte> { 1 };
        block.AddRange(BitConverter.GetBytes(ExtendedMesh));
        block.AddRange(BitConverter.GetBytes(size));
        block.AddRange(Enumerable.Repeat((byte)0xFF, size));
        block.AddRange(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }); // whatever follows

        Assert.False(GridSession.ExtraParamsAnimatedMesh(block.ToArray()));
    }

    [Fact]
    public void NegativeSize_IsRefusedWithoutThrowing()
    {
        // S32 on the wire. A hostile or corrupt -1 must not become a huge unsigned skip or a
        // negative offset into the array.
        var block = new List<byte> { 1 };
        block.AddRange(BitConverter.GetBytes(ExtendedMesh));
        block.AddRange(BitConverter.GetBytes(-1));
        block.AddRange(Flags(ExtendedMeshParams.FlagAnimatedMesh));

        Assert.False(GridSession.ExtraParamsAnimatedMesh(block.ToArray()));
    }

    [Fact]
    public void ABlockThatClaimsToBeHugeBeforeTheOne_StopsTheScanInsteadOfWrapping()
    {
        // 0x7FFFFFFF would overflow an int offset if added naively and land back inside the
        // array; the real 0x70 block behind it must NOT be reached through such a wrap.
        var block = new List<byte> { 2 };
        block.AddRange(BitConverter.GetBytes(Light));
        block.AddRange(BitConverter.GetBytes(int.MaxValue));
        block.AddRange(new byte[16]);
        block.AddRange(BitConverter.GetBytes(ExtendedMesh));
        block.AddRange(BitConverter.GetBytes(4));
        block.AddRange(Flags(ExtendedMeshParams.FlagAnimatedMesh));

        Assert.False(GridSession.ExtraParamsAnimatedMesh(block.ToArray()));
    }

    [Fact]
    public void CountLargerThanTheEntriesPresent_DoesNotRunOffTheEnd()
    {
        // The count byte promises three entries; one Light block follows and then the bytes end.
        var block = Envelope((Light, new byte[16])).ToList();
        block[0] = 3;

        Assert.False(GridSession.ExtraParamsAnimatedMesh(block.ToArray()));
    }

    [Fact]
    public void TruncatedHeaderOfTheNextEntry_DoesNotThrow()
    {
        // Count says two; the second entry is cut off mid-header (3 of its 6 header bytes).
        var block = Envelope((Light, new byte[16])).ToList();
        block[0] = 2;
        block.AddRange(new byte[] { 0x70, 0x00, 0x04 });

        Assert.False(GridSession.ExtraParamsAnimatedMesh(block.ToArray()));
    }

    [Fact]
    public void ABlockLongerThanThisViewerKnows_StillYieldsItsFirstFourBytes()
    {
        // Forward compatibility, like the reflection-probe block: if Linden Lab appends fields,
        // the U32 at the front still means what it means today.
        var payload = new List<byte>(Flags(ExtendedMeshParams.FlagAnimatedMesh));
        payload.AddRange(new byte[8]);

        Assert.True(GridSession.ExtraParamsAnimatedMesh(Envelope((ExtendedMesh, payload.ToArray()))));
    }

    [Fact]
    public void FlagsAreLittleEndian()
    {
        // Bit 0 is the least significant bit of the FIRST byte on the wire; a big-endian read
        // would see this as bit 24 and report "not animesh".
        Assert.True(GridSession.ExtraParamsAnimatedMesh(Envelope((ExtendedMesh, new byte[] { 0x01, 0x00, 0x00, 0x00 }))));
        Assert.False(GridSession.ExtraParamsAnimatedMesh(Envelope((ExtendedMesh, new byte[] { 0x00, 0x00, 0x00, 0x01 }))));
    }
}
