using System.Linq;
using System.Numerics;
using SLNG.Core.Components;
using SLNG.Core.ECS;
using Xunit;

namespace SLNG.Core.Tests.ECS;

public class WorldTests
{
    [Fact]
    public void World_GetOrCreateEntity_ReturnsSameEntityForSameId()
    {
        var world = new World();
        var entity1 = world.GetOrCreateEntity(123ul, 123);
        var entity2 = world.GetOrCreateEntity(123ul, 123);

        Assert.Same(entity1, entity2);
        Assert.Equal(123u, entity1.LocalId);
        Assert.Equal(123ul, entity1.RegionHandle);
    }

    [Fact]
    public void World_Query_FindsEntitiesWithSpecificComponent()
    {
        var world = new World();

        var e1 = world.GetOrCreateEntity(123ul, 1);
        e1.SetComponent(new TransformComponent());

        var e2 = world.GetOrCreateEntity(123ul, 2);
        e2.SetComponent(new TransformComponent());
        e2.SetComponent(new MetadataComponent());

        var e3 = world.GetOrCreateEntity(123ul, 3);
        e3.SetComponent(new MetadataComponent());

        var transformEntities = world.Query<TransformComponent>().ToList();
        var metadataEntities = world.Query<MetadataComponent>().ToList();

        Assert.Equal(2, transformEntities.Count);
        Assert.Contains(e1, transformEntities);
        Assert.Contains(e2, transformEntities);

        Assert.Equal(2, metadataEntities.Count);
        Assert.Contains(e2, metadataEntities);
        Assert.Contains(e3, metadataEntities);
    }

    [Fact]
    public void RemoveEntity_RemovesFromWorld_AndTriggersEvent()
    {
        var world = new World();
        var entity = world.GetOrCreateEntity(123ul, 42);

        bool eventFired = false;
        world.EntityRemoved += (s, e) =>
        {
            if (e.Entity.LocalId == 42) eventFired = true;
        };

        world.RemoveEntity(123ul, 42);

        Assert.Null(world.GetEntity(123ul, 42));
        Assert.True(eventFired);
    }

    // BUG-NET-13: the eager teleport cleanup runs RemoveRegion on the region we just left, while
    // the local agent is still keyed to it (the destination sim's first local AvatarUpdate hasn't
    // arrived yet). Destroying the agent entity there blanked the self avatar. RemoveRegion must
    // take everything in the region EXCEPT the local agent.
    [Fact]
    public void RemoveRegion_RemovesRegionContent_ButPreservesTheLocalAgent()
    {
        var world = new World();
        const ulong region = 741070837455616ul;

        var prim = world.GetOrCreateEntity(region, 10);
        prim.SetComponent(new TransformComponent());

        var remoteAvatar = world.GetOrCreateEntity(region, 20);
        remoteAvatar.SetComponent(new AvatarComponent(System.Guid.NewGuid(), "Remote", "Resident", isLocalAgent: false));

        var selfAvatar = world.GetOrCreateEntity(region, 30);
        selfAvatar.SetComponent(new AvatarComponent(System.Guid.NewGuid(), "Self", "Resident", isLocalAgent: true));

        world.GetOrCreateTerrain(region);

        var removed = new System.Collections.Generic.List<uint>();
        world.EntityRemoved += (s, e) => removed.Add(e.Entity.LocalId);

        world.RemoveRegion(region);

        Assert.Null(world.GetEntity(region, 10));   // ordinary prim gone
        Assert.Null(world.GetEntity(region, 20));   // remote avatar gone
        Assert.NotNull(world.GetEntity(region, 30)); // local agent preserved
        Assert.DoesNotContain(30u, removed);
        Assert.Contains(10u, removed);
        Assert.Contains(20u, removed);
        Assert.False(world.Terrains.ContainsKey(region)); // terrain still unloaded
    }

    [Fact]
    public void RemoveRegion_WithNoLocalAgent_RemovesEverything()
    {
        var world = new World();
        const ulong region = 999ul;
        world.GetOrCreateEntity(region, 1).SetComponent(new TransformComponent());
        world.GetOrCreateEntity(region, 2).SetComponent(
            new AvatarComponent(System.Guid.NewGuid(), "A", "B", isLocalAgent: false));

        world.RemoveRegion(region);

        Assert.Equal(0, world.EntityCount);
    }
}
