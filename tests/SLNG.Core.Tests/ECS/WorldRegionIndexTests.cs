using System;
using System.Collections.Generic;
using System.Linq;
using SLNG.Core.Components;
using SLNG.Core.ECS;
using Xunit;

namespace SLNG.Core.Tests.ECS;

/// <summary>
/// BUG-PERF-11: <see cref="World.RemoveRegion"/> walks the entities of the region it unloads - not the
/// whole world, several times over, as it used to - and the local agent is found through a validated
/// cache. The behaviour that must not change (what is kept, what is marked) is pinned in WorldTests;
/// these pin the index that now carries it and the cache.
/// </summary>
public class WorldRegionIndexTests
{
    private static Entity AddAgent(World world, ulong region, uint localId, bool local = true)
    {
        var agent = world.GetOrCreateEntity(region, localId);
        agent.SetComponent(new AvatarComponent(Guid.NewGuid(), "Self", "Resident", isLocalAgent: local));
        return agent;
    }

    [Fact]
    public void RemoveRegion_TakesTheRegionsEntitiesAndLeavesTheOthers()
    {
        var world = new World();
        const ulong a = 1000ul, b = 2000ul;
        for (uint i = 1; i <= 50; i++) world.GetOrCreateEntity(a, i).SetComponent(new TransformComponent());
        for (uint i = 1; i <= 70; i++) world.GetOrCreateEntity(b, i).SetComponent(new TransformComponent());

        world.RemoveRegion(a);

        Assert.Equal(70, world.EntityCount);
        Assert.Null(world.GetEntity(a, 1));
        Assert.NotNull(world.GetEntity(b, 70));

        world.RemoveRegion(a); // again: nothing to take, nothing thrown
        Assert.Equal(70, world.EntityCount);
    }

    [Fact]
    public void RemoveRegion_FollowsAnEntityThatWasRekeyedIntoAnotherRegion()
    {
        var world = new World();
        const ulong from = 1000ul, to = 2000ul;
        var moved = world.GetOrCreateEntity(from, 5);
        moved.SetComponent(new TransformComponent());
        var stays = world.GetOrCreateEntity(from, 6);
        stays.SetComponent(new TransformComponent());

        Assert.True(world.RekeyEntity(moved, to, 9));

        world.RemoveRegion(from);
        Assert.Null(world.GetEntity(from, 6));
        Assert.Same(moved, world.GetEntity(to, 9));   // it lives in the other region now

        world.RemoveRegion(to);
        Assert.Null(world.GetEntity(to, 9));
        Assert.Equal(0, world.EntityCount);
    }

    [Fact]
    public void RemoveRegion_AfterEntitiesWereRemovedOneByOne_RemovesOnlyWhatIsLeft()
    {
        var world = new World();
        const ulong region = 1000ul;
        for (uint i = 1; i <= 10; i++) world.GetOrCreateEntity(region, i).SetComponent(new TransformComponent());
        for (uint i = 1; i <= 5; i++) world.RemoveEntity(region, i);

        var removed = new List<uint>();
        world.EntityRemoved += (_, e) => removed.Add(e.Entity.LocalId);
        world.RemoveRegion(region);

        Assert.Equal(new uint[] { 6, 7, 8, 9, 10 }, removed.OrderBy(x => x).ToArray());
        Assert.Equal(0, world.EntityCount);
    }

    [Fact]
    public void RemoveRegion_KeepsTheWholeTreeUnderTheAgent_ThroughSeveralLevels()
    {
        var world = new World();
        const ulong region = 1000ul;
        var agent = AddAgent(world, region, 10);

        // agent <- attachment root 20 <- child 21 <- grandchild 22 ; and a HUD child hanging straight off the agent.
        var root = world.GetOrCreateEntity(region, 20);
        root.SetComponent(new AttachmentComponent(agent.Id, 2));
        root.SetComponent(new TransformComponent { ParentLocalId = 10 });
        world.GetOrCreateEntity(region, 21).SetComponent(new TransformComponent { ParentLocalId = 20 });
        world.GetOrCreateEntity(region, 22).SetComponent(new TransformComponent { ParentLocalId = 21 });
        world.GetOrCreateEntity(region, 23).SetComponent(new TransformComponent { ParentLocalId = 10 });

        // Not the agent's: an ordinary linkset with the same shape, and an attachment of somebody else.
        world.GetOrCreateEntity(region, 30).SetComponent(new TransformComponent());
        world.GetOrCreateEntity(region, 31).SetComponent(new TransformComponent { ParentLocalId = 30 });
        var other = AddAgent(world, region, 40, local: false);
        world.GetOrCreateEntity(region, 41).SetComponent(new AttachmentComponent(other.Id, 1));

        world.RemoveRegion(region);

        foreach (uint kept in new uint[] { 10, 20, 21, 22, 23 })
            Assert.NotNull(world.GetEntity(region, kept));
        foreach (uint gone in new uint[] { 30, 31, 40, 41 })
            Assert.Null(world.GetEntity(region, gone));

        foreach (uint marked in new uint[] { 20, 21, 22, 23 })
            Assert.True(world.GetEntity(region, marked)!.GetComponent<AttachmentComponent>()!.AwaitingReconfirmation);
    }

    [Fact]
    public void RemoveRegion_LeavesAnAttachmentInAnotherRegionAlone()
    {
        var world = new World();
        const ulong left = 1000ul, here = 2000ul;
        var agent = AddAgent(world, here, 10);
        var att = world.GetOrCreateEntity(here, 20);
        att.SetComponent(new AttachmentComponent(agent.Id, 2));
        world.GetOrCreateEntity(left, 1).SetComponent(new TransformComponent());

        world.RemoveRegion(left);

        Assert.NotNull(world.GetEntity(here, 10));
        Assert.False(att.GetComponent<AttachmentComponent>()!.AwaitingReconfirmation);   // not this region's business
    }

    [Fact]
    public void RemoveRegion_DoesNotWalkTheWorldOnceTheAgentIsKnown()
    {
        var world = new World();
        const ulong a = 1000ul, b = 2000ul;
        var agent = AddAgent(world, a, 1);
        for (uint i = 2; i <= 2000; i++) world.GetOrCreateEntity(a, i).SetComponent(new TransformComponent());
        for (uint i = 1; i <= 2000; i++) world.GetOrCreateEntity(b, i).SetComponent(new TransformComponent());
        Assert.Same(agent, world.FindLocalAgent());   // the one walk

        long before = world.FullScans;
        world.RemoveRegion(b);
        world.RemoveRegion(a);

        Assert.Equal(before, world.FullScans);
        Assert.Equal(1, world.EntityCount);   // the agent
    }

    [Fact]
    public void FindLocalAgent_FollowsTheAgentAcrossRemovalAndReplacement()
    {
        var world = new World();
        const ulong region = 1000ul;
        Assert.Null(world.FindLocalAgent());

        var first = AddAgent(world, region, 1);
        Assert.Same(first, world.FindLocalAgent());
        Assert.Same(first, world.FindLocalAgent());

        // Un-flagged: no longer the agent, whatever the cache remembers.
        first.GetComponent<AvatarComponent>()!.IsLocalAgent = false;
        Assert.Null(world.FindLocalAgent());

        var second = AddAgent(world, region, 2);
        Assert.Same(second, world.FindLocalAgent());

        world.RemoveEntity(region, 2);
        Assert.Null(world.FindLocalAgent());   // gone from the world: not served from the cache
    }
}
