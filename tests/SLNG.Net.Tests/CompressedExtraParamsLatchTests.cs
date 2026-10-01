using SLNG.Core;
using SLNG.Net;
using Xunit;
using static SLNG.Net.Tests.ExtraParamsBytes;

namespace SLNG.Net.Tests;

/// <summary>
/// BUG-NET-25: the Light and Reflection-Probe latches were fed from <c>ObjectUpdate</c> only, so a
/// mirror streamed as <c>ObjectUpdateCompressed</c> -- which is how OpenSim sends objects at login
/// and what the object cache replays -- was never a mirror, and a light that went out in a
/// compressed update was never turned off.
///
/// <para>The ExtraParams of a compressed object sit in the middle of one blob; their offset comes
/// from <see cref="CompressedParticleRepair.TryFindExtraParams"/>. When the layout cannot be
/// established the previous answers stand: "not there" is only ever claimed after reading the
/// block that would have held it.</para>
///
/// <para>What an ABSENT Light block means is the same as in a full update: the object's current
/// ExtraParams carry no light. LibreMetaverse's compressed handler calls the same
/// <c>SetExtraParamsFromBytes</c> and has the same never-cleared <c>Primitive.Light</c>, so a
/// compressed update that drops the block is exactly as stale-prone as a full one. The simulator
/// writes a single 0 count byte when there are no extra params at all (OpenSim
/// LLClientView.cs:8072-8073), which is therefore a firm "no light, no probe, no animesh".</para>
/// </summary>
public class CompressedExtraParamsLatchTests
{
    private const ulong Region = 1000UL;
    private const uint LocalId = 77; // what CompressedExtendedMeshTests.Compressed writes

    private static byte[] Block(uint flags, byte[] extraParams)
        => CompressedExtendedMeshTests.Compressed(flags, extraParams, out _);

    private static byte[] MirrorBlock(uint flags = 0)
        => Block(flags, Envelope((ReflectionProbe, ProbePayload(0f, 0f, ReflectionProbeParams.FlagMirror))));

    private static byte[] LightBlock(uint flags = 0) => Block(flags, Envelope((Light, LightPayload())));

    private static byte[] NoExtraParams(uint flags = 0) => Block(flags, new byte[] { 0 });

    [Fact]
    public void AMirrorArrivingCompressedIsLatched()
    {
        using var session = new GridSession();

        Assert.True(session.LatchCompressedExtraParams(Region, LocalId, MirrorBlock()));

        var probe = session.ReflectionProbeLatched(Region, LocalId);
        Assert.NotNull(probe);
        Assert.True(probe!.Value.IsMirror);
    }

    [Fact]
    public void ALightArrivingCompressedIsLatchedAndItsRemovalIsToo()
    {
        using var session = new GridSession();
        Assert.True(session.LatchCompressedExtraParams(Region, LocalId, LightBlock()));
        Assert.False(session.IsLightRemovedLatched(Region, LocalId));

        // The same object streamed again with the light switched off: the sim sends a lone 0.
        Assert.True(session.LatchCompressedExtraParams(Region, LocalId, NoExtraParams()));

        Assert.True(session.IsLightRemovedLatched(Region, LocalId));
    }

    [Fact]
    public void AMirrorThatLosesItsProbeInACompressedUpdateStopsBeingOne()
    {
        using var session = new GridSession();
        session.LatchCompressedExtraParams(Region, LocalId, MirrorBlock());

        Assert.True(session.LatchCompressedExtraParams(Region, LocalId, NoExtraParams()));

        Assert.Null(session.ReflectionProbeLatched(Region, LocalId));
    }

    [Fact]
    public void AnUnchangedCompressedUpdateChangesNothing()
    {
        using var session = new GridSession();
        var block = Block(0, Envelope(
            (Light, LightPayload()),
            (ReflectionProbe, ProbePayload(0f, 0f, ReflectionProbeParams.FlagMirror)),
            (ExtendedMesh, MeshFlags(ExtendedMeshParams.FlagAnimatedMesh))));
        Assert.True(session.LatchCompressedExtraParams(Region, LocalId, block));

        // The cache replay and a re-sent compressed object do this constantly.
        Assert.False(session.LatchCompressedExtraParams(Region, LocalId, block));
        Assert.False(session.LatchCompressedExtraParams(Region, LocalId, block));
    }

    [Fact]
    public void AnOrdinaryCompressedPrimLatchesNothing()
    {
        using var session = new GridSession();

        Assert.False(session.LatchCompressedExtraParams(Region, LocalId, NoExtraParams()));
        Assert.False(session.LatchCompressedExtraParams(Region, LocalId,
            Block(0, Envelope((Flexible, new byte[16])))));
        Assert.False(session.IsLightRemovedLatched(Region, LocalId));
        Assert.Null(session.ReflectionProbeLatched(Region, LocalId));
        Assert.False(session.IsAnimatedMeshLatched(Region, LocalId));
    }

    [Theory]
    [InlineData(CompressedParticleRepair.HasAngularVelocity)]
    [InlineData(CompressedParticleRepair.HasParent)]
    [InlineData(CompressedParticleRepair.IsTree)]
    [InlineData(CompressedParticleRepair.HasText)]
    [InlineData(CompressedParticleRepair.HasMediaUrl)]
    [InlineData(CompressedParticleRepair.HasAngularVelocity | CompressedParticleRepair.HasParent
        | CompressedParticleRepair.HasText | CompressedParticleRepair.HasMediaUrl)]
    public void EveryOptionalSectionAheadOfTheExtraParamsIsStillStepped(uint optional)
    {
        // A linkset child carries a parent id, a label carries text: the mirror behind them must
        // still be found, and the filler in those sections must not be read as an extra-params count.
        using var session = new GridSession();

        Assert.True(session.LatchCompressedExtraParams(Region, LocalId, MirrorBlock(optional)));

        Assert.True(session.ReflectionProbeLatched(Region, LocalId)!.Value.IsMirror);
    }

    [Fact]
    public void AnUnknownLayoutLeavesEveryPreviousAnswerAlone()
    {
        using var session = new GridSession();
        var good = Block(0, Envelope(
            (Light, LightPayload()),
            (ReflectionProbe, ProbePayload(0f, 0f, ReflectionProbeParams.FlagMirror)),
            (ExtendedMesh, MeshFlags(ExtendedMeshParams.FlagAnimatedMesh))));
        session.LatchCompressedExtraParams(Region, LocalId, good);

        // A scratch-pad object (width disputed between the decoders), a block cut off inside its
        // optional sections, one cut off inside the extra params themselves, and one too short to
        // hold a header: none of them can say anything.
        var scratchPad = Block(CompressedParticleRepair.HasScratchPad, new byte[] { 0 });
        var cutInSections = Block(CompressedParticleRepair.HasText, new byte[] { 0 })[..86];
        var cutInExtraParams = good[..90];
        var tooShort = new byte[10];

        Assert.False(session.LatchCompressedExtraParams(Region, LocalId, scratchPad));
        Assert.False(session.LatchCompressedExtraParams(Region, LocalId, cutInSections));
        Assert.False(session.LatchCompressedExtraParams(Region, LocalId, cutInExtraParams));
        Assert.False(session.LatchCompressedExtraParams(Region, LocalId, tooShort));
        Assert.False(session.LatchCompressedExtraParams(Region, LocalId, null));

        Assert.False(session.IsLightRemovedLatched(Region, LocalId));
        Assert.True(session.ReflectionProbeLatched(Region, LocalId)!.Value.IsMirror);
        Assert.True(session.IsAnimatedMeshLatched(Region, LocalId));
    }

    [Fact]
    public void AnUnknownLayoutIsReportedAsNullNotAsAnEmptyScan()
    {
        Assert.Null(GridSession.CompressedExtraParams(null));
        Assert.Null(GridSession.CompressedExtraParams(new byte[10]));
        Assert.Null(GridSession.CompressedExtraParams(
            Block(CompressedParticleRepair.HasScratchPad, new byte[] { 0 })));
    }

    [Fact]
    public void AKnownLayoutYieldsTheFullScan()
    {
        var scan = GridSession.CompressedExtraParams(Block(0, Envelope(
            (Light, LightPayload()),
            (ReflectionProbe, ProbePayload(0.25f, 2f, ReflectionProbeParams.FlagMirror)))));

        Assert.NotNull(scan);
        Assert.True(scan!.Value.Complete);
        Assert.True(scan.Value.Light);
        Assert.Equal(0.25f, scan.Value.Probe!.Value.Ambiance, 5);
        Assert.False(scan.Value.AnimatedMesh);
    }

    [Fact]
    public void TheCompressedAnimeshAnswerIsUnchangedByTheSharedWalk()
    {
        // CompressedAnimatedMesh keeps its contract: null = unknown, otherwise the flag.
        Assert.True(GridSession.CompressedAnimatedMesh(CompressedExtendedMeshTests.Compressed(
            0, CompressedExtendedMeshTests.AnimeshOn(), out _)));
        Assert.False(GridSession.CompressedAnimatedMesh(NoExtraParams()));
        Assert.Null(GridSession.CompressedAnimatedMesh(new byte[10]));
    }

    [Fact]
    public void TheSameLocalIdInAnotherRegionIsAnotherObject()
    {
        using var session = new GridSession();
        session.LatchCompressedExtraParams(1000UL, LocalId, MirrorBlock());

        Assert.Null(session.ReflectionProbeLatched(2000UL, LocalId));
        Assert.True(session.LatchCompressedExtraParams(2000UL, LocalId, MirrorBlock())); // its own first sighting
    }
}
