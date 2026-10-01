using System.Buffers.Binary;
using SLNG.Core;

namespace SLNG.Net;

/// <summary>
/// What one ExtraParams buffer says about the three blocks LibreMetaverse cannot be trusted with:
/// Light (0x20), Reflection Probe (0x90) and Extended Mesh (0x70). One validated walk over the
/// entries, shared by every caller, so the three scans cannot drift apart again.
///
/// <para><b>Why the raw bytes.</b> <c>Primitive.Light</c> and <c>ExtendedMeshFlags</c> are written
/// by <c>SetExtraParamsFromBytes</c> and never cleared, and OpenSim signals "switched off" by
/// OMITTING the block -- so the library keeps reporting the last-enabled state forever. It does not
/// parse the Reflection Probe at all. The only way to know a block is absent is to read the bytes
/// of the update that omitted it.</para>
///
/// <para><b>Wire layout</b> (lldatapacker.cpp:292-334): <c>U8 count</c>, then per entry <c>U16
/// type</c>, <c>S32 size</c>, <c>size</c> payload bytes, little-endian. The size is SIGNED. The
/// Light and Probe scans used to read it unsigned and step with <c>i += (int)len</c>, so a size of
/// 0x80000000 or more drove the offset negative and the next read threw -- inside a packet handler,
/// where the catch hid it and the rest of the packet's blocks were never latched. Here every size
/// is checked against the bytes actually present before it is used.</para>
///
/// <para><b>Certainty.</b> A block is only ever reported ABSENT after the walk has read every entry
/// the count byte promised (<see cref="Complete"/>). A buffer that stops making sense says nothing
/// about what was behind the break, so the <c>...Known</c> properties are true only for what was
/// found, or for everything once the walk completed. Callers update a latch only when the answer is
/// known, and leave it alone otherwise.</para>
///
/// <para>The first entry of each kind wins. A Light block counts as present when its envelope is
/// valid, whatever its size -- the same test the old scan applied, minus the lies.</para>
/// </summary>
internal readonly struct ExtraParamsScan
{
    internal const ushort LightType = 0x20;               // ExtraParamType.Light
    internal const ushort ReflectionProbeType = 0x90;     // ExtraParamType.ReflectionProbe

    private const int EntryHeaderSize = 6;                // U16 type + S32 size

    /// <summary>Every entry the count byte promised was read and valid.</summary>
    public bool Complete { get; private init; }

    /// <summary>A Light (0x20) block is present.</summary>
    public bool Light { get; private init; }

    /// <summary>The Reflection Probe (0x90) block, or null if there is none or its payload is too
    /// short to hold one (never invent flags from bytes that are not there).</summary>
    public ReflectionProbeParams? Probe { get; private init; }

    /// <summary>An Extended Mesh (0x70) block with the animated-mesh bit set is present.</summary>
    public bool AnimatedMesh { get; private init; }

    public bool LightKnown => Light || Complete;
    public bool ProbeKnown => Probe is not null || Complete;
    public bool AnimatedMeshKnown => AnimatedMesh || Complete;

    /// <summary>Reads one ExtraParams buffer. An empty buffer is a firm "no blocks" -- an
    /// ObjectUpdate with no ExtraParams field at all is how a prim with none arrives.</summary>
    internal static ExtraParamsScan Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < 1)
        {
            return new ExtraParamsScan { Complete = true };
        }

        int count = data[0];
        int i = 1;
        bool light = false;
        bool animatedMesh = false;
        ReflectionProbeParams? probe = null;
        bool probeSeen = false;
        bool meshSeen = false;

        for (int k = 0; k < count; k++)
        {
            if (data.Length - i < EntryHeaderSize)
            {
                return Partial(light, probe, animatedMesh);
            }

            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(i, 2));
            int size = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(i + 2, 4));
            i += EntryHeaderSize;

            // The declared size has to be real before it is used for anything, whatever the type:
            // an entry that lies about its own length cannot be stepped over, and nothing behind it
            // can be trusted either.
            if (size < 0 || size > data.Length - i)
            {
                return Partial(light, probe, animatedMesh);
            }

            var payload = data.Slice(i, size);
            switch (type)
            {
                case LightType:
                    light = true;
                    break;

                case ReflectionProbeType when !probeSeen:
                    probeSeen = true;
                    // LLReflectionProbeParams::pack (llprimitive.cpp:1837): F32 ambiance, F32 clip
                    // distance, U8 flags. A longer block is read for its first nine bytes -- if
                    // Linden Lab appends fields those still mean what they mean today.
                    if (payload.Length >= ReflectionProbeParams.WireSize)
                    {
                        probe = new ReflectionProbeParams(
                            BinaryPrimitives.ReadSingleLittleEndian(payload[..4]),
                            BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(4, 4)),
                            payload[8]);
                    }
                    break;

                case ExtendedMeshParams.ParamType when !meshSeen:
                    meshSeen = true;
                    // One U32 of flags (llprimitive.cpp:2273-2285); a payload shorter than that
                    // cannot hold it.
                    if (payload.Length >= ExtendedMeshParams.WireSize)
                    {
                        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(payload[..ExtendedMeshParams.WireSize]);
                        animatedMesh = (flags & ExtendedMeshParams.FlagAnimatedMesh) != 0;
                    }
                    break;
            }

            i += size;
        }

        return new ExtraParamsScan { Complete = true, Light = light, Probe = probe, AnimatedMesh = animatedMesh };
    }

    private static ExtraParamsScan Partial(bool light, ReflectionProbeParams? probe, bool animatedMesh)
        => new() { Complete = false, Light = light, Probe = probe, AnimatedMesh = animatedMesh };
}
