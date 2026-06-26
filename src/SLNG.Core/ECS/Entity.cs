using System;
using System.Collections.Generic;

namespace SLNG.Core.ECS;

/// <summary>
/// Represents an object in the world, holding a collection of components.
/// </summary>
public class Entity
{
    public Guid Id { get; }
    public ulong RegionHandle { get; }
    public uint LocalId { get; }
    private readonly Dictionary<Type, IComponent> _components = new();

    public Entity(ulong regionHandle, uint localId)
    {
        Id = Guid.NewGuid();
        RegionHandle = regionHandle;
        LocalId = localId;
    }

    /// <summary>
    /// Adds or replaces a component on this entity.
    /// </summary>
    public void SetComponent<T>(T component) where T : class, IComponent
    {
        _components[typeof(T)] = component;
    }

    /// <summary>
    /// Retrieves a component of type T. Returns null if not present.
    /// </summary>
    public T? GetComponent<T>() where T : class, IComponent
    {
        if (_components.TryGetValue(typeof(T), out var component))
        {
            return component as T;
        }
        return null;
    }

    /// <summary>
    /// Checks if the entity has a specific component.
    /// </summary>
    public bool HasComponent<T>() where T : class, IComponent
    {
        return _components.ContainsKey(typeof(T));
    }

    /// <summary>
    /// Removes a component of type T.
    /// </summary>
    public bool RemoveComponent<T>() where T : class, IComponent
    {
        return _components.Remove(typeof(T));
    }
}
