using System;
using System.Collections.Generic;
using System.Linq;

namespace SLNG.Core.ECS;

/// <summary>
/// Event arguments for entity lifecycle events.
/// </summary>
public class EntityEventArgs : EventArgs
{
    public Entity Entity { get; }
    public EntityEventArgs(Entity entity) => Entity = entity;
}

/// <summary>
/// Event arguments for component modification events.
/// </summary>
public class ComponentEventArgs : EventArgs
{
    public Entity Entity { get; }
    public IComponent Component { get; }
    public ComponentEventArgs(Entity entity, IComponent component)
    {
        Entity = entity;
        Component = component;
    }
}

/// <summary>
/// The central registry of all entities in the simulator.
/// </summary>
public class World
{
    private readonly Dictionary<uint, Entity> _entities = new();

    public RegionTerrain Terrain { get; } = new RegionTerrain();

    // Events to notify observers (e.g. Godot renderer) about world state changes
    public event EventHandler<EntityEventArgs>? EntityAdded;
    public event EventHandler<EntityEventArgs>? EntityRemoved;
    public event EventHandler<ComponentEventArgs>? ComponentUpdated;
    public event EventHandler? TerrainUpdated;

    /// <summary>
    /// Gets or creates an entity with the specified LocalId.
    /// </summary>
    public Entity GetOrCreateEntity(uint localId)
    {
        if (!_entities.TryGetValue(localId, out var entity))
        {
            entity = new Entity(localId);
            _entities[localId] = entity;
            EntityAdded?.Invoke(this, new EntityEventArgs(entity));
        }
        return entity;
    }

    /// <summary>
    /// Retrieves an entity by its LocalId, or null if it doesn't exist.
    /// </summary>
    public Entity? GetEntity(uint localId)
    {
        return _entities.TryGetValue(localId, out var entity) ? entity : null;
    }

    /// <summary>
    /// Removes an entity from the world.
    /// </summary>
    public bool RemoveEntity(uint localId)
    {
        if (_entities.TryGetValue(localId, out var entity))
        {
            _entities.Remove(localId);
            EntityRemoved?.Invoke(this, new EntityEventArgs(entity));
            return true;
        }
        return false;
    }

    /// <summary>
    /// Retrieves all entities currently in the world.
    /// </summary>
    public IEnumerable<Entity> GetAllEntities()
    {
        return _entities.Values;
    }

    /// <summary>
    /// Queries the world for all entities possessing a specific component type.
    /// </summary>
    public IEnumerable<Entity> Query<T>() where T : class, IComponent
    {
        return _entities.Values.Where(e => e.HasComponent<T>());
    }

    /// <summary>
    /// Manually triggers a component update notification for observers.
    /// </summary>
    public void NotifyComponentUpdated<T>(Entity entity, T component) where T : class, IComponent
    {
        ComponentUpdated?.Invoke(this, new ComponentEventArgs(entity, component));
    }

    /// <summary>
    /// Manually triggers a terrain update notification for observers.
    /// </summary>
    public void NotifyTerrainUpdated()
    {
        TerrainUpdated?.Invoke(this, EventArgs.Empty);
    }
}
