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
/// Event arguments for entity re-keying events (same entity, new regionHandle / localId).
/// </summary>
public class EntityRekeyedEventArgs : EventArgs
{
    public Entity Entity { get; }
    public ulong OldRegionHandle { get; }
    public uint OldLocalId { get; }
    public ulong NewRegionHandle { get; }
    public uint NewLocalId { get; }

    public EntityRekeyedEventArgs(Entity entity, ulong oldRegionHandle, uint oldLocalId, ulong newRegionHandle, uint newLocalId)
    {
        Entity = entity;
        OldRegionHandle = oldRegionHandle;
        OldLocalId = oldLocalId;
        NewRegionHandle = newRegionHandle;
        NewLocalId = newLocalId;
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
    public event EventHandler<EntityRekeyedEventArgs>? EntityRekeyed;
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

    /// <summary>FEAT-UI-05: everything currently selected. The renderer needs to rebuild the
    /// selection highlight from scratch after a link or an unlink, because which prim is a root
    /// and which is a child -- and therefore what colour it draws in -- has just changed
    /// underneath a selection nobody touched.</summary>
    public IReadOnlyCollection<System.Guid> SelectedIds => _selectedIds;

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
    /// Moves an existing entity to a new (regionHandle, localId) key in the index,
    /// keeping its Entity.Id, components, and instance identity intact.
    /// Fires <see cref="EntityRekeyed"/> instead of EntityRemoved + EntityAdded.
    /// </summary>
    public bool RekeyEntity(Entity entity, ulong newRegionHandle, uint newLocalId)
    {
        if (entity == null) throw new ArgumentNullException(nameof(entity));
        if (!_entities.ContainsKey(entity.Id)) return false;

        var oldKey = (entity.RegionHandle, entity.LocalId);
        var newKey = (newRegionHandle, newLocalId);
        if (oldKey == newKey) return true;

        if (_entityIndex.TryGetValue(newKey, out var existingId) && existingId != entity.Id)
        {
            RemoveEntity(newRegionHandle, newLocalId);
        }

        _entityIndex.Remove(oldKey);
        entity.Rekey(newRegionHandle, newLocalId);
        _entityIndex[newKey] = entity.Id;

        EntityRekeyed?.Invoke(this, new EntityRekeyedEventArgs(entity, oldKey.Item1, oldKey.Item2, newRegionHandle, newLocalId));
        return true;
    }

    /// <summary>
    /// Removes all entities and terrain associated with a specific region.
    /// </summary>
    public void RemoveRegion(ulong regionHandle)
    {
        // BUG-NET-13 & FEAT-NET-06: never delete the local agent or its attachment tree
        // (worn items, HUDs, and their child prims) as a side effect of unloading a region.
        // The local agent and worn attachments move with the agent, not with the sim.
        // We preserve them and mark attachments as awaiting re-confirmation.
        var localAgent = _entities.Values.FirstOrDefault(e => e.GetComponent<AvatarComponent>()?.IsLocalAgent == true);
        var preservedIds = new HashSet<Guid>();
        if (localAgent != null)
        {
            preservedIds.Add(localAgent.Id);

            foreach (var e in _entities.Values)
            {
                if (e.GetComponent<AttachmentComponent>()?.AvatarEntityId == localAgent.Id)
                {
                    preservedIds.Add(e.Id);
                }
            }

            bool expanded = true;
            while (expanded)
            {
                expanded = false;
                foreach (var e in _entities.Values)
                {
                    if (preservedIds.Contains(e.Id)) continue;
                    if (e.RegionHandle != regionHandle) continue;
                    var t = e.GetComponent<TransformComponent>();
                    if (t != null && t.ParentLocalId != 0)
                    {
                        if (_entityIndex.TryGetValue((regionHandle, t.ParentLocalId), out var parentId)
                            && preservedIds.Contains(parentId))
                        {
                            preservedIds.Add(e.Id);
                            expanded = true;
                        }
                    }
                }
            }

            foreach (var id in preservedIds)
            {
                if (id == localAgent.Id) continue;
                if (_entities.TryGetValue(id, out var entity) && entity.RegionHandle == regionHandle)
                {
                    var att = entity.GetComponent<AttachmentComponent>();
                    if (att != null)
                    {
                        att.AwaitingReconfirmation = true;
                    }
                    else
                    {
                        entity.SetComponent(new AttachmentComponent(localAgent.Id, 0) { AwaitingReconfirmation = true });
                    }
                }
            }
        }

        var toRemove = _entities.Values
            .Where(e => e.RegionHandle == regionHandle && !preservedIds.Contains(e.Id))
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
    /// <see cref="RemoveRegion"/> for every region the world holds entities or terrain for — the
    /// local agent stays, as it does there.
    /// </summary>
    public void RemoveAllRegions()
    {
        var regions = _entities.Values.Select(e => e.RegionHandle)
            .Concat(_terrains.Keys)
            .Distinct()
            .ToList();
        foreach (var region in regions) RemoveRegion(region);
    }

    /// <summary>Puts a terrain back that <see cref="RemoveRegion"/> took out, for a region that is
    /// not holding one now. Returns whether it was put back.</summary>
    public bool RestoreTerrain(ulong regionHandle, RegionTerrain terrain)
    {
        if (_terrains.ContainsKey(regionHandle)) return false;
        _terrains[regionHandle] = terrain;
        NotifyTerrainUpdated(regionHandle);
        return true;
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
        FullScans++;
        return _entities.Values;
    }

    /// <summary>BUG-PERF-10: how many times a caller asked for the whole world to walk over
    /// (<see cref="GetAllEntities"/>, <see cref="Query{T}"/>). A test seam: the thing that must not grow
    /// with the number of objects streaming in is this.</summary>
    internal long FullScans { get; private set; }

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
        FullScans++;
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
