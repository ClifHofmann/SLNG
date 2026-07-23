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

    /// <summary>Regression test for the remote-avatar float bug (fix/remote-avatar-animations):
    /// a remote avatar's entity can be created from a bare TerseObjectUpdate before
    /// LibreMetaverse's ObjectsAvatars cache has resolved the full AgentID, leaving
    /// AvatarComponent.AgentId == Guid.Empty. The dedicated AvatarAppearance message DOES carry
    /// the real AgentId, but an exact-match lookup against the still-empty AgentId never finds
    /// the entity — so the avatar's real VisualParams (and therefore its real BodySizeZ /
    /// FootOffsetY) are silently dropped forever, leaving it on default shape constants that
    /// don't match its actual proportions. ApplyAvatarAnimation already had this fallback
    /// (commit 6bcf31e); ApplyAvatarAppearance did not.</summary>
    [Fact]
    public void AvatarAppearanceEvent_ResolvesEntity_WhenAgentIdWasStillEmpty()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        // Entity created with AgentId == Guid.Empty, mirroring a bare TerseObjectUpdate for a
        // remote avatar whose AgentID hadn't resolved yet.
        var updateEvt = new AvatarUpdateEvent(123ul, 42, Guid.Empty, Vector3.Zero, Quaternion.Identity, "", "", false);
        session.RaiseAvatarUpdate(updateEvt);
        simulation.Pump();

        var entity = world.GetEntity(123ul, 42);
        Assert.NotNull(entity);
        Assert.Equal(Guid.Empty, entity.GetComponent<AvatarComponent>()!.AgentId);

        // The appearance message arrives with the real AgentId before any full ObjectUpdate
        // heals the entity's AgentId.
        var realAgentId = Guid.NewGuid();
        var visualParams = new byte[] { 1, 2, 3 };
        var appearanceEvt = new AvatarAppearanceEvent(123ul, realAgentId, visualParams, new Dictionary<int, Guid>());
        session.RaiseAvatarAppearance(appearanceEvt);
        simulation.Pump();

        var avatar = entity.GetComponent<AvatarComponent>();
        Assert.NotNull(avatar);
        Assert.Equal(visualParams, avatar.VisualParams);
        Assert.Equal(realAgentId, avatar.AgentId);
    }

    /// <summary>Regression test (round 5 of the remote-avatar float investigation): a resolved
    /// AgentId must never regress back to Guid.Empty from a later AvatarUpdateEvent that failed to
    /// resolve it (e.g. a bare TerseObjectUpdate whose Prim isn't an Avatar). Before this fix,
    /// ApplyAvatarUpdate overwrote AvatarComponent.AgentId/FirstName/LastName unconditionally on
    /// every update -- so a resolved AgentId reverting to empty would re-open the entity to
    /// FindAvatarEntityByAgentId's "any unresolved avatar" fallback, letting a LATER, unrelated
    /// avatar's appearance/animation event land on the wrong (already-resolved) entity and clobber
    /// its real shape/animation data.</summary>
    [Fact]
    public void AvatarUpdateEvent_DoesNotRegressAlreadyResolvedAgentId()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        var realAgentId = Guid.NewGuid();

        // 1. First update resolves the real AgentId and name.
        session.RaiseAvatarUpdate(new AvatarUpdateEvent(123ul, 42, realAgentId, Vector3.Zero, Quaternion.Identity, "Reamon", "Bullmer", false));
        simulation.Pump();

        var entity = world.GetEntity(123ul, 42);
        Assert.NotNull(entity);
        var avatar = entity.GetComponent<AvatarComponent>();
        Assert.Equal(realAgentId, avatar!.AgentId);
        Assert.Equal("Reamon", avatar.FirstName);
        Assert.Equal("Bullmer", avatar.LastName);

        // 2. A later update for the same entity fails to resolve AgentId/name (mirrors a bare
        // TerseObjectUpdate) -- must NOT stomp the already-known-good values.
        session.RaiseAvatarUpdate(new AvatarUpdateEvent(123ul, 42, Guid.Empty, new Vector3(1, 2, 3), Quaternion.Identity, "", "", false));
        simulation.Pump();

        avatar = entity.GetComponent<AvatarComponent>();
        Assert.Equal(realAgentId, avatar!.AgentId);
        Assert.Equal("Reamon", avatar.FirstName);
        Assert.Equal("Bullmer", avatar.LastName);
        // Position/rotation should still update normally.
        Assert.Equal(new Vector3(1, 2, 3), entity.GetComponent<TransformComponent>()!.Position);
    }

    /// <summary>Regression test (round 9): AvatarComponent.ScaleZ (diagnostic-only, the avatar's
    /// wire-transmitted Scale.Z -- see AvatarUpdateEvent's doc comment) must not regress to 0 from
    /// a later AvatarUpdateEvent whose ScaleZ wasn't populated (e.g. SyncLocalAgentPositionAfterTeleport,
    /// which doesn't currently supply one), mirroring the same "only add information, never blank
    /// it out" guard already applied to AgentId/FirstName/LastName.</summary>
    [Fact]
    public void AvatarUpdateEvent_DoesNotRegressAlreadyKnownScaleZ()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        session.RaiseAvatarUpdate(new AvatarUpdateEvent(123ul, 42, Guid.NewGuid(), Vector3.Zero, Quaternion.Identity, "Reamon", "Bullmer", false, ScaleZ: 2.19f));
        simulation.Pump();

        var entity = world.GetEntity(123ul, 42);
        var avatar = entity!.GetComponent<AvatarComponent>();
        Assert.Equal(2.19f, avatar!.ScaleZ);

        // A later update with no ScaleZ (defaults to 0) must not stomp the known-good value.
        session.RaiseAvatarUpdate(new AvatarUpdateEvent(123ul, 42, avatar.AgentId, new Vector3(1, 2, 3), Quaternion.Identity, "Reamon", "Bullmer", false));
        simulation.Pump();

        avatar = entity.GetComponent<AvatarComponent>();
        Assert.Equal(2.19f, avatar!.ScaleZ);
    }

    /// <summary>Regression test (viewer-parity rebuild, 2026-07-23): the local agent's position is
    /// server-authoritative, exactly like every other avatar -- matching the real viewer, where
    /// LLAgent::getPositionAgent() mirrors LLVOAvatarSelf's network-driven position rather than
    /// reconciling it against a separate client-predicted one (see llagent.cpp). A prior version of
    /// this fix gave the local agent a special "only snap on >1m divergence" gate to protect an
    /// AvatarController-side WASD prediction; that prediction has since been removed (it was the
    /// actual cause of the reported sideways popping while walking, not the network echo itself),
    /// so the local agent must go through the exact same unconditional-overwrite path as a remote
    /// one now.</summary>
    [Fact]
    public void AvatarUpdateEvent_LocalAgent_PositionIsServerAuthoritative()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        var agentId = Guid.NewGuid();
        session.RaiseAvatarUpdate(new AvatarUpdateEvent(123ul, 42, agentId, new Vector3(10, 10, 10), Quaternion.Identity, "Local", "Agent", true));
        simulation.Pump();

        var entity = world.GetEntity(123ul, 42);
        Assert.NotNull(entity);

        // A later echo, even a small nudge, must be applied directly -- no local authority to
        // reconcile against anymore.
        session.RaiseAvatarUpdate(new AvatarUpdateEvent(123ul, 42, agentId, new Vector3(10.1f, 10.1f, 10f), Quaternion.Identity, "Local", "Agent", true));
        simulation.Pump();

        Assert.Equal(new Vector3(10.1f, 10.1f, 10f), entity!.GetComponent<TransformComponent>()!.Position);
    }

    /// <summary>ApplyAvatarUpdate must record Velocity and reset TimeSinceUpdate to 0 on every
    /// update, so ExtrapolateMovement starts dead-reckoning fresh from the just-arrived position
    /// rather than compounding onto whatever it had already extrapolated from the previous one.</summary>
    [Fact]
    public void AvatarUpdateEvent_StoresVelocityAndResetsExtrapolationClock()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        var agentId = Guid.NewGuid();
        var velocity = new Vector3(1, 0, 0);
        session.RaiseAvatarUpdate(new AvatarUpdateEvent(123ul, 42, agentId, Vector3.Zero, Quaternion.Identity, "Local", "Agent", true, Velocity: velocity));
        simulation.Pump();

        var transform = world.GetEntity(123ul, 42)!.GetComponent<TransformComponent>()!;
        Assert.Equal(velocity, transform.Velocity);
        Assert.Equal(0f, transform.TimeSinceUpdate);

        simulation.ExtrapolateMovement(0.5f);
        Assert.Equal(0.5f, transform.TimeSinceUpdate);

        // A fresh network update resets the clock even if TimeSinceUpdate had already advanced.
        session.RaiseAvatarUpdate(new AvatarUpdateEvent(123ul, 42, agentId, new Vector3(0.5f, 0, 0), Quaternion.Identity, "Local", "Agent", true, Velocity: velocity));
        simulation.Pump();
        Assert.Equal(0f, transform.TimeSinceUpdate);
    }

    /// <summary>ExtrapolateMovement must scale its per-frame dead-reckoning step by the
    /// originating sim's TimeDilation -- mirrors LibreMetaverse's own InterpolationService
    /// (`adjSeconds = seconds * sim.Stats.Dilation`). Reported motivation: movement judders more
    /// on a busy OSGrid megaregion than the user's own (presumably quiet) sim -- a dilated sim
    /// runs its own physics below real-time, so extrapolating at full real-time speed overshoots
    /// what the sim actually simulated.</summary>
    [Fact]
    public void ExtrapolateMovement_ScalesByTimeDilation()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        var agentId = Guid.NewGuid();
        var velocity = new Vector3(2, 0, 0); // 2 m/s along X
        session.RaiseAvatarUpdate(new AvatarUpdateEvent(123ul, 42, agentId, Vector3.Zero, Quaternion.Identity, "Local", "Agent", true, Velocity: velocity, TimeDilation: 0.5f));
        simulation.Pump();

        var transform = world.GetEntity(123ul, 42)!.GetComponent<TransformComponent>()!;
        Assert.Equal(0.5f, transform.TimeDilation);

        simulation.ExtrapolateMovement(0.1f); // full weight (well before phase-out), half dilation
        Assert.Equal(new Vector3(0.1f, 0, 0), transform.Position); // 2 m/s * 0.1s * 0.5 dilation
    }

    /// <summary>ExtrapolateMovement dead-reckons Position from Velocity at full weight before
    /// ExtrapolationPhaseOutStartSeconds (2s) -- mirrors the real viewer's
    /// LLViewerObject::interpolateLinearMotion extrapolating from the last reported velocity
    /// between packets instead of holding Position static.</summary>
    [Fact]
    public void ExtrapolateMovement_AdvancesPositionByVelocity_BeforePhaseOut()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        var agentId = Guid.NewGuid();
        var velocity = new Vector3(2, 0, 0); // 2 m/s along X
        session.RaiseAvatarUpdate(new AvatarUpdateEvent(123ul, 42, agentId, Vector3.Zero, Quaternion.Identity, "Local", "Agent", true, Velocity: velocity));
        simulation.Pump();

        var transform = world.GetEntity(123ul, 42)!.GetComponent<TransformComponent>()!;

        simulation.ExtrapolateMovement(0.1f); // well before the 2s phase-out start
        Assert.Equal(new Vector3(0.2f, 0, 0), transform.Position);
    }

    /// <summary>Extrapolation must stop entirely once TimeSinceUpdate reaches
    /// ExtrapolationMaxSeconds (3s) -- a stalled/lost connection should freeze the avatar in place
    /// rather than fling it forever along a possibly-stale velocity.</summary>
    [Fact]
    public void ExtrapolateMovement_StopsAfterMaxSeconds()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        var agentId = Guid.NewGuid();
        var velocity = new Vector3(2, 0, 0);
        session.RaiseAvatarUpdate(new AvatarUpdateEvent(123ul, 42, agentId, Vector3.Zero, Quaternion.Identity, "Local", "Agent", true, Velocity: velocity));
        simulation.Pump();

        var transform = world.GetEntity(123ul, 42)!.GetComponent<TransformComponent>()!;

        // Advance in small steps well past the 3s cutoff (mirrors real per-frame deltas).
        for (int i = 0; i < 40; i++) // 40 * 0.1s = 4s
        {
            simulation.ExtrapolateMovement(0.1f);
        }
        var positionAfterCutoff = transform.Position;

        simulation.ExtrapolateMovement(0.1f); // fully decayed by now -- must no longer move
        Assert.Equal(positionAfterCutoff, transform.Position);
    }
}
