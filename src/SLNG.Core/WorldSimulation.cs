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
        _session.TerrainPatchReceived += OnTerrainPatchReceived;
    }

    private void OnObjectUpdateReceived(object? sender, ObjectUpdateEvent e)
    {
        var entity = _world.GetOrCreateEntity(e.LocalId);

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

        if (!entity.HasComponent<PrimitiveComponent>())
        {
            var prim = new PrimitiveComponent(e.Scale, e.ProfileCurve);
            entity.SetComponent(prim);
            _world.NotifyComponentUpdated(entity, prim);
        }
        else
        {
            var prim = entity.GetComponent<PrimitiveComponent>()!;
            prim.Scale = e.Scale;
            prim.ProfileCurve = e.ProfileCurve;
            _world.NotifyComponentUpdated(entity, prim);
        }
    }

    private void OnTerrainPatchReceived(object? sender, TerrainPatchEvent e)
    {
        _world.Terrain.ApplyPatch(e.X, e.Y, e.HeightMap);
        _world.NotifyTerrainUpdated();
    }

    public void Dispose()
    {
        _session.ObjectUpdateReceived -= OnObjectUpdateReceived;
        _session.TerrainPatchReceived -= OnTerrainPatchReceived;
    }
}
