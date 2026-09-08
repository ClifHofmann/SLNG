using System;
using System.Collections.Generic;
using System.Linq;
using SLNG.Core.Components;

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
    private readonly Dictionary<Guid, Entity> _entities = new();
    private readonly Dictionary<(ulong, uint), Guid> _entityIndex = new();
    private readonly Dictionary<ulong, RegionTerrain> _terrains = new();

    public IReadOnlyDictionary<ulong, RegionTerrain> Terrains => _terrains;

    // Events to notify observers (e.g. Godot renderer) about world state changes
    public event EventHandler<EntityEventArgs>? EntityAdded;
    public event EventHandler<EntityEventArgs>? EntityRemoved;
    public event EventHandler<ComponentEventArgs>? ComponentUpdated;
    public event EventHandler<ulong>? TerrainUpdated;
    public event EventHandler<ulong>? TerrainSettingsUpdated;

    // Selection state -- a set, not a single slot: multiple objects can be selected (and
    // edited via independent windows) at once without one selection silently evicting another.
    private readonly HashSet<Guid> _selectedIds = new();
    public IReadOnlyCollection<Guid> SelectedEntityIds => _selectedIds;
    public event EventHandler<EntityEventArgs>? EntitySelected;
    public event EventHandler<EntityEventArgs>? EntityDeselected;

    public bool IsSelected(Entity entity) => _selectedIds.Contains(entity.Id);

    /// <summary>
    /// Gets or creates an entity with the specified RegionHandle and LocalId.
    /// </summary>
    public Entity GetOrCreateEntity(ulong regionHandle, uint localId)
    {
        var key = (regionHandle, localId);
        if (_entityIndex.TryGetValue(key, out var id) && _entities.TryGetValue(id, out var entity))
        {
            return entity;
        }

        entity = new Entity(regionHandle, localId);
        _entities[entity.Id] = entity;
        _entityIndex[key] = entity.Id;
        EntityAdded?.Invoke(this, new EntityEventArgs(entity));

        return entity;
    }

    /// <summary>
    /// Retrieves an entity by its RegionHandle and LocalId, or null if it doesn't exist.
    /// </summary>
    public Entity? GetEntity(ulong regionHandle, uint localId)
    {
        var key = (regionHandle, localId);
        if (_entityIndex.TryGetValue(key, out var id) && _entities.TryGetValue(id, out var entity))
        {
            return entity;
        }
        return null;
    }

    /// <summary>
    /// Retrieves an entity by its global Guid, or null if it doesn't exist.
    /// </summary>
    public Entity? GetEntity(Guid id)
    {
        return _entities.TryGetValue(id, out var entity) ? entity : null;
    }

    /// <summary>
    /// Removes an entity from the world.
    /// </summary>
    public bool RemoveEntity(ulong regionHandle, uint localId)
    {
        var key = (regionHandle, localId);
        if (_entityIndex.TryGetValue(key, out var id) && _entities.TryGetValue(id, out var entity))
        {
            _entities.Remove(id);
            _entityIndex.Remove(key);
            EntityRemoved?.Invoke(this, new EntityEventArgs(entity));
            return true;
        }
        return false;
    }

    /// <summary>
    /// Removes all entities and terrain associated with a specific region.
    /// </summary>
    public void RemoveRegion(ulong regionHandle)
    {
        // BUG-NET-13: never delete the local agent as a side effect of unloading a region. The
        // local agent is the player, not regional content. On a teleport this runs (via the eager
        // BUG-NET-04 cleanup) while the agent entity is still keyed to the region we left, before
        // the destination sim's first local AvatarUpdate re-keys it -- removing it here blanks the
        // self avatar (its AvatarComponent/BakedTextures are gone) and leaves the renderer with no
        // self visual to follow until a fresh AvatarAppearance arrives, which the sim does not
        // reliably re-send after a teleport (BUG-AVATAR-04). WorldSimulation.ApplyAvatarUpdate
        // already removes the stale old-region local agent when the new one arrives, so preserving
        // it here just bridges that gap. A genuine DisableSimulator for a neighbor region (the
        // BUG-NET-03 walking path) never contains the local agent, so this is a no-op there.
        var toRemove = _entities.Values
            .Where(e => e.RegionHandle == regionHandle
                        && e.GetComponent<AvatarComponent>()?.IsLocalAgent != true)
            .ToList();
        foreach (var entity in toRemove)
        {
            _entities.Remove(entity.Id);
            _entityIndex.Remove((regionHandle, entity.LocalId));
            EntityRemoved?.Invoke(this, new EntityEventArgs(entity));
        }

        if (_terrains.Remove(regionHandle))
        {
            NotifyTerrainUpdated(regionHandle);
        }
    }

    /// <summary>
    /// Gets or creates a terrain object for the specified region.
    /// </summary>
    public RegionTerrain GetOrCreateTerrain(ulong regionHandle,
        int sizeX = RegionTerrain.DefaultRegionSize, int sizeY = RegionTerrain.DefaultRegionSize)
    {
        if (!_terrains.TryGetValue(regionHandle, out var terrain))
        {
            // Size to the actual region (varregions are larger than 256). Both the terrain
            // patch and settings events carry the size, so the first to arrive sizes it right.
            terrain = new RegionTerrain(sizeX, sizeY);
            _terrains[regionHandle] = terrain;
        }
        else
        {
            // If a later event reports a larger region, grow to fit (size is only learned from
            // these events, never from raw patch coordinates).
            terrain.EnsureSize(sizeX, sizeY);
        }
        return terrain;
    }

    /// <summary>
    /// Retrieves all entities currently in the world.
    /// </summary>
    /// <summary>Number of entities currently in the world.</summary>
    public int EntityCount => _entities.Count;

    public IEnumerable<Entity> GetAllEntities()
    {
        return _entities.Values;
    }

    /// <summary>
    /// Queries the world for all entities possessing a specific component type.
    /// </summary>
    /// <summary>
    /// All entities carrying component T.
    ///
    /// A full linear scan with a dictionary probe per entity, and it allocates a LINQ iterator per
    /// call. That is fine for occasional lookups and emphatically not fine per frame: measured on a
    /// 24,000-entity region it was the single largest main-thread cost in the client, 226 ms per
    /// second of wall clock. Callers on the frame path must cache the result -- see
    /// WorldSimulation.ExtrapolateMovement -- rather than calling this repeatedly.
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
    public void NotifyTerrainUpdated(ulong regionHandle)
    {
        TerrainUpdated?.Invoke(this, regionHandle);
    }

    public void NotifyTerrainSettingsUpdated(ulong regionHandle)
    {
        TerrainSettingsUpdated?.Invoke(this, regionHandle);
    }

    /// <summary>Adds entity to the selection set. Additive: selecting a new entity does NOT
    /// deselect any other -- callers that want single-select semantics (e.g. "clicking a new
    /// object replaces the highlight") must explicitly deselect the old one themselves.</summary>
    public void SelectEntity(Entity entity)
    {
        if (!_selectedIds.Add(entity.Id)) return; // already selected
        EntitySelected?.Invoke(this, new EntityEventArgs(entity));
    }

    /// <summary>Removes entity from the selection set.</summary>
    public void DeselectEntity(Entity entity)
    {
        if (!_selectedIds.Remove(entity.Id)) return;
        EntityDeselected?.Invoke(this, new EntityEventArgs(entity));
    }
}
