using System.Linq;
using System.Numerics;
using SLNG.Core.Components;
using SLNG.Core.ECS;
using SLNG.Net;
using Xunit;

namespace SLNG.Core.Tests;

public class WorldSimulationTests
{
    [Fact]
    public void ObjectUpdateEvent_CreatesEntityWithTransform()
    {
        var world = new World();
        // We instantiate GridSession to trigger the event.
        // For testing we just trigger the internal method.
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        var pos = new Vector3(10, 20, 30);
        var rot = Quaternion.Identity;

        // Trigger the internal RaiseObjectUpdate method (which we added a wrapper or just made internal).
        // Since GridSession.RaiseObjectUpdate is internal, and InternalsVisibleTo is set for SLNG.Net.Tests,
        // we need to make sure SLNG.Core.Tests also has InternalsVisibleTo!

        var scale = new Vector3(1, 1, 1);
        byte pCode = 1;

        var updateEvt = new ObjectUpdateEvent(123ul, 42, new Vector3(10, 20, 30), Quaternion.Identity, Vector3.One, 1, false, Guid.Empty, Guid.Empty, Guid.Empty, Vector4.One, ParentLocalId: 0, AttachmentPoint: 0);
        session.RaiseObjectUpdate(updateEvt);
        simulation.Pump();

        var entity = world.GetEntity(123ul, 42);
        Assert.NotNull(entity);

        var transform = entity.GetComponent<TransformComponent>();
        Assert.NotNull(transform);
        Assert.Equal(pos, transform.Position);
        Assert.Equal(rot, transform.Rotation);

        var prim = entity.GetComponent<PrimitiveComponent>();
        Assert.NotNull(prim);
        Assert.Equal(scale, prim.Scale);
        Assert.Equal(pCode, prim.ProfileCurve);
    }

    [Fact]
    public void LinkedChild_IsComposedToWorldSpace_WhenRootArrivesFirst()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        // Root at world (100,200,30); child offset (5,0,0) relative to it.
        session.RaiseObjectUpdate(new ObjectUpdateEvent(1ul, 1, new Vector3(100, 200, 30), Quaternion.Identity, Vector3.One, 1, false, Guid.Empty, Guid.Empty, Guid.Empty, Vector4.One, ParentLocalId: 0, AttachmentPoint: 0));
        session.RaiseObjectUpdate(new ObjectUpdateEvent(1ul, 2, new Vector3(5, 0, 0), Quaternion.Identity, Vector3.One, 1, false, Guid.Empty, Guid.Empty, Guid.Empty, Vector4.One, ParentLocalId: 1, AttachmentPoint: 0));
        simulation.Pump();

        var child = world.GetEntity(1ul, 2)!.GetComponent<TransformComponent>()!;
        Assert.Equal(new Vector3(105, 200, 30), child.Position);
    }

    [Fact]
    public void LinkedChild_IsRecomposed_WhenRootArrivesAfterChild()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        // Child arrives before its root — it should be re-composed once the root shows up.
        session.RaiseObjectUpdate(new ObjectUpdateEvent(1ul, 2, new Vector3(5, 0, 0), Quaternion.Identity, Vector3.One, 1, false, Guid.Empty, Guid.Empty, Guid.Empty, Vector4.One, ParentLocalId: 1, AttachmentPoint: 0));
        simulation.Pump();
        session.RaiseObjectUpdate(new ObjectUpdateEvent(1ul, 1, new Vector3(100, 200, 30), Quaternion.Identity, Vector3.One, 1, false, Guid.Empty, Guid.Empty, Guid.Empty, Vector4.One, ParentLocalId: 0, AttachmentPoint: 0));
        simulation.Pump();

        var child = world.GetEntity(1ul, 2)!.GetComponent<TransformComponent>()!;
        Assert.Equal(new Vector3(105, 200, 30), child.Position);
    }

    [Fact]
    public void RootPrim_KeepsWorldPosition()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        session.RaiseObjectUpdate(new ObjectUpdateEvent(1ul, 1, new Vector3(100, 200, 30), Quaternion.Identity, Vector3.One, 1, false, Guid.Empty, Guid.Empty, Guid.Empty, Vector4.One, ParentLocalId: 0, AttachmentPoint: 0));
        simulation.Pump();

        var root = world.GetEntity(1ul, 1)!.GetComponent<TransformComponent>()!;
        Assert.Equal(new Vector3(100, 200, 30), root.Position);
    }

    [Fact]
    public void AvatarAppearanceEvent_UpdatesAvatarComponent()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        var agentId = Guid.NewGuid();

        // 1. Create the avatar entity first by raising an AvatarUpdate
        var updateEvt = new AvatarUpdateEvent(123ul, 42, agentId, Vector3.Zero, Quaternion.Identity, "Test", "User", false);
        session.RaiseAvatarUpdate(updateEvt);
        simulation.Pump();

        var entity = world.GetEntity(123ul, 42);
        Assert.NotNull(entity);
        var avatar = entity.GetComponent<AvatarComponent>();
        Assert.NotNull(avatar);
        Assert.Null(avatar.VisualParams);
        Assert.Null(avatar.BakedTextures);

        // 2. Raise the appearance update
        var visualParams = new byte[] { 10, 20, 30 };
        var bakedTextures = new Dictionary<int, Guid> { { 8, Guid.NewGuid() } };
        var appearanceEvt = new AvatarAppearanceEvent(123ul, agentId, visualParams, bakedTextures);

        session.RaiseAvatarAppearance(appearanceEvt);
        simulation.Pump();

        // 3. Verify it was applied
        avatar = entity.GetComponent<AvatarComponent>();
        Assert.NotNull(avatar);
        Assert.Equal(visualParams, avatar.VisualParams);
        Assert.Equal(bakedTextures, avatar.BakedTextures);
    }

    [Fact]
    public void AvatarAnimationEvent_UpdatesActiveAnimations()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        var agentId = Guid.NewGuid();

        // 1. Create the avatar entity first
        var updateEvt = new AvatarUpdateEvent(123ul, 42, agentId, Vector3.Zero, Quaternion.Identity, "Test", "User", false);
        session.RaiseAvatarUpdate(updateEvt);
        simulation.Pump();

        var entity = world.GetEntity(123ul, 42);
        Assert.NotNull(entity);
        var avatar = entity.GetComponent<AvatarComponent>();
        Assert.NotNull(avatar);
        Assert.Null(avatar.ActiveAnimations);

        // 2. Raise animation event with two animation IDs
        var anim1 = Guid.NewGuid();
        var anim2 = Guid.NewGuid();
        var animEvt = new AvatarAnimationEvent(agentId, new List<Guid> { anim1, anim2 });
        session.RaiseAvatarAnimation(animEvt);
        simulation.Pump();

        // 3. Verify
        avatar = entity.GetComponent<AvatarComponent>();
        Assert.NotNull(avatar);
        Assert.NotNull(avatar.ActiveAnimations);
        Assert.Equal(2, avatar.ActiveAnimations.Count);
        Assert.Contains(anim1, avatar.ActiveAnimations);
        Assert.Contains(anim2, avatar.ActiveAnimations);
    }
}
