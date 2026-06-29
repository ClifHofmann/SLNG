using Godot;
using System;
using System.Collections.Generic;

namespace SLNG.App;

/// <summary>
/// A Godot-side LRU cache for heavy Godot resources like ImageTextures and ArrayMeshes.
/// Prevents the Godot client from leaking VRAM by tracking references and evicting 
/// unused assets when a budget is exceeded.
/// </summary>
public class GpuCache
{
    private class CacheEntry
    {
        public Guid Id;
        public Resource Res = null!;
        public long Size;
        public LinkedListNode<CacheEntry>? Node;
        public int RefCount;
    }

    private readonly Dictionary<Guid, CacheEntry> _cache = new();
    private readonly LinkedList<CacheEntry> _lruList = new();
    
    private long _currentSize = 0;
    private readonly long _maxSize;

    /// <summary>
    /// Initializes a new GpuCache with a VRAM budget.
    /// Default is 256 MB.
    /// </summary>
    public GpuCache(long maxSizeInBytes = 256 * 1024 * 1024)
    {
        _maxSize = maxSizeInBytes;
    }

    public void AddRef(Guid id)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(id, out var entry))
            {
                entry.RefCount++;
            }
        }
    }

    public void ReleaseRef(Guid id)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(id, out var entry))
            {
                entry.RefCount--;
                if (entry.RefCount < 0) entry.RefCount = 0;
                EvictIfNeeded();
            }
        }
    }

    public Resource? Get(Guid id)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(id, out var entry))
            {
                if (entry.Node != null)
                {
                    _lruList.Remove(entry.Node);
                    _lruList.AddLast(entry.Node);
                }
                return entry.Res;
            }
            return null;
        }
    }

    public void Put(Guid id, Resource res, long size, int initialRefCount = 0)
    {
        lock (_cache)
        {
            if (_cache.ContainsKey(id)) return;

            // initialRefCount lets a caller that immediately holds the resource (e.g. a mesh
            // assigned to a node) pin it before EvictIfNeeded runs, so a tight budget can't
            // evict the entry on the same call that added it.
            var entry = new CacheEntry { Id = id, Res = res, Size = size, RefCount = initialRefCount };
            entry.Node = _lruList.AddLast(entry);
            _cache[id] = entry;
            _currentSize += size;

            EvictIfNeeded();
        }
    }

    private void EvictIfNeeded()
    {
        if (_currentSize <= _maxSize) return;

        var node = _lruList.First;
        while (node != null && _currentSize > _maxSize)
        {
            var next = node.Next;
            var entry = node.Value;

            if (entry.RefCount <= 0)
            {
                _lruList.Remove(node);
                _cache.Remove(entry.Id);
                _currentSize -= entry.Size;
                // Dropping C# reference allows Godot to free the resource when no nodes use it
            }
            node = next;
        }
    }
}
