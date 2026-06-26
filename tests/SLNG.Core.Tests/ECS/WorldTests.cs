using System.Linq;
using SLNG.Core.ECS;
using SLNG.Core.Components;
using Xunit;
using System.Numerics;

namespace SLNG.Core.Tests.ECS;

public class WorldTests
{
    [Fact]
    public void World_GetOrCreateEntity_ReturnsSameEntityForSameId()
    {
        var world = new World();
        var entity1 = world.GetOrCreateEntity(123);
        var entity2 = world.GetOrCreateEntity(123);
        
        Assert.Same(entity1, entity2);
        Assert.Equal(123u, entity1.LocalId);
    }

    [Fact]
    public void World_Query_FindsEntitiesWithSpecificComponent()
    {
        var world = new World();
        
        var e1 = world.GetOrCreateEntity(1);
        e1.SetComponent(new TransformComponent());

        var e2 = world.GetOrCreateEntity(2);
        e2.SetComponent(new TransformComponent());
        e2.SetComponent(new MetadataComponent());

        var e3 = world.GetOrCreateEntity(3);
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
    public void World_Events_AreFiredOnModifications()
    {
        var world = new World();
        bool addedFired = false;
        bool removedFired = false;

        world.EntityAdded += (sender, e) => { addedFired = true; };
        world.EntityRemoved += (sender, e) => { removedFired = true; };

        world.GetOrCreateEntity(1);
        Assert.True(addedFired);

        world.RemoveEntity(1);
        Assert.True(removedFired);
    }
}
