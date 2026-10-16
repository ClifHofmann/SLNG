using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using SLNG.Core;

namespace SLNG.App;

/// <summary>FEAT-PERF-20 Phase A1: shared-material cache for prim faces.
/// When <see cref="RenderConfig.ShareMaterials"/> is enabled, faces with identical
/// <see cref="PrimMaterialKey"/> share the same <see cref="ShaderMaterial"/> instance.
/// Reference counted and freed when unused.
/// Copy-on-write is used when a surface needs private modifications (texanim, mirror, selection, MOAP).</summary>
public sealed class PrimMaterialCache
{
    public sealed class Entry
    {
        public PrimMaterialKey Key { get; }
        public ShaderMaterial Material { get; set; }
        public List<Guid> UsedTextures { get; set; }
        public int RefCount { get; set; }
        public Task<(ShaderMaterial Material, List<Guid> Used)>? InFlightTask { get; set; }

        public Entry(PrimMaterialKey key, ShaderMaterial material, List<Guid>? usedTextures = null)
        {
            Key = key;
            Material = material;
            UsedTextures = usedTextures ?? new List<Guid>();
            RefCount = 1;
        }
    }

    private readonly Dictionary<PrimMaterialKey, Entry> _entries = new();

    /// <summary>Number of distinct shared material entries currently cached.</summary>
    public int Count => _entries.Count;

    /// <summary>Checks whether a shared material entry exists for the given key.</summary>
    public bool TryGet(in PrimMaterialKey key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Entry? entry) => _entries.TryGetValue(key, out entry);

    /// <summary>Increments the ref count and returns the shared material for an existing entry.</summary>
    public ShaderMaterial Acquire(in PrimMaterialKey key)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            entry.RefCount++;
            return entry.Material;
        }
        throw new InvalidOperationException($"Cannot acquire non-existent material for key: {key}");
    }

    /// <summary>Adds or registers an in-flight build task for a key.</summary>
    public void SetInFlight(in PrimMaterialKey key, Task<(ShaderMaterial Material, List<Guid> Used)> task)
    {
        if (_entries.TryGetValue(key, out var existing))
        {
            existing.InFlightTask = task;
            return;
        }
        var entry = new Entry(key, null!)
        {
            InFlightTask = task,
            RefCount = 1
        };
        _entries[key] = entry;
    }

    /// <summary>Registers a completed material and its used textures for a key.</summary>
    public Entry Register(in PrimMaterialKey key, ShaderMaterial material, List<Guid> usedTextures)
    {
        if (_entries.TryGetValue(key, out var existing))
        {
            existing.Material = material;
            existing.UsedTextures = usedTextures;
            existing.InFlightTask = null;
            return existing;
        }

        var entry = new Entry(key, material, usedTextures);
        _entries[key] = entry;
        return entry;
    }

    /// <summary>Releases one reference to the material for the given key. If RefCount reaches 0, removes it from cache.</summary>
    public bool Release(in PrimMaterialKey key)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            entry.RefCount--;
            if (entry.RefCount <= 0)
            {
                _entries.Remove(key);
                return true;
            }
        }
        return false;
    }

    /// <summary>Removes an entry directly (e.g. if an in-flight build fails).</summary>
    public bool Remove(in PrimMaterialKey key) => _entries.Remove(key);

    /// <summary>Clears the entire cache.</summary>
    public void Clear() => _entries.Clear();
}
