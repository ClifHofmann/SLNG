using LibreMetaverse;
using LibreMetaverse.Packets;
using SLNG.Core;
using SLNG.Net;
using Xunit;
using static SLNG.Net.Tests.ExtraParamsBytes;

namespace SLNG.Net.Tests;

/// <summary>
/// BUG-NET-25: the Light and Reflection-Probe latches. Both were keyed by LocalId alone (LocalIds
/// are per region, and a neighbour -- MultipleSims -- reuses the same numbers), never pruned, fed
/// only from ObjectUpdate, and read with a one-update-late race. They now follow the animated-mesh
/// latch: keyed by (region, LocalId), holding only the objects that ever showed the block,
/// reporting whether the stored answer CHANGED so the callback can re-raise once, and pruned with
/// the object and with the region.
///
/// <para>What the Light latch MEANS: "the most recent raw ExtraParams positively had no Light
/// block". LibreMetaverse only ever writes <c>Primitive.Light</c> and never clears it, so that is
/// the one fact the library cannot give us. It matters only for an object that once showed a light
/// -- every other object's <c>Primitive.Light</c> is already the default -- so only those objects
/// are held, and "no entry" keeps meaning "trust the Primitive".</para>
/// </summary>
public class ExtraParamsLatchTests
{
    private const ulong RegionA = 1000UL;
    private const ulong RegionB = 2000UL;

    private static readonly ReflectionProbeParams Mirror = new(0.5f, 1.5f, ReflectionProbeParams.FlagMirror);
    private static readonly ReflectionProbeParams Plain = new(0.5f, 1.5f, ReflectionProbeParams.FlagBoxVolume);

    private static ObjectUpdatePacket.ObjectDataBlock Block(uint id, byte[]? extra) => new() { ID = id, ExtraParams = extra! };

    // --- Light ---------------------------------------------------------------------------------

    [Fact]
    public void AnObjectNeverSeenWithALightIsNotHeldAtAll()
    {
        // Every ordinary prim passes through on every update; none of them belongs in the map.
        using var session = new GridSession();

        Assert.False(session.LatchLight(RegionA, 5, present: false));
        Assert.False(session.IsLightRemovedLatched(RegionA, 5));
    }

    [Fact]
    public void ALightAppearingIsAChange_AndRepeatingItIsNot()
    {
        using var session = new GridSession();

        Assert.True(session.LatchLight(RegionA, 5, present: true));
        Assert.False(session.LatchLight(RegionA, 5, present: true));
        Assert.False(session.IsLightRemovedLatched(RegionA, 5));
    }

    [Fact]
    public void ALightGoingAwayIsAChange_AndStaysRemovedUntilItReturns()
    {
        using var session = new GridSession();
        session.LatchLight(RegionA, 5, present: true);

        Assert.True(session.LatchLight(RegionA, 5, present: false));
        Assert.True(session.IsLightRemovedLatched(RegionA, 5));
        Assert.False(session.LatchLight(RegionA, 5, present: false));   // still off: no change

        Assert.True(session.LatchLight(RegionA, 5, present: true));     // switched back on
        Assert.False(session.IsLightRemovedLatched(RegionA, 5));
    }

    [Fact]
    public void TheSameLocalIdInTwoRegionsHoldsTwoDifferentLightAnswers()
    {
        using var session = new GridSession();
        session.LatchLight(RegionA, 5, present: true);
        session.LatchLight(RegionB, 5, present: true);

        session.LatchLight(RegionA, 5, present: false);

        Assert.True(session.IsLightRemovedLatched(RegionA, 5));
        Assert.False(session.IsLightRemovedLatched(RegionB, 5));
    }

    [Fact]
    public void ALightRemovedInOneRegionDoesNotSwitchOffANeighboursObjectWithTheSameLocalId()
    {
        // The visible symptom of the old LocalId-only key: region A's prim 5 loses its light and
        // region B's unrelated prim 5, light on, is reported dark.
        using var session = new GridSession();
        session.LatchLight(RegionA, 5, present: true);
        session.LatchLight(RegionA, 5, present: false);
        var neighbourLight = new Primitive.LightData { Intensity = 1f };

        var effective = session.EffectiveLight(RegionB, 5, neighbourLight);

        Assert.Same(neighbourLight, effective);
    }

    [Fact]
    public void ARemovedLightIsReportedOff_WithoutTouchingTheLibrarysObject()
    {
        // LibreMetaverse's Primitive.Light is stale-on after the block disappears; the event must
        // say off. The shared object is NOT rewritten: a re-enable arriving while a stale read is
        // in flight would otherwise be wiped, and the re-raise could never bring it back.
        using var session = new GridSession();
        session.LatchLight(RegionA, 5, present: true);
        session.LatchLight(RegionA, 5, present: false);
        var stale = new Primitive.LightData { Intensity = 0.8f, Radius = 6f };

        var effective = session.EffectiveLight(RegionA, 5, stale);

        Assert.Equal(0f, effective.Intensity);
        Assert.NotSame(stale, effective);
        Assert.Equal(0.8f, stale.Intensity);
        Assert.Equal(6f, stale.Radius);
    }

    [Fact]
    public void ALightThatIsOnIsLeftAlone()
    {
        using var session = new GridSession();
        session.LatchLight(RegionA, 5, present: true);
        var live = new Primitive.LightData { Intensity = 0.8f };

        Assert.Same(live, session.EffectiveLight(RegionA, 5, live));
        Assert.Same(live, session.EffectiveLight(RegionA, 99, live)); // and so is an unknown object
    }

    // --- Reflection probe ----------------------------------------------------------------------

    [Fact]
    public void AnObjectWithNoProbeIsNotHeldAndReadsNull()
    {
        using var session = new GridSession();

        Assert.False(session.LatchReflectionProbe(RegionA, 5, null));
        Assert.Null(session.ReflectionProbeLatched(RegionA, 5));
    }

    [Fact]
    public void AProbeAppearingChangingAndGoingAwayAreEachOneChange()
    {
        using var session = new GridSession();

        Assert.True(session.LatchReflectionProbe(RegionA, 5, Mirror));
        Assert.Equal(Mirror, session.ReflectionProbeLatched(RegionA, 5));
        Assert.False(session.LatchReflectionProbe(RegionA, 5, Mirror));     // same again: none

        Assert.True(session.LatchReflectionProbe(RegionA, 5, Plain));       // mirror -> ordinary probe
        Assert.Equal(Plain, session.ReflectionProbeLatched(RegionA, 5));

        Assert.True(session.LatchReflectionProbe(RegionA, 5, null));        // block gone
        Assert.Null(session.ReflectionProbeLatched(RegionA, 5));
        Assert.False(session.LatchReflectionProbe(RegionA, 5, null));       // gone already: none
    }

    [Fact]
    public void TheSameLocalIdInTwoRegionsHoldsTwoDifferentProbes()
    {
        using var session = new GridSession();
        session.LatchReflectionProbe(RegionA, 5, Mirror);
        session.LatchReflectionProbe(RegionB, 5, Plain);

        Assert.Equal(Mirror, session.ReflectionProbeLatched(RegionA, 5));
        Assert.Equal(Plain, session.ReflectionProbeLatched(RegionB, 5));
    }

    // --- Pruning -------------------------------------------------------------------------------

    [Fact]
    public void ForgettingAnObjectClearsAllThreeKindsForThatObjectOnly()
    {
        using var session = new GridSession();
        foreach (uint id in new uint[] { 5, 6 })
        {
            session.LatchLight(RegionA, id, present: true);
            session.LatchLight(RegionA, id, present: false);
            session.LatchReflectionProbe(RegionA, id, Mirror);
            session.LatchAnimatedMesh(RegionA, id, true);
        }

        session.ForgetObjectLatches(RegionA, 5);

        Assert.False(session.IsLightRemovedLatched(RegionA, 5));
        Assert.Null(session.ReflectionProbeLatched(RegionA, 5));
        Assert.False(session.IsAnimatedMeshLatched(RegionA, 5));
        Assert.True(session.IsLightRemovedLatched(RegionA, 6));
        Assert.Equal(Mirror, session.ReflectionProbeLatched(RegionA, 6));
        Assert.True(session.IsAnimatedMeshLatched(RegionA, 6));
    }

    [Fact]
    public void ForgettingARegionClearsAllThreeKindsForThatRegionOnly()
    {
        using var session = new GridSession();
        foreach (ulong region in new[] { RegionA, RegionB })
        {
            session.LatchLight(region, 5, present: true);
            session.LatchLight(region, 5, present: false);
            session.LatchReflectionProbe(region, 5, Mirror);
            session.LatchAnimatedMesh(region, 5, true);
        }

        session.ForgetObjectLatchesRegion(RegionA);

        Assert.False(session.IsLightRemovedLatched(RegionA, 5));
        Assert.Null(session.ReflectionProbeLatched(RegionA, 5));
        Assert.False(session.IsAnimatedMeshLatched(RegionA, 5));
        Assert.True(session.IsLightRemovedLatched(RegionB, 5));
        Assert.Equal(Mirror, session.ReflectionProbeLatched(RegionB, 5));
        Assert.True(session.IsAnimatedMeshLatched(RegionB, 5));
    }

    [Fact]
    public void AForgottenObjectStartsCleanSoAReusedLocalIdInheritsNothing()
    {
        using var session = new GridSession();
        session.LatchLight(RegionA, 5, present: true);
        session.LatchLight(RegionA, 5, present: false);
        session.ForgetObjectLatches(RegionA, 5);
        var newObjectsLight = new Primitive.LightData { Intensity = 1f };

        Assert.Same(newObjectsLight, session.EffectiveLight(RegionA, 5, newObjectsLight));
        Assert.True(session.LatchLight(RegionA, 5, present: true)); // a first sighting again
    }

    // --- A whole ObjectUpdate packet -----------------------------------------------------------

    [Fact]
    public void AnObjectUpdatePacketLatchesEveryBlockAndNamesTheObjectsThatChanged()
    {
        using var session = new GridSession();
        var changed = session.LatchObjectUpdateBlocks(RegionA, new[]
        {
            Block(1, Envelope((Light, LightPayload()))),
            Block(2, Envelope((ReflectionProbe, ProbePayload(0f, 0f, ReflectionProbeParams.FlagMirror)))),
            Block(3, Envelope((Flexible, new byte[16]))),            // nothing of interest
            Block(4, Envelope((ExtendedMesh, MeshFlags(ExtendedMeshParams.FlagAnimatedMesh)))),
        });

        Assert.Equal(new uint[] { 1, 2, 4 }, changed);
        Assert.False(session.LatchLight(RegionA, 1, present: true));   // already latched on: no change
        Assert.NotNull(session.ReflectionProbeLatched(RegionA, 2));
        Assert.True(session.IsAnimatedMeshLatched(RegionA, 4));
    }

    [Fact]
    public void AnUnchangedPacketNamesNobody_SoNothingIsRaisedAgain()
    {
        using var session = new GridSession();
        var blocks = new[]
        {
            Block(1, Envelope((Light, LightPayload()), (ReflectionProbe, ProbePayload(0f, 0f, ReflectionProbeParams.FlagMirror)))),
            Block(2, Envelope((Flexible, new byte[16]))),
        };
        session.LatchObjectUpdateBlocks(RegionA, blocks);

        Assert.Empty(session.LatchObjectUpdateBlocks(RegionA, blocks));
    }

    [Fact]
    public void SeveralKindsChangingOnOneObjectNameItOnce()
    {
        // One re-raise per object per packet, however many of its latches moved.
        using var session = new GridSession();
        var changed = session.LatchObjectUpdateBlocks(RegionA, new[]
        {
            Block(7, Envelope(
                (Light, LightPayload()),
                (ReflectionProbe, ProbePayload(0f, 0f, ReflectionProbeParams.FlagMirror)),
                (ExtendedMesh, MeshFlags(ExtendedMeshParams.FlagAnimatedMesh)))),
        });

        Assert.Equal(new uint[] { 7 }, changed);
    }

    [Fact]
    public void TheSameObjectTwiceInOnePacketIsNamedOnce()
    {
        using var session = new GridSession();
        var changed = session.LatchObjectUpdateBlocks(RegionA, new[]
        {
            Block(7, Envelope((Light, LightPayload()))),
            Block(7, Envelope((Light, LightPayload()), (ReflectionProbe, ProbePayload(0f, 0f, 0)))),
        });

        Assert.Equal(new uint[] { 7 }, changed);
    }

    [Fact]
    public void ALightSwitchedOffIsNamedOnTheUpdateThatOmitsTheBlock()
    {
        using var session = new GridSession();
        session.LatchObjectUpdateBlocks(RegionA, new[] { Block(1, Envelope((Light, LightPayload()))) });

        var changed = session.LatchObjectUpdateBlocks(RegionA, new[] { Block(1, new byte[] { 0 }) });

        Assert.Equal(new uint[] { 1 }, changed);
        Assert.True(session.IsLightRemovedLatched(RegionA, 1));
    }

    [Fact]
    public void AHostileBlockDoesNotStopTheRestOfThePacketBeingLatched()
    {
        // The bug: the first block's scan threw, and blocks 2 and 3 never reached the latches.
        using var session = new GridSession();
        var changed = session.LatchObjectUpdateBlocks(RegionA, new[]
        {
            Block(1, Raw(1, EntryWithDeclaredSize(0x55, int.MinValue))),
            Block(2, Envelope((Light, LightPayload()))),
            Block(3, Envelope((ReflectionProbe, ProbePayload(0f, 0f, ReflectionProbeParams.FlagMirror)))),
        });

        Assert.Equal(new uint[] { 2, 3 }, changed);
    }

    [Fact]
    public void AMalformedBlockLeavesThePreviousAnswersAlone()
    {
        // "No light" can only be claimed after reading the whole block; a block that stops
        // making sense must not switch a light off, un-mirror a mirror, or stand an animesh up.
        using var session = new GridSession();
        session.LatchObjectUpdateBlocks(RegionA, new[]
        {
            Block(1, Envelope(
                (Light, LightPayload()),
                (ReflectionProbe, ProbePayload(0f, 0f, ReflectionProbeParams.FlagMirror)),
                (ExtendedMesh, MeshFlags(ExtendedMeshParams.FlagAnimatedMesh)))),
        });

        var changed = session.LatchObjectUpdateBlocks(RegionA, new[]
        {
            Block(1, Raw(1, EntryWithDeclaredSize(0x55, int.MinValue))),
        });

        Assert.Empty(changed);
        Assert.False(session.IsLightRemovedLatched(RegionA, 1));
        Assert.NotNull(session.ReflectionProbeLatched(RegionA, 1));
        Assert.True(session.IsAnimatedMeshLatched(RegionA, 1));
    }

    [Fact]
    public void ANullExtraParamsFieldIsAFirmNoBlocks()
    {
        using var session = new GridSession();
        session.LatchObjectUpdateBlocks(RegionA, new[] { Block(1, Envelope((Light, LightPayload()))) });

        var changed = session.LatchObjectUpdateBlocks(RegionA, new[] { Block(1, null) });

        Assert.Equal(new uint[] { 1 }, changed);
        Assert.True(session.IsLightRemovedLatched(RegionA, 1));
    }
}
