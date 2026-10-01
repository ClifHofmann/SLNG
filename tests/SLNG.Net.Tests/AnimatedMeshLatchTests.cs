using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>
/// FEAT-ANIMESH-01: the per-object "is animesh" state <c>GridSession</c> keeps between the raw
/// packet callback that learns it and the event builder that reports it.
///
/// <para>It exists because LibreMetaverse raises its object event on a thread-pool work item
/// queued BEFORE our raw callback runs (ObjectManager.PacketHandlers.cs: the
/// <c>ThreadPool.QueueUserWorkItem</c> around <c>m_ObjectUpdate</c>), so the event can be built
/// either side of the callback's write. The latch therefore reports whether it CHANGED, and the
/// callback re-raises the update when it did -- otherwise an animesh would arrive one update
/// late, i.e. lying on its side until something else touched it.</para>
/// </summary>
public class AnimatedMeshLatchTests
{
    private const ulong RegionA = 1000UL;
    private const ulong RegionB = 2000UL;

    [Fact]
    public void AnUnseenObjectIsNotAnimesh()
    {
        using var session = new GridSession();

        Assert.False(session.IsAnimatedMeshLatched(RegionA, 5));
    }

    [Fact]
    public void LatchingTrueReportsAChange_AndASecondTimeDoesNot()
    {
        using var session = new GridSession();

        Assert.True(session.LatchAnimatedMesh(RegionA, 5, true));
        Assert.True(session.IsAnimatedMeshLatched(RegionA, 5));
        Assert.False(session.LatchAnimatedMesh(RegionA, 5, true));
        Assert.True(session.IsAnimatedMeshLatched(RegionA, 5));
    }

    [Fact]
    public void AFullUpdateWithoutTheBlockClearsIt()
    {
        // The sim omits the block when animesh is switched off; "false" must be written, not
        // skipped, or the object stays animesh for the rest of the session.
        using var session = new GridSession();
        session.LatchAnimatedMesh(RegionA, 5, true);

        Assert.True(session.LatchAnimatedMesh(RegionA, 5, false));
        Assert.False(session.IsAnimatedMeshLatched(RegionA, 5));
    }

    [Fact]
    public void FalseForAnObjectNeverSeenAnimeshIsNotAChange()
    {
        // Every ordinary prim goes through here on every update; reporting "changed" for them
        // would re-raise every object in the region.
        using var session = new GridSession();

        Assert.False(session.LatchAnimatedMesh(RegionA, 5, false));
    }

    [Fact]
    public void LocalIdsAreScopedToTheirRegion()
    {
        // LocalIDs are per region; two neighbours routinely hand out the same number.
        using var session = new GridSession();
        session.LatchAnimatedMesh(RegionA, 5, true);

        Assert.False(session.IsAnimatedMeshLatched(RegionB, 5));
        Assert.False(session.LatchAnimatedMesh(RegionB, 5, false));
        Assert.True(session.IsAnimatedMeshLatched(RegionA, 5));
    }

    [Fact]
    public void ForgettingAnObjectClearsItWithoutAnEvent()
    {
        // A killed object's LocalID can be handed to a different object. It starts clean.
        using var session = new GridSession();
        session.LatchAnimatedMesh(RegionA, 5, true);

        session.ForgetAnimatedMesh(RegionA, 5);

        Assert.False(session.IsAnimatedMeshLatched(RegionA, 5));
    }

    [Fact]
    public void ForgettingARegionClearsOnlyThatRegion()
    {
        using var session = new GridSession();
        session.LatchAnimatedMesh(RegionA, 5, true);
        session.LatchAnimatedMesh(RegionA, 6, true);
        session.LatchAnimatedMesh(RegionB, 5, true);

        session.ForgetAnimatedMeshRegion(RegionA);

        Assert.False(session.IsAnimatedMeshLatched(RegionA, 5));
        Assert.False(session.IsAnimatedMeshLatched(RegionA, 6));
        Assert.True(session.IsAnimatedMeshLatched(RegionB, 5));
    }
}
