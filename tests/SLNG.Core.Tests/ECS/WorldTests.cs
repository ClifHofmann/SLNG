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
}
