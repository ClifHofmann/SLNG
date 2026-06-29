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
    }

    // These run on background network threads: enqueue only, never touch the world.
    private void OnObjectUpdate(object? sender, ObjectUpdateEvent e) => _pending.Enqueue(e);
    private void OnAvatarUpdate(object? sender, AvatarUpdateEvent e) => _pending.Enqueue(e);
    private void OnObjectRemoved(object? sender, ObjectRemovedEvent e) => _pending.Enqueue(e);
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
            prim = new PrimitiveComponent(e.Scale, e.ProfileCurve, e.IsMesh, e.MeshId, e.TextureId, e.RenderMaterialId, e.ColorTint, e.Shape, e.IsSculpt, e.SculptId, e.SculptType);
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
            prim.Shape = e.Shape;
            prim.IsSculpt = e.IsSculpt;
            prim.SculptId = e.SculptId;
            prim.SculptType = e.SculptType;
            entity.SetComponent(prim);
        }
        _world.NotifyComponentUpdated(entity, prim);

        // When the object has a parent, check if the parent is an avatar; if so mark
        // this entity as an attachment so the renderer can wire it to the correct bone.
        if (e.ParentLocalId != 0)
        {
            var parentEntity = _world.GetEntity(e.RegionHandle, e.ParentLocalId);
            if (parentEntity?.GetComponent<AvatarComponent>() != null)
            {
                var attachment = entity.GetComponent<AttachmentComponent>();
                if (attachment == null)
                {
                    attachment = new AttachmentComponent(parentEntity.Id, e.AttachmentPoint);
                    entity.SetComponent(attachment);
                }
                else
                {
                    attachment.AvatarEntityId = parentEntity.Id;
                    attachment.AttachmentPoint = e.AttachmentPoint;
                }
                _world.NotifyComponentUpdated(entity, attachment);
            }
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
        var terrain = _world.GetOrCreateTerrain(e.RegionHandle);
        terrain.ApplyPatch(e.X, e.Y, e.HeightMap);
        _world.NotifyTerrainUpdated(e.RegionHandle);
    }

    private void ApplyTerrainSettings(TerrainSettingsEvent e)
    {
        var terrain = _world.GetOrCreateTerrain(e.RegionHandle);
        terrain.TerrainDetail0 = e.Detail0;
        terrain.TerrainDetail1 = e.Detail1;
        terrain.TerrainDetail2 = e.Detail2;
        terrain.TerrainDetail3 = e.Detail3;
        
        Array.Copy(e.StartHeights, terrain.TerrainStartHeights, 4);
        Array.Copy(e.HeightRanges, terrain.TerrainHeightRanges, 4);
        
        terrain.WaterHeight = e.WaterHeight;
        
        _world.NotifyTerrainSettingsUpdated(e.RegionHandle);
    }

    public void Dispose()
    {
        _source.ObjectUpdateReceived -= OnObjectUpdate;
        _source.ObjectRemovedReceived -= OnObjectRemoved;
        _source.TerrainPatchReceived -= OnTerrainPatch;
        _source.TerrainSettingsReceived -= OnTerrainSettings;
        _source.RegionDisconnectedReceived -= OnRegionDisconnected;
        _source.AvatarAnimationReceived -= OnAvatarAnimation;
    }
}
