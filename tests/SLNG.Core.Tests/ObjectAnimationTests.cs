using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using SLNG.Core.Components;
using SLNG.Core.ECS;
using SLNG.Net;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>
/// FEAT-ANIMESH-02. The simulator's <c>ObjectAnimation</c> message names a PRIM by its object UUID
/// and carries that prim's whole signaled-animation list. The world has to keep it on the prim's
/// <see cref="PrimitiveComponent"/> for the renderer, and survive the two things a UUID-addressed
/// message does that a LocalID-addressed one does not: arrive before the object it names, and
/// outlive it.
///
/// <para>Viewer behaviour being matched: <c>process_object_animation</c> (llviewermessage.cpp:4108)
/// REPLACES the list for the object id, writes it BEFORE looking the object up, and treats an empty
/// list as "stop everything". The union over a linkset's prims is the control avatar's job
/// (llcontrolavatar.cpp:559-607) and is deliberately not done here.</para>
/// </summary>
public class ObjectAnimationTests
{
    private const ulong Region = 123ul;
    private static readonly Guid Walk = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Wave = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid Idle = Guid.Parse("cccccccc-0000-0000-0000-000000000003");

    private sealed class Rig : IDisposable
    {
        public World World { get; } = new();
        public GridSession Session { get; } = new();
        public WorldSimulation Simulation { get; }
        public int PrimNotifications;

        public Rig()
        {
            Simulation = new WorldSimulation(World, Session);
            World.ComponentUpdated += (_, e) =>
            {
                if (e.Component is PrimitiveComponent) PrimNotifications++;
            };
        }

        public void Rez(uint localId, Guid objectId, ulong region = Region)
        {
            Session.RaiseObjectUpdate(new ObjectUpdateEvent(
                region, localId, Vector3.Zero, Quaternion.Identity, Vector3.One,
                1, true, Guid.NewGuid(), Guid.Empty, Guid.Empty, Vector4.One,
                ObjectId: objectId));
            Simulation.Pump();
        }

        public void Signal(Guid objectId, ulong region = Region, params SignaledAnimation[] animations)
        {
            Session.RaiseObjectAnimation(new ObjectAnimationEvent(region, objectId, animations));
            Simulation.Pump();
        }

        public void Kill(uint localId, ulong region = Region)
        {
            Session.RaiseObjectRemoved(new ObjectRemovedEvent(region, localId));
            Simulation.Pump();
        }

        public IReadOnlyList<SignaledAnimation> AnimationsOf(uint localId, ulong region = Region)
            => World.GetEntity(region, localId)!.GetComponent<PrimitiveComponent>()!.SignaledAnimations;

        public void Dispose()
        {
            Simulation.Dispose();
            Session.Dispose();
        }
    }

    private static SignaledAnimation A(Guid id, int seq = 1) => new(id, seq);

    [Fact]
    public void ANewPrimHasAnEmptyListNotNull()
    {
        using var rig = new Rig();
        rig.Rez(1, Guid.NewGuid());

        Assert.NotNull(rig.AnimationsOf(1));
        Assert.Empty(rig.AnimationsOf(1));
    }

    [Fact]
    public void TheListIsAppliedToThePrimWithThatObjectId()
    {
        using var rig = new Rig();
        var obj = Guid.NewGuid();
        rig.Rez(1, obj);
        rig.Rez(2, Guid.NewGuid());

        rig.Signal(obj, animations: new[] { A(Walk, 4), A(Wave, 9) });

        Assert.Equal(new[] { A(Walk, 4), A(Wave, 9) }, rig.AnimationsOf(1));
        Assert.Empty(rig.AnimationsOf(2)); // a different prim is untouched
    }

    [Fact]
    public void ANewMessageReplacesTheWholeListRatherThanMergingIntoIt()
    {
        using var rig = new Rig();
        var obj = Guid.NewGuid();
        rig.Rez(1, obj);

        rig.Signal(obj, animations: new[] { A(Walk), A(Wave) });
        rig.Signal(obj, animations: new[] { A(Idle, 2) });

        Assert.Equal(new[] { A(Idle, 2) }, rig.AnimationsOf(1));
    }

    [Fact]
    public void AnEmptyListMeansStopEverything()
    {
        using var rig = new Rig();
        var obj = Guid.NewGuid();
        rig.Rez(1, obj);
        rig.Signal(obj, animations: new[] { A(Walk) });

        rig.Signal(obj, animations: Array.Empty<SignaledAnimation>());

        Assert.Empty(rig.AnimationsOf(1));
    }

    [Fact]
    public void TheListIsNotDeduplicatedOrCollapsed()
    {
        // Union and "keep the larger sequence id" are the control avatar's job, across the
        // linkset; this layer hands over exactly what the sim sent for the one prim.
        using var rig = new Rig();
        var obj = Guid.NewGuid();
        rig.Rez(1, obj);

        rig.Signal(obj, animations: new[] { A(Walk, 1), A(Walk, 5) });

        Assert.Equal(new[] { A(Walk, 1), A(Walk, 5) }, rig.AnimationsOf(1));
    }

    [Fact]
    public void AMessageBeforeTheObjectExistsIsAppliedWhenItArrives()
    {
        using var rig = new Rig();
        var obj = Guid.NewGuid();

        rig.Signal(obj, animations: new[] { A(Walk, 3) });   // nothing to put it on yet
        Assert.Null(rig.World.GetEntity(Region, 1));

        rig.Rez(1, obj);

        Assert.Equal(new[] { A(Walk, 3) }, rig.AnimationsOf(1));
    }

    [Fact]
    public void ALateAppliedListIsAlreadyOnThePrimWhenListenersFirstSeeIt()
    {
        // The renderer builds the visual on the first ComponentUpdated for the prim. A list that
        // was waiting for the object must be on the component by then, not arrive in a second
        // notification a moment later (which would build the visual once without it).
        using var rig = new Rig();
        var obj = Guid.NewGuid();
        rig.Signal(obj, animations: new[] { A(Walk, 3) });

        var seen = new List<IReadOnlyList<SignaledAnimation>>();
        rig.World.ComponentUpdated += (_, e) =>
        {
            if (e.Component is PrimitiveComponent p) seen.Add(p.SignaledAnimations);
        };
        rig.Rez(1, obj);

        Assert.NotEmpty(seen);
        Assert.All(seen, list => Assert.Equal(new[] { A(Walk, 3) }, list));
    }

    [Fact]
    public void ALaterPendingMessageReplacesAnEarlierPendingOne()
    {
        using var rig = new Rig();
        var obj = Guid.NewGuid();

        rig.Signal(obj, animations: new[] { A(Walk) });
        rig.Signal(obj, animations: new[] { A(Wave, 2) });
        rig.Rez(1, obj);

        Assert.Equal(new[] { A(Wave, 2) }, rig.AnimationsOf(1));
    }

    [Fact]
    public void APendingListIsConsumedByTheObjectItWasHeldFor()
    {
        // Rezzing the same UUID a second time (after the first copy is gone) must not resurrect
        // a list that was already handed over once.
        using var rig = new Rig();
        var obj = Guid.NewGuid();
        rig.Signal(obj, animations: new[] { A(Walk) });
        rig.Rez(1, obj);
        rig.Kill(1);

        rig.Rez(2, obj);

        Assert.Empty(rig.AnimationsOf(2));
    }

    [Fact]
    public void ARemovedObjectForgetsItsAnimations()
    {
        using var rig = new Rig();
        var obj = Guid.NewGuid();
        rig.Rez(1, obj);
        rig.Signal(obj, animations: new[] { A(Walk) });
        rig.Kill(1);
        Assert.Null(rig.World.GetEntity(Region, 1));

        // The same object comes back (out of range and in again): no stale list on it.
        rig.Rez(1, obj);

        Assert.Empty(rig.AnimationsOf(1));
    }

    [Fact]
    public void AMessageForAnObjectThatIsGoneIsHeldNotAppliedToWhoeverReusesItsLocalId()
    {
        // LocalIDs are recycled; the message names a UUID. The index from UUID to entity must not
        // hand object A's animations to object B that now sits on A's old LocalID.
        using var rig = new Rig();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        rig.Rez(5, a);
        rig.Kill(5);
        rig.Rez(5, b);

        rig.Signal(a, animations: new[] { A(Walk) });

        Assert.Empty(rig.AnimationsOf(5));
    }

    [Fact]
    public void AnObjectThatCameBackUnderANewLocalIdIsFoundThereEvenAfterTheOldOneIsKilled()
    {
        // The same UUID shows up under a second LocalID before the first copy's kill is processed
        // (a re-rez, a region hand-over). The index has moved to the new one; removing the OLD
        // entity must not take the new entry with it.
        using var rig = new Rig();
        var obj = Guid.NewGuid();
        rig.Rez(1, obj);
        rig.Rez(2, obj);

        rig.Kill(1);
        rig.Signal(obj, animations: new[] { A(Walk, 2) });

        Assert.Equal(new[] { A(Walk, 2) }, rig.AnimationsOf(2));
    }

    [Fact]
    public void ARegionDisconnectForgetsAnAppliedList()
    {
        using var rig = new Rig();
        var obj = Guid.NewGuid();
        rig.Rez(1, obj);
        rig.Signal(obj, animations: new[] { A(Walk) });

        rig.Session.RaiseRegionDisconnected(new RegionDisconnectedEvent(Region));
        rig.Simulation.Pump();
        Assert.Null(rig.World.GetEntity(Region, 1));

        rig.Rez(1, obj);

        Assert.Empty(rig.AnimationsOf(1));
    }

    [Fact]
    public void ARegionDisconnectForgetsOnlyThatRegionsPendingLists()
    {
        using var rig = new Rig();
        var inGoneRegion = Guid.NewGuid();
        var inOtherRegion = Guid.NewGuid();
        rig.Signal(inGoneRegion, Region, A(Walk));
        rig.Signal(inOtherRegion, 999ul, A(Wave));

        rig.Session.RaiseRegionDisconnected(new RegionDisconnectedEvent(Region));
        rig.Simulation.Pump();

        rig.Rez(1, inGoneRegion);
        rig.Rez(2, inOtherRegion, region: 999ul);
        Assert.Empty(rig.AnimationsOf(1));
        Assert.Equal(new[] { A(Wave) }, rig.AnimationsOf(2, 999ul));
    }

    [Fact]
    public void ASessionEndKeepsWhatIsStillOnScreen()
    {
        // BUG-NET-24: a region the whole session took with it stays as it was.
        using var rig = new Rig();
        var obj = Guid.NewGuid();
        rig.Rez(1, obj);
        rig.Signal(obj, animations: new[] { A(Walk) });

        rig.Session.RaiseRegionDisconnected(new RegionDisconnectedEvent(Region, SessionEnded: true));
        rig.Simulation.Pump();

        Assert.Equal(new[] { A(Walk) }, rig.AnimationsOf(1));
    }

    [Fact]
    public void UnloadingEveryRegionForgetsPendingListsToo()
    {
        using var rig = new Rig();
        var obj = Guid.NewGuid();
        rig.Signal(obj, animations: new[] { A(Walk) });

        rig.Simulation.UnloadAllRegions();
        rig.Rez(1, obj);

        Assert.Empty(rig.AnimationsOf(1));
    }

    [Fact]
    public void AnIdenticalListDoesNotNotifyAgain()
    {
        // The sim re-sends a prim's list whenever anything about its animation state changes;
        // an unchanged list must not make the renderer re-run for every such message.
        using var rig = new Rig();
        var obj = Guid.NewGuid();
        rig.Rez(1, obj);
        rig.Signal(obj, animations: new[] { A(Walk, 2) });
        int afterFirst = rig.PrimNotifications;

        rig.Signal(obj, animations: new[] { A(Walk, 2) });
        Assert.Equal(afterFirst, rig.PrimNotifications);

        rig.Signal(obj, animations: new[] { A(Walk, 3) }); // a new sequence id restarts it: a change
        Assert.Equal(afterFirst + 1, rig.PrimNotifications);
    }

    [Fact]
    public void AnEmptyListForAnObjectAlreadyEmptyDoesNotNotify()
    {
        using var rig = new Rig();
        var obj = Guid.NewGuid();
        rig.Rez(1, obj);
        int before = rig.PrimNotifications;

        rig.Signal(obj, animations: Array.Empty<SignaledAnimation>());

        Assert.Equal(before, rig.PrimNotifications);
    }

    [Fact]
    public void TheComponentOwnsItsCopyOfTheList()
    {
        using var rig = new Rig();
        var obj = Guid.NewGuid();
        rig.Rez(1, obj);
        var source = new[] { A(Walk, 1) };
        rig.Signal(obj, animations: source);

        source[0] = A(Wave, 7);

        Assert.Equal(new[] { A(Walk, 1) }, rig.AnimationsOf(1));
    }

    [Fact]
    public void AnObjectUpdateDoesNotWipeTheList()
    {
        using var rig = new Rig();
        var obj = Guid.NewGuid();
        rig.Rez(1, obj);
        rig.Signal(obj, animations: new[] { A(Walk) });

        rig.Rez(1, obj);                                   // the next full update for the prim
        rig.Session.RaiseObjectUpdate(new ObjectUpdateEvent(
            Region, 1, Vector3.One, Quaternion.Identity, Vector3.One,
            1, true, Guid.NewGuid(), Guid.Empty, Guid.Empty, Vector4.One,
            ObjectId: obj, IsFullUpdate: false));          // and a terse one
        rig.Simulation.Pump();

        Assert.Equal(new[] { A(Walk) }, rig.AnimationsOf(1));
    }

    [Fact]
    public void AnEmptyObjectIdIsIgnored()
    {
        using var rig = new Rig();
        rig.Rez(1, Guid.Empty);                            // an object the sim never named

        rig.Signal(Guid.Empty, animations: new[] { A(Walk) });

        Assert.Empty(rig.AnimationsOf(1));
    }

    [Fact]
    public void PendingListsAreBounded()
    {
        // An object that never materialises must not pin its list forever. Past the limit the
        // OLDEST held list is the one given up.
        using var rig = new Rig();
        int limit = WorldSimulation.PendingObjectAnimationLimit;
        var ids = Enumerable.Range(0, limit + 5).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var id in ids) rig.Signal(id, animations: new[] { A(Walk) });

        rig.Rez(1, ids[0]);                                // oldest: given up
        rig.Rez(2, ids[4]);                                // the last one given up (5 over the limit)
        rig.Rez(3, ids[5]);                                // the oldest still held
        rig.Rez(4, ids[^1]);                               // newest

        Assert.Empty(rig.AnimationsOf(1));
        Assert.Empty(rig.AnimationsOf(2));
        Assert.Equal(new[] { A(Walk) }, rig.AnimationsOf(3));
        Assert.Equal(new[] { A(Walk) }, rig.AnimationsOf(4));
    }
}
