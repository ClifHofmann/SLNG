using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using SLNG.Core.Components;
using SLNG.Core.ECS;

namespace SLNG.Core;

/// <summary>
/// Bridges a world-event source (the networking layer) with the ECS world.
///
/// World events arrive on LibreMetaverse's background network threads, so the handlers
/// only <b>enqueue</b> them. The queued mutations are applied to the (deliberately
/// non-thread-safe) <see cref="World"/> by <see cref="Pump"/>, which the host calls once
/// per frame on the main thread — making world mutation single-threaded. The core
/// depends on <see cref="IWorldEventSource"/>, never on the networking layer.
/// </summary>
public sealed class WorldSimulation : IDisposable
{
    private readonly World _world;
    private readonly IWorldEventSource _source;
    private readonly ConcurrentQueue<IWorldEvent> _pending = new();

    // (region, parentLocalId) -> child entity ids, so a linkset root's children can be
    // re-composed when the root arrives or moves. Touched only on the pump thread.
    private readonly Dictionary<(ulong, uint), HashSet<System.Guid>> _children = new();

    public WorldSimulation(World world, IWorldEventSource source)
    {
        _world = world;
        _source = source;

        _source.ObjectUpdateReceived += OnObjectUpdate;
        _source.AvatarUpdateReceived += OnAvatarUpdate;
        _source.ObjectRemovedReceived += OnObjectRemoved;
        _source.TerrainPatchReceived += OnTerrainPatch;
        _source.TerrainSettingsReceived += OnTerrainSettings;
        _source.RegionDisconnectedReceived += OnRegionDisconnected;
        _source.AvatarAppearanceReceived += OnAvatarAppearance;
        _source.AvatarAnimationReceived += OnAvatarAnimation;
        _source.ObjectPropertiesReceived += OnObjectProperties;
    }

    // These run on background network threads: enqueue only, never touch the world.
    private void OnObjectUpdate(object? sender, ObjectUpdateEvent e) => _pending.Enqueue(e);
    private void OnAvatarUpdate(object? sender, AvatarUpdateEvent e) => _pending.Enqueue(e);
    private void OnObjectRemoved(object? sender, ObjectRemovedEvent e) => _pending.Enqueue(e);
    private void OnObjectProperties(object? sender, ObjectPropertiesEvent e) => _pending.Enqueue(e);
    private void OnTerrainPatch(object? sender, TerrainPatchEvent e) => _pending.Enqueue(e);
    private void OnTerrainSettings(object? sender, TerrainSettingsEvent e) => _pending.Enqueue(e);
    private void OnRegionDisconnected(object? sender, RegionDisconnectedEvent e) => _pending.Enqueue(e);
    private void OnAvatarAppearance(object? sender, AvatarAppearanceEvent e) => _pending.Enqueue(e);
    private void OnAvatarAnimation(object? sender, AvatarAnimationEvent e) => _pending.Enqueue(e);

    /// <summary>
    /// Applies all queued world events to the world. Call once per frame on the main
    /// thread; this is the only place that mutates the world.
    /// </summary>
    public void Pump()
    {
        while (_pending.TryDequeue(out var evt))
        {
            switch (evt)
            {
                case ObjectUpdateEvent e: ApplyObjectUpdate(e); break;
                case AvatarUpdateEvent e: ApplyAvatarUpdate(e); break;
                case ObjectRemovedEvent e: _world.RemoveEntity(e.RegionHandle, e.LocalId); break;
                case ObjectPropertiesEvent e: ApplyObjectProperties(e); break;
                case TerrainPatchEvent e: ApplyTerrainPatch(e); break;
                case TerrainSettingsEvent e: ApplyTerrainSettings(e); break;
                case RegionDisconnectedEvent e: _world.RemoveRegion(e.RegionHandle); break;
                case AvatarAppearanceEvent e: ApplyAvatarAppearance(e); break;
                case AvatarAnimationEvent e: ApplyAvatarAnimation(e); break;
            }
        }
    }

    private void ApplyObjectUpdate(ObjectUpdateEvent e)
    {
        var entity = _world.GetOrCreateEntity(e.RegionHandle, e.LocalId);

        var transform = entity.GetComponent<TransformComponent>() ?? new TransformComponent();
        transform.LocalPosition = e.Position;
        transform.LocalRotation = e.Rotation;
        transform.ParentLocalId = e.ParentLocalId;
        // Linked child prims send their transform relative to the root; compose to world space.
        ResolveWorldTransform(transform, e.RegionHandle);
        entity.SetComponent(transform);
        _world.NotifyComponentUpdated(entity, transform);

        // Index this entity under its parent so the parent can re-compose it later, and
        // re-compose any children already waiting on this entity (handles either arrival order).
        if (e.ParentLocalId != 0)
        {
            var key = (e.RegionHandle, e.ParentLocalId);
            if (!_children.TryGetValue(key, out var set)) _children[key] = set = new HashSet<System.Guid>();
            set.Add(entity.Id);
        }
        RecomposeChildren(e.RegionHandle, e.LocalId);

        var prim = entity.GetComponent<PrimitiveComponent>();
        if (prim == null)
        {
            prim = new PrimitiveComponent(e.Scale, e.ProfileCurve, e.IsMesh, e.MeshId, e.TextureId, e.RenderMaterialId, e.ColorTint, e.RepeatU, e.RepeatV, e.OffsetU, e.OffsetV, e.TextureRotation, e.Shape, e.IsSculpt, e.SculptId, e.SculptType, e.Faces);
            entity.SetComponent(prim);
        }
        else
        {
            prim.Scale = e.Scale;
            prim.ProfileCurve = e.ProfileCurve;
            prim.IsMesh = e.IsMesh;
            prim.MeshId = e.MeshId;
            prim.TextureId = e.TextureId;
            prim.RenderMaterialId = e.RenderMaterialId;
            prim.ColorTint = e.ColorTint;
            prim.RepeatU = e.RepeatU;
            prim.RepeatV = e.RepeatV;
            prim.OffsetU = e.OffsetU;
            prim.OffsetV = e.OffsetV;
            prim.Rotation = e.TextureRotation;
            prim.Shape = e.Shape;
            prim.IsSculpt = e.IsSculpt;
            prim.SculptId = e.SculptId;
            prim.SculptType = e.SculptType;
            prim.Faces = e.Faces;
            entity.SetComponent(prim);
        }
        prim.AttachmentPoint = e.AttachmentPoint;
        _world.NotifyComponentUpdated(entity, prim);

        // An attachment is any object whose parent is EITHER an avatar directly, OR another
        // object that is itself already an attachment (recursively). A detailed mesh product
        // (e.g. a Bento mesh head) commonly ships as dozens of LINKED prims — one root plus many
        // children — and every rigged child needs to be treated as its own independent
        // attachment for rendering: a rigged mesh binds straight to the avatar skeleton via its
        // own skin data, completely ignoring prim-hierarchy position, so there is no reason to
        // exclude children the way a naive "only the root is an attachment" rule does. Excluding
        // them (the previous rule here) silently dropped every child prim from rendering —
        // for one real asset, 21 of 22 linked prims (the entire head shell, eyes, most of the
        // teeth) never rendered, leaving only the root prim visible.
        // If the parent isn't resolved yet (arrived out of order), PropagateAttachment (called
        // from LinkPendingAttachments and from the two branches below) picks this up once
        // whichever ancestor resolves the chain down to this entity.
        if (e.ParentLocalId != 0)
        {
            var parentEntity = _world.GetEntity(e.RegionHandle, e.ParentLocalId);
            var parentAvatar = parentEntity?.GetComponent<AvatarComponent>();
            var parentAttachment = parentEntity?.GetComponent<AttachmentComponent>();
            if (parentAvatar != null)
                SetAttachment(entity, parentEntity!.Id, e.AttachmentPoint);
            else if (parentAttachment != null)
                SetAttachment(entity, parentAttachment.AvatarEntityId, parentAttachment.AttachmentPoint);
        }
    }

    private void SetAttachment(Entity entity, System.Guid avatarEntityId, byte attachmentPoint)
    {
        var attachment = entity.GetComponent<AttachmentComponent>();
        bool changed = attachment == null || attachment.AvatarEntityId != avatarEntityId || attachment.AttachmentPoint != attachmentPoint;
        if (attachment == null)
        {
            attachment = new AttachmentComponent(avatarEntityId, attachmentPoint);
            entity.SetComponent(attachment);
        }
        else
        {
            attachment.AvatarEntityId = avatarEntityId;
            attachment.AttachmentPoint = attachmentPoint;
        }
        _world.NotifyComponentUpdated(entity, attachment);

        // Cascade to any children already waiting on this entity — they may have streamed in
        // before this entity itself became an attachment (out-of-order arrival is common: a
        // linkset's prims and the avatar itself can each arrive in any order).
        if (changed) PropagateAttachmentToChildren(entity.RegionHandle, entity.LocalId, avatarEntityId, attachmentPoint);
    }

    /// <summary>Pushes attachment status down to every child already indexed under
    /// <paramref name="localId"/>, recursively — see the cascading-attachment comment in
    /// <see cref="ApplyObjectUpdate"/> for why every level of a linked attachment needs this,
    /// not just the immediate children of the avatar.</summary>
    private void PropagateAttachmentToChildren(ulong region, uint localId, System.Guid avatarEntityId, byte attachmentPoint)
    {
        if (!_children.TryGetValue((region, localId), out var set) || set.Count == 0) return;

        foreach (var childId in set.ToList())
        {
            var child = _world.GetEntity(childId);
            if (child == null) continue;
            var existing = child.GetComponent<AttachmentComponent>();
            if (existing != null && existing.AvatarEntityId == avatarEntityId && existing.AttachmentPoint == attachmentPoint) continue;

            SetAttachment(child, avatarEntityId, attachmentPoint);
        }
    }

    /// <summary>Wires up attachment roots that streamed in before their wearer's avatar entity
    /// existed. The avatar's direct children (indexed by parent local id) are exactly its
    /// attachment roots; each root's own children cascade via <see cref="SetAttachment"/>.
    /// Called when an avatar appears/updates.</summary>
    private void LinkPendingAttachments(ulong region, uint avatarLocalId, System.Guid avatarEntityId)
    {
        if (!_children.TryGetValue((region, avatarLocalId), out var set) || set.Count == 0) return;

        foreach (var childId in set.ToList())
        {
            var child = _world.GetEntity(childId);
            if (child == null) continue;
            if (child.GetComponent<AttachmentComponent>()?.AvatarEntityId == avatarEntityId) continue;

            byte point = child.GetComponent<PrimitiveComponent>()?.AttachmentPoint ?? 0;
            SetAttachment(child, avatarEntityId, point);
        }
    }

    /// <summary>Computes a transform's world-space Position/Rotation from its parent (linkset
    /// root). Roots, avatar attachments, and orphans (parent not yet present) stay at their
    /// local values.</summary>
    private void ResolveWorldTransform(TransformComponent t, ulong region)
    {
        if (t.ParentLocalId == 0)
        {
            t.Position = t.LocalPosition;
            t.Rotation = t.LocalRotation;
            return;
        }

        var parent = _world.GetEntity(region, t.ParentLocalId);
        var parentT = parent?.GetComponent<TransformComponent>();
        if (parent == null || parentT == null || parent.GetComponent<AvatarComponent>() != null)
        {
            // No prim parent (yet) — leave local; avatar attachments are placed via bones.
            t.Position = t.LocalPosition;
            t.Rotation = t.LocalRotation;
            return;
        }

        t.Rotation = parentT.Rotation * t.LocalRotation;
        t.Position = parentT.Position + Vector3.Transform(t.LocalPosition, parentT.Rotation);
    }

    /// <summary>Re-composes the world transform of every child linked to <paramref name="localId"/>
    /// — used when a linkset root arrives or moves so its children follow.</summary>
    private void RecomposeChildren(ulong region, uint localId)
    {
        if (!_children.TryGetValue((region, localId), out var set) || set.Count == 0) return;

        foreach (var childId in set.ToList())
        {
            var child = _world.GetEntity(childId);
            var ct = child?.GetComponent<TransformComponent>();
            if (child == null || ct == null) continue;
            ResolveWorldTransform(ct, region);
            _world.NotifyComponentUpdated(child, ct);
        }
    }

    private void ApplyAvatarUpdate(AvatarUpdateEvent e)
    {
        var entity = _world.GetOrCreateEntity(e.RegionHandle, e.LocalId);

        var transform = entity.GetComponent<TransformComponent>();
        if (transform == null)
        {
            transform = new TransformComponent(e.Position, e.Rotation);
            entity.SetComponent(transform);
        }
        else
        {
            transform.Position = e.Position;
            transform.Rotation = e.Rotation;
            entity.SetComponent(transform);
        }
        _world.NotifyComponentUpdated(entity, transform);

        var avatar = entity.GetComponent<AvatarComponent>();
        if (avatar == null)
        {
            avatar = new AvatarComponent(e.AgentId, e.FirstName, e.LastName, e.IsLocalAgent);
            entity.SetComponent(avatar);
        }
        else
        {
            avatar.AgentId = e.AgentId;
            avatar.FirstName = e.FirstName;
            avatar.LastName = e.LastName;
            avatar.IsLocalAgent = e.IsLocalAgent;
            entity.SetComponent(avatar);
        }
        _world.NotifyComponentUpdated(entity, avatar);

        // Link any worn mesh that arrived before this avatar entity existed.
        LinkPendingAttachments(e.RegionHandle, e.LocalId, entity.Id);
    }

    private void ApplyAvatarAppearance(AvatarAppearanceEvent e)
    {
        var entity = _world.Query<AvatarComponent>()
            .FirstOrDefault(ent => ent.GetComponent<AvatarComponent>()?.AgentId == e.AgentId);

        if (entity != null)
        {
            var avatar = entity.GetComponent<AvatarComponent>()!;
            avatar.VisualParams = e.VisualParams;
            avatar.BakedTextures = e.BakedTextures;
            entity.SetComponent(avatar);
            _world.NotifyComponentUpdated(entity, avatar);
        }
    }

    private void ApplyAvatarAnimation(AvatarAnimationEvent e)
    {
        var entity = _world.Query<AvatarComponent>()
            .FirstOrDefault(ent => ent.GetComponent<AvatarComponent>()?.AgentId == e.AgentId);

        if (entity != null)
        {
            var avatar = entity.GetComponent<AvatarComponent>()!;
            avatar.ActiveAnimations = e.AnimationIds;
            entity.SetComponent(avatar);
            _world.NotifyComponentUpdated(entity, avatar);
        }
    }

    private void ApplyTerrainPatch(TerrainPatchEvent e)
    {
        var terrain = _world.GetOrCreateTerrain(e.RegionHandle, e.RegionSizeX, e.RegionSizeY);
        terrain.ApplyPatch(e.X, e.Y, e.HeightMap);
        _world.NotifyTerrainUpdated(e.RegionHandle);
    }

    private void ApplyTerrainSettings(TerrainSettingsEvent e)
    {
        var terrain = _world.GetOrCreateTerrain(e.RegionHandle, e.RegionSizeX, e.RegionSizeY);
        terrain.TerrainDetail0 = e.Detail0;
        terrain.TerrainDetail1 = e.Detail1;
        terrain.TerrainDetail2 = e.Detail2;
        terrain.TerrainDetail3 = e.Detail3;

        Array.Copy(e.StartHeights, terrain.TerrainStartHeights, 4);
        Array.Copy(e.HeightRanges, terrain.TerrainHeightRanges, 4);

        terrain.WaterHeight = e.WaterHeight;

        _world.NotifyTerrainSettingsUpdated(e.RegionHandle);
    }

    private void ApplyObjectProperties(ObjectPropertiesEvent e)
    {
        var entity = _world.GetEntity(e.ObjectId);
        if (entity == null) return;

        var meta = entity.GetComponent<MetadataComponent>();
        if (meta == null)
        {
            meta = new MetadataComponent(e.ObjectId);
            entity.SetComponent(meta);
        }

        meta.Name = e.Name;
        meta.Description = e.Description;
        meta.CreatorId = e.CreatorId;
        meta.OwnerId = e.OwnerId;
        meta.GroupId = e.GroupId;

        _world.NotifyComponentUpdated(entity, meta);
    }

    public void Dispose()
    {
        _source.ObjectUpdateReceived -= OnObjectUpdate;
        _source.AvatarUpdateReceived -= OnAvatarUpdate;
        _source.ObjectRemovedReceived -= OnObjectRemoved;
        _source.ObjectPropertiesReceived -= OnObjectProperties;
        _source.TerrainPatchReceived -= OnTerrainPatch;
        _source.TerrainSettingsReceived -= OnTerrainSettings;
        _source.RegionDisconnectedReceived -= OnRegionDisconnected;
        _source.AvatarAppearanceReceived -= OnAvatarAppearance;
        _source.AvatarAnimationReceived -= OnAvatarAnimation;
    }
}
