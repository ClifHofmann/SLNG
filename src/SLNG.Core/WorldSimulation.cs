using System.Collections.Concurrent;
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
        _source.ObjectRemovedReceived += OnObjectRemoved;
        _source.TerrainPatchReceived += OnTerrainPatch;
        _source.RegionDisconnectedReceived += OnRegionDisconnected;
    }

    // These run on background network threads: enqueue only, never touch the world.
    private void OnObjectUpdate(object? sender, ObjectUpdateEvent e) => _pending.Enqueue(e);
    private void OnObjectRemoved(object? sender, ObjectRemovedEvent e) => _pending.Enqueue(e);
    private void OnTerrainPatch(object? sender, TerrainPatchEvent e) => _pending.Enqueue(e);
    private void OnRegionDisconnected(object? sender, RegionDisconnectedEvent e) => _pending.Enqueue(e);

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
                case ObjectRemovedEvent e: _world.RemoveEntity(e.RegionHandle, e.LocalId); break;
                case TerrainPatchEvent e: ApplyTerrainPatch(e); break;
                case RegionDisconnectedEvent e: _world.RemoveRegion(e.RegionHandle); break;
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
            // Update existing prim visual state
            prim.Scale = e.Scale;
            prim.ProfileCurve = e.ProfileCurve;
            prim.IsMesh = e.IsMesh;
            prim.MeshId = e.MeshId;
            prim.TextureId = e.TextureId;
            prim.RenderMaterialId = e.RenderMaterialId;
            prim.ColorTint = e.ColorTint;
        }
        _world.NotifyComponentUpdated(entity, prim);
    }

    private void ApplyTerrainPatch(TerrainPatchEvent e)
    {
        var terrain = _world.GetOrCreateTerrain(e.RegionHandle);
        terrain.ApplyPatch(e.X, e.Y, e.HeightMap);
        _world.NotifyTerrainUpdated(e.RegionHandle);
    }

    public void Dispose()
    {
        _source.ObjectUpdateReceived -= OnObjectUpdate;
        _source.ObjectRemovedReceived -= OnObjectRemoved;
        _source.TerrainPatchReceived -= OnTerrainPatch;
        _source.RegionDisconnectedReceived -= OnRegionDisconnected;
    }
}
