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

        // We either update an existing transform or create a new one.
        var transform = entity.GetComponent<TransformComponent>();
        if (transform != null)
        {
            transform.Position = e.Position;
            transform.Rotation = e.Rotation;
        }
        else
        {
            transform = new TransformComponent(e.Position, e.Rotation);
            entity.SetComponent(transform);
        }

        // Notify observers that this component has changed so they can update the scene graph.
        _world.NotifyComponentUpdated(entity, transform);
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
