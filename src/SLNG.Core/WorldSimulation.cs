using System.Collections.Concurrent;
using System.Linq;
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
    }

    // These run on background network threads: enqueue only, never touch the world.
    private void OnObjectUpdate(object? sender, ObjectUpdateEvent e) => _pending.Enqueue(e);
    private void OnAvatarUpdate(object? sender, AvatarUpdateEvent e) => _pending.Enqueue(e);
    private void OnObjectRemoved(object? sender, ObjectRemovedEvent e) => _pending.Enqueue(e);
    private void OnTerrainPatch(object? sender, TerrainPatchEvent e) => _pending.Enqueue(e);
    private void OnTerrainSettings(object? sender, TerrainSettingsEvent e) => _pending.Enqueue(e);
    private void OnRegionDisconnected(object? sender, RegionDisconnectedEvent e) => _pending.Enqueue(e);
    private void OnAvatarAppearance(object? sender, AvatarAppearanceEvent e) => _pending.Enqueue(e);

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
            }
        }
    }

    private void ApplyObjectUpdate(ObjectUpdateEvent e)
    {
        var entity = _world.GetOrCreateEntity(e.RegionHandle, e.LocalId);

        if (!entity.HasComponent<TransformComponent>())
        {
            var transform = new TransformComponent(e.Position, e.Rotation);
            entity.SetComponent(transform);
            _world.NotifyComponentUpdated(entity, transform);
        }
        else
        {
            var transform = entity.GetComponent<TransformComponent>()!;
            transform.Position = e.Position;
            transform.Rotation = e.Rotation;
            _world.NotifyComponentUpdated(entity, transform);
        }

        var prim = entity.GetComponent<PrimitiveComponent>();
        if (prim == null)
        {
            prim = new PrimitiveComponent(e.Scale, e.ProfileCurve, e.IsMesh, e.MeshId, e.TextureId, e.RenderMaterialId, e.ColorTint);
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
            entity.SetComponent(prim);
        }
        _world.NotifyComponentUpdated(entity, prim);
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
    }
}
