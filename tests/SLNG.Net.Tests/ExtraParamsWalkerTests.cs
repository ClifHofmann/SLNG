using SLNG.Core;
using SLNG.Net;
using Xunit;
using static SLNG.Net.Tests.ExtraParamsBytes;

namespace SLNG.Net.Tests;

/// <summary>
/// BUG-NET-25: the three ExtraParams scans (Light 0x20, Reflection Probe 0x90, Extended Mesh 0x70)
/// share ONE validated entry walker. The Light and Probe scans used to read the entry size as an
/// unsigned value and step with <c>i += (int)len</c>: a size of 0x80000000 or more drove the
/// offset negative and the next read threw -- inside a packet handler, where the catch hides it and
/// the rest of the packet's blocks are never latched.
///
/// <para>The size is a SIGNED 32-bit value on the wire (lldatapacker.cpp:292-334) and is checked
/// against the bytes actually present before it is used for anything.</para>
/// </summary>
public class ExtraParamsWalkerTests
{
    private const int HostileNegative = int.MinValue; // 0x80000000 on the wire: drove (int)len negative
    private const int HostileHuge = int.MaxValue;

    private static byte[] HostileAfter(params byte[][] goodEntries)
    {
        // The good entries first, then one entry whose declared size is a lie, counted so the walk
        // has to try it.
        var entries = goodEntries.Concat(new[] { EntryWithDeclaredSize(0x55, HostileNegative) }).ToArray();
        return Raw((byte)entries.Length, entries);
    }

    // --- the unsigned-size crash ------------------------------------------------------------------

    [Theory]
    [InlineData(HostileNegative)]
    [InlineData(-1)]
    [InlineData(HostileHuge)]
    public void LightScan_RefusesAHostileSizeWithoutThrowing(int declared)
    {
        var data = Raw(1, EntryWithDeclaredSize(0x55, declared));

        Assert.False(GridSession.ExtraParamsContainsLight(data));
    }

    [Theory]
    [InlineData(HostileNegative)]
    [InlineData(-1)]
    [InlineData(HostileHuge)]
    public void ProbeScan_RefusesAHostileSizeWithoutThrowing(int declared)
    {
        var data = Raw(1, EntryWithDeclaredSize(0x55, declared));

        Assert.Null(GridSession.ExtraParamsReflectionProbe(data));
    }

    [Theory]
    [InlineData(HostileNegative)]
    [InlineData(-1)]
    [InlineData(HostileHuge)]
    public void MeshScan_RefusesAHostileSizeWithoutThrowing(int declared)
    {
        var data = Raw(1, EntryWithDeclaredSize(0x55, declared));

        Assert.False(GridSession.ExtraParamsAnimatedMesh(data));
    }

    [Fact]
    public void EntriesBeforeTheHostileOneAreStillHonoured()
    {
        // The walk stops at the lie; it does not forget what it already read.
        Assert.True(GridSession.ExtraParamsContainsLight(HostileAfter(Entry(Light, LightPayload()))));

        var probe = GridSession.ExtraParamsReflectionProbe(
            HostileAfter(Entry(ReflectionProbe, ProbePayload(0.5f, 1.5f, ReflectionProbeParams.FlagMirror))));
        Assert.NotNull(probe);
        Assert.True(probe!.Value.IsMirror);

        Assert.True(GridSession.ExtraParamsAnimatedMesh(
            HostileAfter(Entry(ExtendedMesh, MeshFlags(ExtendedMeshParams.FlagAnimatedMesh)))));
    }

    [Fact]
    public void AllThreeKindsAreReadFromOneBlockEvenWhenAHostileEntryFollows()
    {
        var data = HostileAfter(
            Entry(Light, LightPayload()),
            Entry(ReflectionProbe, ProbePayload(0f, 0f, ReflectionProbeParams.FlagMirror)),
            Entry(ExtendedMesh, MeshFlags(ExtendedMeshParams.FlagAnimatedMesh)));

        Assert.True(GridSession.ExtraParamsContainsLight(data));
        Assert.True(GridSession.ExtraParamsReflectionProbe(data)!.Value.IsMirror);
        Assert.True(GridSession.ExtraParamsAnimatedMesh(data));
    }

    [Fact]
    public void ATruncatedHeaderDoesNotThrow()
    {
        // Count says two; the second entry is cut off mid-header (3 of its 6 bytes).
        var data = Raw(2, Entry(Flexible, new byte[16]), new byte[] { 0x20, 0x00, 0x10 });

        Assert.False(GridSession.ExtraParamsContainsLight(data));
        Assert.Null(GridSession.ExtraParamsReflectionProbe(data));
        Assert.False(GridSession.ExtraParamsAnimatedMesh(data));
    }

    [Fact]
    public void ASizeBeyondTheRemainingBytesIsRefusedEvenForTheKindBeingLookedFor()
    {
        // A Light block that claims 16 bytes and carries 3 has not positively shown a light.
        var data = Raw(1, EntryWithDeclaredSize(Light, 16, 1, 2, 3));

        Assert.False(GridSession.ExtraParamsContainsLight(data));
    }

    [Fact]
    public void ACountLargerThanTheEntriesPresentDoesNotRunOffTheEnd()
    {
        var data = Raw(5, Entry(Light, LightPayload()));

        Assert.True(GridSession.ExtraParamsContainsLight(data)); // the entry that IS there counts
        Assert.Null(GridSession.ExtraParamsReflectionProbe(data));
    }

    [Fact]
    public void NullEmptyAndCountZeroAreSimplyEmpty()
    {
        Assert.False(GridSession.ExtraParamsContainsLight(null));
        Assert.False(GridSession.ExtraParamsContainsLight(Array.Empty<byte>()));
        Assert.False(GridSession.ExtraParamsContainsLight(new byte[] { 0 }));
        Assert.Null(GridSession.ExtraParamsReflectionProbe(new byte[] { 0 }));
    }

    // --- the shared walk: what is found, and how sure we are that nothing else is there ---------

    [Fact]
    public void AWellFormedBlockIsComplete_SoAbsenceIsAKnownAnswer()
    {
        var scan = ExtraParamsScan.Read(Envelope((Flexible, new byte[16])));

        Assert.True(scan.Complete);
        Assert.False(scan.Light);
        Assert.Null(scan.Probe);
        Assert.False(scan.AnimatedMesh);
        Assert.True(scan.LightKnown && scan.ProbeKnown && scan.AnimatedMeshKnown);
    }

    [Fact]
    public void AMalformedBlockWithNothingFoundIsNotAnAnswerForAnyKind()
    {
        // "Not there" can only be claimed after walking everything; a block that stops making
        // sense says nothing about what might have been behind the break.
        var scan = ExtraParamsScan.Read(Raw(1, EntryWithDeclaredSize(0x55, HostileNegative)));

        Assert.False(scan.Complete);
        Assert.False(scan.LightKnown);
        Assert.False(scan.ProbeKnown);
        Assert.False(scan.AnimatedMeshKnown);
    }

    [Fact]
    public void AMalformedBlockStillAnswersForWhatItDidShow()
    {
        var scan = ExtraParamsScan.Read(HostileAfter(Entry(Light, LightPayload())));

        Assert.False(scan.Complete);
        Assert.True(scan.Light);
        Assert.True(scan.LightKnown);
        Assert.False(scan.ProbeKnown);
    }

    [Fact]
    public void ADeclaredShortProbeBlockIsAnAnswer_NoProbe()
    {
        // A well-formed envelope around too few bytes: refused (never invent flags), and since the
        // walk completed, "no probe" is a firm answer.
        var scan = ExtraParamsScan.Read(Envelope((ReflectionProbe, new byte[8])));

        Assert.Null(scan.Probe);
        Assert.True(scan.ProbeKnown);
    }

    [Fact]
    public void TheFirstBlockOfAKindWins()
    {
        var scan = ExtraParamsScan.Read(Envelope(
            (ReflectionProbe, ProbePayload(1f, 1f, ReflectionProbeParams.FlagMirror)),
            (ReflectionProbe, ProbePayload(2f, 2f, 0))));

        Assert.Equal(1f, scan.Probe!.Value.Ambiance, 5);
    }
}
