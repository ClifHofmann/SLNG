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
    public void LinkedGrandchild_IsRecomposed_WhenRootArrivesLast()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        // Grandchild (id 3, parent 2), child (id 2, parent 1), then root (id 1, parent 0)
        session.RaiseObjectUpdate(new ObjectUpdateEvent(1ul, 3, new Vector3(2, 0, 0), Quaternion.Identity, Vector3.One, 1, false, Guid.Empty, Guid.Empty, Guid.Empty, Vector4.One, ParentLocalId: 2, AttachmentPoint: 0));
        session.RaiseObjectUpdate(new ObjectUpdateEvent(1ul, 2, new Vector3(5, 0, 0), Quaternion.Identity, Vector3.One, 1, false, Guid.Empty, Guid.Empty, Guid.Empty, Vector4.One, ParentLocalId: 1, AttachmentPoint: 0));
        simulation.Pump();
        session.RaiseObjectUpdate(new ObjectUpdateEvent(1ul, 1, new Vector3(100, 200, 30), Quaternion.Identity, Vector3.One, 1, false, Guid.Empty, Guid.Empty, Guid.Empty, Vector4.One, ParentLocalId: 0, AttachmentPoint: 0));
        simulation.Pump();

        var grandchild = world.GetEntity(1ul, 3)!.GetComponent<TransformComponent>()!;
        Assert.Equal(new Vector3(107, 200, 30), grandchild.Position);
    }

    [Fact]
    public void LinkedAttachmentChild_ComposesRootRotation_WhenRecomposed()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        // Rotations: root rotated 180 deg around Z -> (0, 0, 1, 0).
        // Child local offset (1, 2, 3).
        // Transformed by 180 deg around Z: (-1, -2, 3).
        var rotZ180 = new Quaternion(0, 0, 1, 0);
        session.RaiseObjectUpdate(new ObjectUpdateEvent(1ul, 2, new Vector3(1, 2, 3), Quaternion.Identity, Vector3.One, 1, false, Guid.Empty, Guid.Empty, Guid.Empty, Vector4.One, ParentLocalId: 1, AttachmentPoint: 35));
        simulation.Pump();
        session.RaiseObjectUpdate(new ObjectUpdateEvent(1ul, 1, new Vector3(10, 20, 30), rotZ180, Vector3.One, 1, false, Guid.Empty, Guid.Empty, Guid.Empty, Vector4.One, ParentLocalId: 0, AttachmentPoint: 35));
        simulation.Pump();

        var child = world.GetEntity(1ul, 2)!.GetComponent<TransformComponent>()!;
        Assert.Equal(new Vector3(9, 18, 33), child.Position);
        Assert.Equal(rotZ180, child.Rotation);
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
        // Position/rotation should still update normally. Checks TargetPosition, not the rendered
        // Position -- Position now eases toward TargetPosition via ExtrapolateMovement rather than
        // being set directly by Pump() alone (see TransformComponent.TargetPosition's doc comment).
        Assert.Equal(new Vector3(1, 2, 3), entity.GetComponent<TransformComponent>()!.TargetPosition);
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

    /// <summary>Regression test (viewer-parity rebuild, 2026-07-23): the local agent's
    /// TargetPosition is server-authoritative, exactly like every other avatar -- matching the real
    /// viewer, where LLAgent::getPositionAgent() mirrors LLVOAvatarSelf's network-driven position
    /// rather than reconciling it against a separate client-predicted one (see llagent.cpp). A
    /// prior version of this fix gave the local agent a special "only snap on >1m divergence" gate
    /// to protect an AvatarController-side WASD prediction; that prediction has since been removed
    /// (it was the actual cause of the reported sideways popping while walking, not the network
    /// echo itself), so the local agent must go through the exact same unconditional-overwrite path
    /// as a remote one now. (The RENDERED Position is a separate, eased follower of TargetPosition
    /// -- see the PositionEasesTowardTarget test below -- so this test checks TargetPosition, not
    /// Position, which Pump() alone no longer touches for a small update.)</summary>
    [Fact]
    public void AvatarUpdateEvent_LocalAgent_TargetPositionIsServerAuthoritative()
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

        Assert.Equal(new Vector3(10.1f, 10.1f, 10f), entity!.GetComponent<TransformComponent>()!.TargetPosition);
    }

    /// <summary>Regression test: a small/ordinary TargetPosition update (an in-range packet-gap
    /// catch-up, not a teleport) must NOT snap the rendered Position instantly -- it should ease
    /// toward TargetPosition over subsequent ExtrapolateMovement frames instead. Live-tested console
    /// data (2026-07-23, OSGrid) showed 1-2+ second packet gaps recurring during ordinary walking;
    /// hard-snapping Position the instant a delayed packet landed read as a repeated multi-metre pop
    /// even though nothing was actually wrong with the sync -- this is what turns that into a quick
    /// glide instead.</summary>
    [Fact]
    public void ExtrapolateMovement_PositionEasesTowardTarget_ForOrdinaryUpdates()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        var agentId = Guid.NewGuid();
        session.RaiseAvatarUpdate(new AvatarUpdateEvent(123ul, 42, agentId, Vector3.Zero, Quaternion.Identity, "Local", "Agent", true));
        simulation.Pump();

        var transform = world.GetEntity(123ul, 42)!.GetComponent<TransformComponent>()!;

        // A 3m catch-up (well under the 5m teleport-snap threshold) after a real packet gap.
        session.RaiseAvatarUpdate(new AvatarUpdateEvent(123ul, 42, agentId, new Vector3(3, 0, 0), Quaternion.Identity, "Local", "Agent", true));
        simulation.Pump();

        Assert.Equal(new Vector3(3, 0, 0), transform.TargetPosition);
        Assert.Equal(Vector3.Zero, transform.Position); // not snapped yet

        simulation.ExtrapolateMovement(0.05f);
        Assert.NotEqual(Vector3.Zero, transform.Position); // visibly approaching...
        Assert.NotEqual(new Vector3(3, 0, 0), transform.Position); // ...but not there yet

        for (int i = 0; i < 20; i++) simulation.ExtrapolateMovement(0.05f); // 1s total, well past 95%-there
        Assert.True(Vector3.Distance(transform.Position, new Vector3(3, 0, 0)) < 0.01f);
    }

    /// <summary>Companion to the easing test above: a genuinely large jump (teleport, sit/stand,
    /// initial spawn) must still snap Position instantly -- the easing behavior is deliberately
    /// scoped to ordinary packet-gap catch-ups, not real discontinuous moves, which should look
    /// instant.</summary>
    [Fact]
    public void ApplyAvatarUpdate_LargeJump_SnapsPositionInstantly()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        var agentId = Guid.NewGuid();
        session.RaiseAvatarUpdate(new AvatarUpdateEvent(123ul, 42, agentId, Vector3.Zero, Quaternion.Identity, "Local", "Agent", true));
        simulation.Pump();

        var transform = world.GetEntity(123ul, 42)!.GetComponent<TransformComponent>()!;

        // Well past the 5m teleport-snap threshold.
        session.RaiseAvatarUpdate(new AvatarUpdateEvent(123ul, 42, agentId, new Vector3(200, 0, 0), Quaternion.Identity, "Local", "Agent", true));
        simulation.Pump();

        Assert.Equal(new Vector3(200, 0, 0), transform.TargetPosition);
        Assert.Equal(new Vector3(200, 0, 0), transform.Position); // snapped immediately, no easing
    }

    /// <summary>Regression test (2026-07-23, round 3 of the OSGrid judder investigation): the local
    /// agent's Z is never taken from the network. AvatarController's ground-clamp runs every frame
    /// independent of any packet (a physics raycast producing groundHeight + halfBodyZ) and writes
    /// straight into TransformComponent.Position.Z -- a separate, already-authoritative source for
    /// local Z. Feeding the network's own e.Position.Z into TargetPosition as well made two
    /// independent systems fight over Z every frame, continuously, not just at packet-arrival
    /// moments -- live-test feedback ("genau so ruckelig") after every purely network-timing fix in
    /// this file had already landed pointed at exactly this. X/Y remain fully network-driven; only Z
    /// is pinned to whatever the ground-clamp has already put in Position.Z.</summary>
    [Fact]
    public void AvatarUpdateEvent_LocalAgent_IgnoresNetworkZ_KeepsLocalGroundClampZ()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        var agentId = Guid.NewGuid();
        session.RaiseAvatarUpdate(new AvatarUpdateEvent(123ul, 42, agentId, new Vector3(10, 10, 20), Quaternion.Identity, "Local", "Agent", true));
        simulation.Pump();

        var entity = world.GetEntity(123ul, 42);
        var transform = entity!.GetComponent<TransformComponent>()!;

        // Simulate AvatarController's ground-clamp having independently moved local Z (e.g. the
        // avatar stepped onto a platform) to a value the network doesn't know about yet.
        transform.Position = new Vector3(transform.Position.X, transform.Position.Y, 25f);

        // A network echo reports a totally different Z (its own separate wire convention/lag --
        // see TargetPosition's doc comment on why local Z is never trusted from it).
        session.RaiseAvatarUpdate(new AvatarUpdateEvent(123ul, 42, agentId, new Vector3(10.5f, 10.5f, 999f), Quaternion.Identity, "Local", "Agent", true));
        simulation.Pump();

        Assert.Equal(10.5f, transform.TargetPosition.X);
        Assert.Equal(10.5f, transform.TargetPosition.Y);
        Assert.Equal(25f, transform.TargetPosition.Z); // NOT 999 -- ground-clamp Z wins for local agent
    }

    /// <summary>Companion to the test above: ExtrapolateMovement's position-easing Lerp must also
    /// leave the local agent's Z untouched. TargetPosition.Z is only re-pinned to Position.Z when a
    /// packet arrives; between packets AvatarController's ground-clamp keeps moving Position.Z while
    /// TargetPosition.Z stays frozen, so easing Position toward TargetPosition on Z would drag it
    /// back toward the stale value every frame -- the same fight the network X/Y sync used to have
    /// with the old client prediction, just on the vertical axis (would read as a vertical bob while
    /// walking over uneven terrain). X/Y still ease normally.</summary>
    [Fact]
    public void ExtrapolateMovement_LocalAgent_DoesNotEaseZTowardStaleTarget()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        var agentId = Guid.NewGuid();
        session.RaiseAvatarUpdate(new AvatarUpdateEvent(123ul, 42, agentId, new Vector3(0, 0, 20), Quaternion.Identity, "Local", "Agent", true));
        simulation.Pump();

        var transform = world.GetEntity(123ul, 42)!.GetComponent<TransformComponent>()!;
        // TargetPosition is now (0,0,20); Position was snapped there on first update.

        // Give X/Y a target the render position lags behind, and have the ground-clamp move Z to a
        // value TargetPosition.Z (still 20) doesn't know about.
        session.RaiseAvatarUpdate(new AvatarUpdateEvent(123ul, 42, agentId, new Vector3(1, 0, 20), Quaternion.Identity, "Local", "Agent", true));
        simulation.Pump();
        transform.Position = new Vector3(transform.Position.X, transform.Position.Y, 25f); // ground-clamp raised Z

        simulation.ExtrapolateMovement(0.05f);

        Assert.Equal(25f, transform.Position.Z); // Z untouched by the ease -- clamp value preserved
        Assert.NotEqual(0f, transform.Position.X); // X did ease toward the (1,0,20) target
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

        // Checks TargetPosition -- what velocity dead-reckoning directly drives -- rather than the
        // rendered Position, which separately eases toward TargetPosition (see
        // ExtrapolateMovement_PositionEasesTowardTarget_ForOrdinaryUpdates).
        simulation.ExtrapolateMovement(0.1f); // full weight (well before phase-out), half dilation
        Assert.Equal(new Vector3(0.1f, 0, 0), transform.TargetPosition); // 2 m/s * 0.1s * 0.5 dilation
    }

    /// <summary>ExtrapolateMovement dead-reckons Position from Velocity at full weight before
    /// ExtrapolationPhaseOutStartSeconds (0.4s) -- mirrors the real viewer's
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

        // Checks TargetPosition -- see the dilation test above for why.
        simulation.ExtrapolateMovement(0.1f); // well before the 0.4s phase-out start
        Assert.Equal(new Vector3(0.2f, 0, 0), transform.TargetPosition);
    }

    /// <summary>Extrapolation must stop entirely once TimeSinceUpdate reaches
    /// ExtrapolationMaxSeconds (0.8s) -- a stalled/lost connection should freeze the avatar in
    /// place rather than fling it forever along a possibly-stale velocity.</summary>
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

        // Advance in small steps well past the 0.8s cutoff (mirrors real per-frame deltas). Checks
        // TargetPosition -- see the dilation test above for why.
        for (int i = 0; i < 60; i++) // 60 * 0.1s = 6.0s (local agent cutoff is now 5.0s)
        {
            simulation.ExtrapolateMovement(0.1f);
        }
        var targetPositionAfterCutoff = transform.TargetPosition;

        simulation.ExtrapolateMovement(0.1f); // fully decayed by now -- must no longer move
        Assert.Equal(targetPositionAfterCutoff, transform.TargetPosition);
    }

    /// <summary>Regression test (REMOTE avatar -- the local agent's Rotation is excluded from this
    /// system entirely, see the local-agent test below): turning must not hard-snap. Reported
    /// symptom -- once Position's own judder was fixed, turning was still visibly choppy while
    /// straight-line walking looked smooth. Root cause: ApplyAvatarUpdate used to write straight
    /// into Rotation on every packet; unlike Position there's no reliable AngularVelocity to dead-
    /// reckon a turning avatar from (SL's wire AngularVelocity is for llSetTargetOmega-spun
    /// objects), so Rotation only ever changed in discrete per-packet jumps. Fixed by routing
    /// network rotation through TargetRotation and slerping Rotation toward it every frame
    /// instead.</summary>
    [Fact]
    public void AvatarUpdateEvent_RemoteAgent_RotationDoesNotSnap_SmoothsTowardTarget()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        var agentId = Guid.NewGuid();
        session.RaiseAvatarUpdate(new AvatarUpdateEvent(123ul, 42, agentId, Vector3.Zero, Quaternion.Identity, "Remote", "Agent", false));
        simulation.Pump();

        var entity = world.GetEntity(123ul, 42);
        var transform = entity!.GetComponent<TransformComponent>()!;
        Assert.Equal(Quaternion.Identity, transform.Rotation); // starts facing identity, no smoothing needed on spawn

        // Avatar turns 90 degrees around Z (SL up-axis pre-conversion doesn't matter for this test).
        var turned = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);
        session.RaiseAvatarUpdate(new AvatarUpdateEvent(123ul, 42, agentId, Vector3.Zero, turned, "Remote", "Agent", false));
        simulation.Pump();

        // Must NOT have snapped straight to the target -- that's the bug being fixed.
        Assert.NotEqual(turned, transform.Rotation);
        Assert.Equal(Quaternion.Identity, transform.Rotation); // ApplyAvatarUpdate alone doesn't move it

        // A few frames of extrapolation should visibly approach, but not yet reach, the target.
        simulation.ExtrapolateMovement(0.05f);
        Assert.NotEqual(Quaternion.Identity, transform.Rotation);
        Assert.NotEqual(turned, transform.Rotation);

        // Enough elapsed time (well past the ~0.36s to-95% window) converges on the target.
        for (int i = 0; i < 20; i++) simulation.ExtrapolateMovement(0.05f); // 1s total
        Assert.True(Quaternion.Dot(transform.Rotation, turned) > 0.999f);
    }

    /// <summary>Regression test (round 4 of the OSGrid judder investigation, 2026-07-23):
    /// AvatarController writes the local agent's Rotation directly every 100ms from the player's
    /// own camera yaw -- instant local input, not something that should wait on or blend with a
    /// network round-trip (same reasoning as local Z). Before this fix, ApplyAvatarUpdate/
    /// ExtrapolateMovement fed the network's echo of our own previously-sent rotation into
    /// TargetRotation and slerped toward it for the local agent too, fighting AvatarController's
    /// fresh writes on literally every frame (slerp pulls toward a latency-delayed echo of an
    /// older yaw, then AvatarController snaps back to the current one 100ms later, repeat) --
    /// unlike the X/Y position fight this whole investigation mostly addressed, this one wasn't
    /// gated on packet timing at all, so it never showed any correlation with packet gaps in the
    /// [AvatarMove] diagnostic despite being live the whole time. This test simulates
    /// AvatarController's own write (direct field set, exactly what it does) and confirms a
    /// network echo of a stale rotation afterward neither snaps nor drags Rotation away from it,
    /// for both ApplyAvatarUpdate alone and after ExtrapolateMovement frames.</summary>
    /// <summary>MVP2-1: AvatarComponent.SittingOnLocalId must reflect the seat prim's local id
    /// from the wire event, and clear back to 0 once the sim reports standing again (e.g. after
    /// GridSession.Stand()) -- this is the sole flag AvatarController/UI use to know the local
    /// agent is seated.</summary>
    [Fact]
    public void AvatarUpdateEvent_PopulatesAndClearsSittingOnLocalId()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        var agentId = Guid.NewGuid();
        session.RaiseAvatarUpdate(new AvatarUpdateEvent(123ul, 42, agentId, Vector3.Zero, Quaternion.Identity, "Local", "Agent", true));
        simulation.Pump();

        var entity = world.GetEntity(123ul, 42);
        Assert.Equal(0u, entity!.GetComponent<AvatarComponent>()!.SittingOnLocalId);

        // Sits on seat prim local id 7.
        session.RaiseAvatarUpdate(new AvatarUpdateEvent(123ul, 42, agentId, new Vector3(0, 0, 0.5f), Quaternion.Identity, "Local", "Agent", true, SittingOnLocalId: 7));
        simulation.Pump();
        Assert.Equal(7u, entity.GetComponent<AvatarComponent>()!.SittingOnLocalId);

        // Stands back up.
        session.RaiseAvatarUpdate(new AvatarUpdateEvent(123ul, 42, agentId, new Vector3(1, 2, 3), Quaternion.Identity, "Local", "Agent", true));
        simulation.Pump();
        Assert.Equal(0u, entity.GetComponent<AvatarComponent>()!.SittingOnLocalId);
    }

    /// <summary>MVP2-1: while seated, AvatarController stops writing this entity's Z (its
    /// ground-clamp is suspended -- see AvatarController's own isSitting gate), so a seated local
    /// agent must take its FULL network Position -- including Z -- exactly like a remote avatar,
    /// unlike the standing local-agent case (see AvatarUpdateEvent_LocalAgent_IgnoresNetworkZ_
    /// KeepsLocalGroundClampZ above, which this directly contrasts with).</summary>
    [Fact]
    public void AvatarUpdateEvent_SeatedLocalAgent_TakesNetworkZ()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        var agentId = Guid.NewGuid();
        session.RaiseAvatarUpdate(new AvatarUpdateEvent(123ul, 42, agentId, new Vector3(10, 10, 20), Quaternion.Identity, "Local", "Agent", true));
        simulation.Pump();

        var entity = world.GetEntity(123ul, 42);
        var transform = entity!.GetComponent<TransformComponent>()!;

        // Ground-clamp had independently pushed local Z to 25 before sitting.
        transform.Position = new Vector3(transform.Position.X, transform.Position.Y, 25f);

        // Now seated on prim 7 -- the seat's resolved world Z (e.g. an elevated chair) must win,
        // not the stale ground-clamp value.
        session.RaiseAvatarUpdate(new AvatarUpdateEvent(123ul, 42, agentId, new Vector3(10.5f, 10.5f, 30f), Quaternion.Identity, "Local", "Agent", true, SittingOnLocalId: 7));
        simulation.Pump();

        Assert.Equal(30f, transform.TargetPosition.Z);
    }

    /// <summary>MVP2-1: while seated, AvatarController also stops writing this entity's Rotation
    /// (the seat/script owns facing, not the player's camera yaw -- see AvatarController's own
    /// isSitting gate), so a seated local agent must smooth toward the network's Rotation exactly
    /// like a remote avatar, unlike the standing local-agent case (see
    /// AvatarUpdateEvent_LocalAgent_RotationIsNeverNetworkDriven above).</summary>
    [Fact]
    public void AvatarUpdateEvent_SeatedLocalAgent_RotationIsNetworkDriven()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        var agentId = Guid.NewGuid();
        session.RaiseAvatarUpdate(new AvatarUpdateEvent(123ul, 42, agentId, Vector3.Zero, Quaternion.Identity, "Local", "Agent", true));
        simulation.Pump();

        var entity = world.GetEntity(123ul, 42);
        var transform = entity!.GetComponent<TransformComponent>()!;
        Assert.Equal(Quaternion.Identity, transform.Rotation);

        var seatRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);
        session.RaiseAvatarUpdate(new AvatarUpdateEvent(123ul, 42, agentId, Vector3.Zero, seatRotation, "Local", "Agent", true, SittingOnLocalId: 7));
        simulation.Pump();

        // TargetRotation is now network-driven (unlike the un-seated local-agent path); smoothing
        // toward it converges the same way a remote avatar's does.
        for (int i = 0; i < 20; i++) simulation.ExtrapolateMovement(0.05f);
        Assert.True(Quaternion.Dot(transform.Rotation, seatRotation) > 0.999f);
    }

    [Fact]
    public void AvatarUpdateEvent_LocalAgent_RotationIsNeverNetworkDriven()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        var agentId = Guid.NewGuid();
        session.RaiseAvatarUpdate(new AvatarUpdateEvent(123ul, 42, agentId, Vector3.Zero, Quaternion.Identity, "Local", "Agent", true));
        simulation.Pump();

        var entity = world.GetEntity(123ul, 42);
        var transform = entity!.GetComponent<TransformComponent>()!;

        // AvatarController's own direct write of the player's current camera yaw.
        var localYaw = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);
        transform.Rotation = localYaw;

        // A network echo reports our own OLDER rotation, latency-delayed (Identity, from the
        // initial creation) -- must not pull Rotation away from what AvatarController just set.
        session.RaiseAvatarUpdate(new AvatarUpdateEvent(123ul, 42, agentId, Vector3.Zero, Quaternion.Identity, "Local", "Agent", true));
        simulation.Pump();
        Assert.Equal(localYaw, transform.Rotation);

        simulation.ExtrapolateMovement(0.1f);
        Assert.Equal(localYaw, transform.Rotation);
    }

    // BUG-NET-13: the stale-circuit churn after a teleport could leave an avatar entity with a
    // non-finite Position or a zero (default(Quaternion), not Identity) Rotation -- both feed the
    // renderer a NaN basis and produce the engine's per-frame "Vector3 cannot be normalized"
    // warning (9212 copies in one teleport-heavy session). ExtrapolateMovement now repairs it.
    [Theory]
    [InlineData(false)] // remote avatar -- Rotation IS slerped here
    [InlineData(true)]  // local agent  -- Rotation is not, but Position easing still runs
    public void ExtrapolateMovement_RepairsNonFiniteAvatarTransform(bool isLocalAgent)
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        var agentId = Guid.NewGuid();
        session.RaiseAvatarUpdate(new AvatarUpdateEvent(
            123ul, 42, agentId, Vector3.Zero, Quaternion.Identity, "Test", "Resident", isLocalAgent));
        simulation.Pump();

        var transform = world.GetEntity(123ul, 42)!.GetComponent<TransformComponent>()!;
        transform.Position = new Vector3(float.NaN, 0, 0);
        transform.TargetPosition = new Vector3(1, 2, 3);
        transform.Rotation = new Quaternion(0, 0, 0, 0);       // zero quaternion normalizes to NaN
        transform.TargetRotation = new Quaternion(0, 0, 0, 0);

        simulation.ExtrapolateMovement(0.016f);

        Assert.True(float.IsFinite(transform.Position.X) && float.IsFinite(transform.Position.Y)
            && float.IsFinite(transform.Position.Z), "Position must be finite after ExtrapolateMovement");
        Assert.True(float.IsFinite(transform.Rotation.X) && float.IsFinite(transform.Rotation.Y)
            && float.IsFinite(transform.Rotation.Z) && float.IsFinite(transform.Rotation.W),
            "Rotation must be finite after ExtrapolateMovement");
        Assert.NotEqual(new Quaternion(0, 0, 0, 0), transform.Rotation);
    }

    [Fact]
    public void ExtrapolateMovement_LeavesAFiniteAvatarTransformUntouched()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        var agentId = Guid.NewGuid();
        var rot = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.7f);
        session.RaiseAvatarUpdate(new AvatarUpdateEvent(
            123ul, 42, agentId, new Vector3(5, 6, 7), rot, "Test", "Resident", false));
        simulation.Pump();

        var transform = world.GetEntity(123ul, 42)!.GetComponent<TransformComponent>()!;

        simulation.ExtrapolateMovement(0.016f);

        Assert.Equal(new Vector3(5, 6, 7), transform.Position);
        Assert.True(Quaternion.Dot(transform.Rotation, rot) > 0.999f);
    }
}
