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

    [Fact]
    public void RekeyEntity_MovesEntityToNewKey_PreservesIdAndComponents_AndFiresEntityRekeyed()
    {
        var world = new World();
        const ulong oldRegion = 100ul;
        const ulong newRegion = 200ul;
        const uint oldLocal = 42u;
        const uint newLocal = 84u;

        var entity = world.GetOrCreateEntity(oldRegion, oldLocal);
        var originalGuid = entity.Id;
        var transform = new TransformComponent { Position = new Vector3(1, 2, 3) };
        entity.SetComponent(transform);

        EntityRekeyedEventArgs? receivedArgs = null;
        world.EntityRekeyed += (s, e) => receivedArgs = e;

        bool rekeyed = world.RekeyEntity(entity, newRegion, newLocal);

        Assert.True(rekeyed);
        Assert.Equal(newRegion, entity.RegionHandle);
        Assert.Equal(newLocal, entity.LocalId);
        Assert.Equal(originalGuid, entity.Id);
        Assert.Same(transform, entity.GetComponent<TransformComponent>());

        // Key lookups
        Assert.Null(world.GetEntity(oldRegion, oldLocal));
        Assert.Same(entity, world.GetEntity(newRegion, newLocal));
        Assert.Same(entity, world.GetEntity(originalGuid));

        // Event
        Assert.NotNull(receivedArgs);
        Assert.Same(entity, receivedArgs.Entity);
        Assert.Equal(oldRegion, receivedArgs.OldRegionHandle);
        Assert.Equal(oldLocal, receivedArgs.OldLocalId);
        Assert.Equal(newRegion, receivedArgs.NewRegionHandle);
        Assert.Equal(newLocal, receivedArgs.NewLocalId);
    }

    [Fact]
    public void RekeyEntity_EvictsConflictingEntityAtTargetKey()
    {
        var world = new World();
        const ulong region = 100ul;
        var entityToMove = world.GetOrCreateEntity(region, 1);
        var conflictingEntity = world.GetOrCreateEntity(region, 2);

        bool removedFired = false;
        world.EntityRemoved += (s, e) =>
        {
            if (e.Entity.LocalId == 2) removedFired = true;
        };

        world.RekeyEntity(entityToMove, region, 2);

        Assert.True(removedFired);
        Assert.Same(entityToMove, world.GetEntity(region, 2));
    }

    [Fact]
    public void RemoveRegion_PreservesLocalAgent_AndAllAttachmentsAndHUDs_AndMarksAwaitingReconfirmation()
    {
        var world = new World();
        const ulong region = 500ul;

        // 1. Self avatar
        var selfAvatar = world.GetOrCreateEntity(region, 10);
        selfAvatar.SetComponent(new AvatarComponent(System.Guid.NewGuid(), "Self", "Resident", isLocalAgent: true));

        // 2. Direct attachment root (e.g. hair)
        var hairRoot = world.GetOrCreateEntity(region, 20);
        var hairAtt = new AttachmentComponent(selfAvatar.Id, 2 /* Skull */);
        hairRoot.SetComponent(hairAtt);
        hairRoot.SetComponent(new TransformComponent { ParentLocalId = 10 });

        // 3. Child prim linked to attachment root
        var hairChild = world.GetOrCreateEntity(region, 21);
        hairChild.SetComponent(new TransformComponent { ParentLocalId = 20 });

        // 4. HUD attachment (e.g. HUD root)
        var hudRoot = world.GetOrCreateEntity(region, 30);
        var hudAtt = new AttachmentComponent(selfAvatar.Id, 31 /* HUD Center 2 */);
        hudRoot.SetComponent(hudAtt);

        // 5. Remote avatar and its attachment (must be removed)
        var remoteAvatar = world.GetOrCreateEntity(region, 40);
        remoteAvatar.SetComponent(new AvatarComponent(System.Guid.NewGuid(), "Other", "Resident", isLocalAgent: false));
        var remoteAtt = world.GetOrCreateEntity(region, 41);
        remoteAtt.SetComponent(new AttachmentComponent(remoteAvatar.Id, 1));

        // 6. World prim (must be removed)
        var worldPrim = world.GetOrCreateEntity(region, 50);
        worldPrim.SetComponent(new TransformComponent());

        var removedIds = new System.Collections.Generic.List<uint>();
        world.EntityRemoved += (s, e) => removedIds.Add(e.Entity.LocalId);

        world.RemoveRegion(region);

        // Self avatar and attachments preserved
        Assert.NotNull(world.GetEntity(region, 10));
        Assert.NotNull(world.GetEntity(region, 20));
        Assert.NotNull(world.GetEntity(region, 21));
        Assert.NotNull(world.GetEntity(region, 30));

        // Marked awaiting re-confirmation
        Assert.True(world.GetEntity(region, 20)!.GetComponent<AttachmentComponent>()!.AwaitingReconfirmation);
        Assert.True(world.GetEntity(region, 21)!.GetComponent<AttachmentComponent>()!.AwaitingReconfirmation);
        Assert.True(world.GetEntity(region, 30)!.GetComponent<AttachmentComponent>()!.AwaitingReconfirmation);

        // Other content removed
        Assert.Null(world.GetEntity(region, 40));
        Assert.Null(world.GetEntity(region, 41));
        Assert.Null(world.GetEntity(region, 50));

        Assert.Contains(40u, removedIds);
        Assert.Contains(41u, removedIds);
        Assert.Contains(50u, removedIds);
        Assert.DoesNotContain(10u, removedIds);
        Assert.DoesNotContain(20u, removedIds);
        Assert.DoesNotContain(21u, removedIds);
        Assert.DoesNotContain(30u, removedIds);
    }
}
