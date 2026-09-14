using SLNG.Core;
using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>
/// BUG-RENDER-24: the Reflection Probe (0x90) ExtraParams block, which is how Second Life says
/// "this object is a mirror". LibreMetaverse does not parse it — <c>ExtraParamType.ReflectionProbe
/// = 0x90</c> exists in its enum but <c>SetExtraParamsFromBytes</c> has no branch for it — so this
/// decoder is ours, and it reads bytes off the wire: exactly the code AGENTS.md wants covered by
/// tests rather than by looking at it in-world.
///
/// Layout under test is <c>LLReflectionProbeParams::pack</c> (llprimitive.cpp:1837): F32 ambiance,
/// F32 clip distance, U8 flags, little-endian; wrapped in the ExtraParams envelope of one count
/// byte then per entry a UInt16 type and a UInt32 payload length.
/// </summary>
public class ReflectionProbeExtraParamsTests
{
    private static byte[] Envelope(params (ushort Type, byte[] Payload)[] blocks)
    {
        var bytes = new System.Collections.Generic.List<byte> { (byte)blocks.Length };
        foreach (var (type, payload) in blocks)
        {
            bytes.AddRange(System.BitConverter.GetBytes(type));
            bytes.AddRange(System.BitConverter.GetBytes((uint)payload.Length));
            bytes.AddRange(payload);
        }
        return bytes.ToArray();
    }

    private static byte[] ProbePayload(float ambiance, float clipDistance, byte flags)
    {
        var bytes = new System.Collections.Generic.List<byte>();
        bytes.AddRange(System.BitConverter.GetBytes(ambiance));
        bytes.AddRange(System.BitConverter.GetBytes(clipDistance));
        bytes.Add(flags);
        return bytes.ToArray();
    }

    [Fact]
    public void ReadsAmbianceClipDistanceAndFlags()
    {
        var probe = GridSession.ExtraParamsReflectionProbe(
            Envelope((0x90, ProbePayload(0.75f, 2.5f, ReflectionProbeParams.FlagBoxVolume | ReflectionProbeParams.FlagMirror))));

        Assert.NotNull(probe);
        Assert.Equal(0.75f, probe!.Value.Ambiance, 5);
        Assert.Equal(2.5f, probe.Value.ClipDistance, 5);
        Assert.True(probe.Value.IsMirror);
        Assert.True(probe.Value.IsBox);
        Assert.False(probe.Value.IsDynamic);
    }

    [Fact]
    public void FindsTheBlockAfterOtherExtraParams()
    {
        // A real mirror carries more than one block, and the scan has to step over the ones it
        // does not understand using their declared length rather than a fixed size.
        var probe = GridSession.ExtraParamsReflectionProbe(Envelope(
            (0x10, new byte[16]),                                      // Flexible
            (0x20, new byte[16]),                                      // Light
            (0x90, ProbePayload(0f, 0f, ReflectionProbeParams.FlagMirror))));

        Assert.NotNull(probe);
        Assert.True(probe!.Value.IsMirror);
    }

    [Fact]
    public void ReturnsNullWhenTheObjectCarriesNoProbeBlock()
    {
        Assert.Null(GridSession.ExtraParamsReflectionProbe(Envelope((0x20, new byte[16]))));
        Assert.Null(GridSession.ExtraParamsReflectionProbe(new byte[] { 0 }));
        Assert.Null(GridSession.ExtraParamsReflectionProbe(null));
        Assert.Null(GridSession.ExtraParamsReflectionProbe(System.Array.Empty<byte>()));
    }

    [Fact]
    public void NotAMirrorWhenOnlyTheOtherFlagsAreSet()
    {
        // An ordinary (non-mirror) reflection probe is ordinary content. Reading one as a mirror
        // would hand it the single real-time hero probe and take it away from an actual mirror.
        var probe = GridSession.ExtraParamsReflectionProbe(
            Envelope((0x90, ProbePayload(1f, 1f, ReflectionProbeParams.FlagBoxVolume | ReflectionProbeParams.FlagDynamic))));

        Assert.NotNull(probe);
        Assert.False(probe!.Value.IsMirror);
        Assert.True(probe.Value.IsDynamic);
    }

    [Fact]
    public void RefusesATruncatedBlockRatherThanInventingFlags()
    {
        // Short payload: reading past it would take the flag byte from whatever followed, i.e.
        // invent or lose a mirror. Declared length shorter than the wire size is the case.
        var truncated = Envelope((0x90, new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 }));
        Assert.Null(GridSession.ExtraParamsReflectionProbe(truncated));
    }

    [Fact]
    public void ReadsAProbeBlockLongerThanThisViewerKnows()
    {
        // Forward compatibility: if Linden Lab appends fields, the first nine bytes still mean
        // what they mean today. Skipping the block instead would silently drop every mirror.
        var payload = new System.Collections.Generic.List<byte>(
            ProbePayload(0.25f, 1.5f, ReflectionProbeParams.FlagMirror));
        payload.AddRange(new byte[8]);

        var probe = GridSession.ExtraParamsReflectionProbe(Envelope((0x90, payload.ToArray())));

        Assert.NotNull(probe);
        Assert.True(probe!.Value.IsMirror);
        Assert.Equal(0.25f, probe.Value.Ambiance, 5);
    }
}
