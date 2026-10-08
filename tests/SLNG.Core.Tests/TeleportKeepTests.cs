using System;
using System.Linq;
using System.Numerics;
using SLNG.Core.Components;
using SLNG.Core.ECS;
using SLNG.Net;
using Xunit;

namespace SLNG.Core.Tests;

public class TeleportKeepTests
{
    [Fact]
    public void ApplyAvatarUpdate_RekeyesExistingLocalAgent_PreservingEntityIdAndAppearance()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        var agentId = Guid.NewGuid();
        ulong region1 = 1000ul;
        uint localId1 = 42;

        // 1. Initial spawn in region 1
        session.RaiseAvatarUpdate(new AvatarUpdateEvent(
            region1, localId1, agentId, new Vector3(10, 20, 30), Quaternion.Identity,
            "Test", "User", IsLocalAgent: true));
        simulation.Pump();

        var agentEntity = world.GetEntity(region1, localId1);
        Assert.NotNull(agentEntity);
        var originalGuid = agentEntity.Id;
        var avatarComp = agentEntity.GetComponent<AvatarComponent>()!;
        avatarComp.ScaleZ = 1.85f;

        // 2. Teleport to region 2 with new localId 99
        ulong region2 = 2000ul;
        uint localId2 = 99;
        session.RaiseAvatarUpdate(new AvatarUpdateEvent(
            region2, localId2, agentId, new Vector3(100, 200, 50), Quaternion.Identity,
            "Test", "User", IsLocalAgent: true, IsTeleport: true));
        simulation.Pump();

        // Old key must be gone
        Assert.Null(world.GetEntity(region1, localId1));

        // New key must hold the SAME Entity instance and Guid
        var rekeyedAgent = world.GetEntity(region2, localId2);
        Assert.NotNull(rekeyedAgent);
        Assert.Equal(originalGuid, rekeyedAgent.Id);
        Assert.Same(agentEntity, rekeyedAgent);
        Assert.Equal(1.85f, rekeyedAgent.GetComponent<AvatarComponent>()!.ScaleZ);
    }

    [Fact]
    public void ApplyObjectUpdate_RekeyesExistingAttachmentByUUID_PreservingEntityId()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        var agentId = Guid.NewGuid();
        ulong region1 = 1000ul;
        uint agentLocalId1 = 10;
        uint attachLocalId1 = 20;
        var attachUuid = Guid.NewGuid();

        // Spawn agent & attachment in region 1
        session.RaiseAvatarUpdate(new AvatarUpdateEvent(
            region1, agentLocalId1, agentId, Vector3.Zero, Quaternion.Identity,
            "Test", "User", IsLocalAgent: true));
        session.RaiseObjectUpdate(new ObjectUpdateEvent(
            region1, attachLocalId1, Vector3.Zero, Quaternion.Identity, Vector3.One,
            1, false, Guid.Empty, Guid.Empty, Guid.Empty, Vector4.One,
            ParentLocalId: agentLocalId1, AttachmentPoint: 2, ObjectId: attachUuid));
        simulation.Pump();

        var agent = world.GetEntity(region1, agentLocalId1)!;
        var attach = world.GetEntity(region1, attachLocalId1)!;
        var attachEntityId = attach.Id;

        // Disconnect region 1 (RemoveRegion keeps agent + attachment marked AwaitingReconfirmation)
        session.RaiseRegionDisconnected(new RegionDisconnectedEvent(region1));
        simulation.Pump();

        Assert.True(attach.GetComponent<AttachmentComponent>()!.AwaitingReconfirmation);

        // Region 2 arrives: sends avatar and attachment under new local IDs
        ulong region2 = 2000ul;
        uint agentLocalId2 = 50;
        uint attachLocalId2 = 60;

        session.RaiseAvatarUpdate(new AvatarUpdateEvent(
            region2, agentLocalId2, agentId, Vector3.Zero, Quaternion.Identity,
            "Test", "User", IsLocalAgent: true, IsTeleport: true));
        session.RaiseObjectUpdate(new ObjectUpdateEvent(
            region2, attachLocalId2, Vector3.Zero, Quaternion.Identity, Vector3.One,
            1, false, Guid.Empty, Guid.Empty, Guid.Empty, Vector4.One,
            ParentLocalId: agentLocalId2, AttachmentPoint: 2, ObjectId: attachUuid));
        simulation.Pump();

        // Old attach key gone
        Assert.Null(world.GetEntity(region1, attachLocalId1));

        // Rekeyed attach found at region 2, localId 60 with same EntityId
        var rekeyedAttach = world.GetEntity(region2, attachLocalId2);
        Assert.NotNull(rekeyedAttach);
        Assert.Equal(attachEntityId, rekeyedAttach.Id);
        Assert.Same(attach, rekeyedAttach);
        Assert.False(rekeyedAttach.GetComponent<AttachmentComponent>()!.AwaitingReconfirmation);
    }

    [Fact]
    public void Reconciliation_RemovesUnconfirmedAttachments_WhenAnswersSubside()
    {
        var world = new World();
        using var session = new GridSession();
        using var simulation = new WorldSimulation(world, session);

        var agentId = Guid.NewGuid();
        ulong region1 = 1000ul;
        uint agentLocalId1 = 10;
        uint attach1LocalId = 21;
        uint attach2LocalId = 22;
        var attach1Uuid = Guid.NewGuid();
        var attach2Uuid = Guid.NewGuid();

        // Spawn agent & 2 attachments in region 1
        session.RaiseAvatarUpdate(new AvatarUpdateEvent(
            region1, agentLocalId1, agentId, Vector3.Zero, Quaternion.Identity,
            "Test", "User", IsLocalAgent: true));
        session.RaiseObjectUpdate(new ObjectUpdateEvent(
            region1, attach1LocalId, Vector3.Zero, Quaternion.Identity, Vector3.One,
            1, false, Guid.Empty, Guid.Empty, Guid.Empty, Vector4.One,
            ParentLocalId: agentLocalId1, AttachmentPoint: 1, ObjectId: attach1Uuid));
        session.RaiseObjectUpdate(new ObjectUpdateEvent(
            region1, attach2LocalId, Vector3.Zero, Quaternion.Identity, Vector3.One,
            1, false, Guid.Empty, Guid.Empty, Guid.Empty, Vector4.One,
            ParentLocalId: agentLocalId1, AttachmentPoint: 2, ObjectId: attach2Uuid));
        simulation.Pump();

        var attach1 = world.GetEntity(region1, attach1LocalId)!;
        var attach2 = world.GetEntity(region1, attach2LocalId)!;

        // Disconnect region 1: keeps agent + both attachments awaiting reconfirmation
        session.RaiseRegionDisconnected(new RegionDisconnectedEvent(region1));
        simulation.Pump();

        Assert.True(attach1.GetComponent<AttachmentComponent>()!.AwaitingReconfirmation);
        Assert.True(attach2.GetComponent<AttachmentComponent>()!.AwaitingReconfirmation);

        // Region 2 arrives: only re-confirms attach1 (attach2 was detached during teleport)
        ulong region2 = 2000ul;
        uint agentLocalId2 = 50;
        uint attach1LocalId2 = 61;

        session.RaiseAvatarUpdate(new AvatarUpdateEvent(
            region2, agentLocalId2, agentId, Vector3.Zero, Quaternion.Identity,
            "Test", "User", IsLocalAgent: true, IsTeleport: true));
        session.RaiseObjectUpdate(new ObjectUpdateEvent(
            region2, attach1LocalId2, Vector3.Zero, Quaternion.Identity, Vector3.One,
            1, false, Guid.Empty, Guid.Empty, Guid.Empty, Vector4.One,
            ParentLocalId: agentLocalId2, AttachmentPoint: 1, ObjectId: attach1Uuid));
        simulation.Pump();


        Assert.False(attach1.GetComponent<AttachmentComponent>()!.AwaitingReconfirmation);
        Assert.True(attach2.GetComponent<AttachmentComponent>()!.AwaitingReconfirmation);

        // Simulate quiet window expiration (elapsed = 6.0s >= 5.0s, quiet = 4.0s >= 3.0s)
        simulation.CheckReconciliationProgress(elapsedOverride: 6.0, quietSecondsOverride: 4.0);

        // attach1 survived at new key; attach2 unconfirmed item was removed
        Assert.NotNull(world.GetEntity(region2, attach1LocalId2));
        Assert.Null(world.GetEntity(region1, attach2LocalId));
        Assert.Null(world.GetEntity(attach2.Id));
    }
}
