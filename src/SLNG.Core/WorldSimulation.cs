using SLNG.Core.Components;
using SLNG.Core.ECS;
using SLNG.Net;

namespace SLNG.Core;

/// <summary>
/// Bridges the networking layer with the ECS world.
/// Listens to GridSession events and mutates the ECS world state accordingly.
/// </summary>
public class WorldSimulation : IDisposable
{
    private readonly World _world;
    private readonly GridSession _session;

    public WorldSimulation(World world, GridSession session)
    {
        _world = world;
        _session = session;

        _session.ObjectUpdateReceived += OnObjectUpdateReceived;
        _session.ObjectRemovedReceived += OnObjectRemovedReceived;
        _session.TerrainPatchReceived += OnTerrainPatchReceived;
        _session.RegionDisconnectedReceived += OnRegionDisconnectedReceived;
    }

    private void OnObjectUpdateReceived(object? sender, ObjectUpdateEvent e)
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
            prim = new PrimitiveComponent(e.Scale, e.ProfileCurve, e.IsMesh, e.MeshId);
            entity.SetComponent(prim);
        }
        else
        {
            prim.Scale = e.Scale;
            prim.ProfileCurve = e.ProfileCurve;
            prim.IsMesh = e.IsMesh;
            prim.MeshId = e.MeshId;
            _world.NotifyComponentUpdated(entity, prim);
        }
    }

    private void OnObjectRemovedReceived(object? sender, ObjectRemovedEvent e)
    {
        _world.RemoveEntity(e.RegionHandle, e.LocalId);
    }

    private void OnTerrainPatchReceived(object? sender, TerrainPatchEvent e)
    {
        var terrain = _world.GetOrCreateTerrain(e.RegionHandle);
        terrain.ApplyPatch(e.X, e.Y, e.HeightMap);
        _world.NotifyTerrainUpdated(e.RegionHandle);
    }

    private void OnRegionDisconnectedReceived(object? sender, RegionDisconnectedEvent e)
    {
        _world.RemoveRegion(e.RegionHandle);
    }

    public void Dispose()
    {
        _session.ObjectUpdateReceived -= OnObjectUpdateReceived;
        _session.ObjectRemovedReceived -= OnObjectRemovedReceived;
        _session.TerrainPatchReceived -= OnTerrainPatchReceived;
        _session.RegionDisconnectedReceived -= OnRegionDisconnectedReceived;
    }
}
