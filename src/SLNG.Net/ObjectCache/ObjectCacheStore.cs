namespace SLNG.Net.ObjectCache;

/// <summary>A region as the simulator names it for caching: where it is, and the id it gives the
/// current state of its content. A simulator that resets a region hands out a new cache id, which
/// is how it tells every viewer that what they hold of it is void.</summary>
internal readonly record struct RegionKey(ulong Handle, Guid CacheId);

/// <summary>One object as the simulator last described it in full: the compressed block exactly as
/// it came off the wire, with the CRC it carried and the update flags of the packet.</summary>
internal readonly record struct CachedObject(uint LocalId, uint Crc, uint UpdateFlags, byte[] Block);

internal enum CacheProbe { Hit, CrcMiss, TotalMiss }

/// <summary>
/// What a viewer holds of the regions it has seen (FEAT-NET-04).
///
/// <para>Plain bytes and integers: nothing here knows LibreMetaverse, the world model, or Godot.
/// It is the same thing the reference viewer keeps per region (<c>LLVOCache</c>): a map from local
/// id to the object's last full state and the CRC it had. A probe from the simulator carries an id
/// and a CRC; the same CRC means the object has not changed since, so the stored state is as good
/// as a fresh one.</para>
///
/// <para>Bounded by bytes. When a write takes it over the budget the regions used least recently go,
/// whole, never the one being written.</para>
/// </summary>
internal sealed class ObjectCacheStore
{
    private sealed class Region
    {
        public readonly Dictionary<uint, CachedObject> Objects = new();
        public long Bytes;
        public long LastUsed;
        public bool Dirty; // changed since it was last written to disk or read from it
    }

    public const long DefaultMaxBytes = 256L * 1024 * 1024;

    private readonly object _lock = new();
    private readonly Dictionary<RegionKey, Region> _regions = new();
    private readonly long _maxBytes;
    private long _clock;
    private long _totalBytes;

    public ObjectCacheStore(long maxBytes = DefaultMaxBytes) => _maxBytes = maxBytes;

    public long TotalBytes
    {
        get { lock (_lock) return _totalBytes; }
    }

    public int Count(RegionKey key)
    {
        lock (_lock) return _regions.TryGetValue(key, out var r) ? r.Objects.Count : 0;
    }

    public bool IsEmpty(RegionKey key) => Count(key) == 0;

    /// <summary>Stores an object, replacing what was held under its local id. The block is copied:
    /// the caller's buffer belongs to the packet it came from.</summary>
    public void Put(RegionKey key, CachedObject obj)
    {
        var copy = new CachedObject(obj.LocalId, obj.Crc, obj.UpdateFlags, (byte[])obj.Block.Clone());
        lock (_lock)
        {
            if (!_regions.TryGetValue(key, out var region))
                _regions[key] = region = new Region();

            if (region.Objects.TryGetValue(copy.LocalId, out var old))
            {
                region.Bytes -= old.Block.Length;
                _totalBytes -= old.Block.Length;
            }
            region.Objects[copy.LocalId] = copy;
            region.Bytes += copy.Block.Length;
            _totalBytes += copy.Block.Length;
            region.LastUsed = ++_clock;
            region.Dirty = true;

            EvictOthersOverBudget(key);
        }
    }

    /// <summary>What is held under this local id, whatever its CRC: for the caller that has reason to
    /// believe the simulator would say the same (an object it has not withdrawn).</summary>
    public bool TryGet(RegionKey key, uint localId, out CachedObject held)
    {
        lock (_lock)
        {
            if (_regions.TryGetValue(key, out var region) && region.Objects.TryGetValue(localId, out held))
            {
                region.LastUsed = ++_clock;
                return true;
            }
        }
        held = default;
        return false;
    }

    /// <summary>Everything held for a region, for writing it out.</summary>
    public IReadOnlyCollection<CachedObject> Snapshot(RegionKey key)
    {
        lock (_lock)
            return _regions.TryGetValue(key, out var region) ? region.Objects.Values.ToArray() : Array.Empty<CachedObject>();
    }

    /// <summary>Puts what was read from disk into a region that holds nothing yet. Not dirty: it is
    /// what the file already says.</summary>
    public void Load(RegionKey key, IEnumerable<CachedObject> objects)
    {
        foreach (var obj in objects) Put(key, obj);
        lock (_lock)
        {
            if (_regions.TryGetValue(key, out var region)) region.Dirty = false;
        }
    }

    /// <summary>The regions changed since they were last written, each only once until it changes again.</summary>
    public IReadOnlyList<RegionKey> TakeDirty()
    {
        lock (_lock)
        {
            var dirty = new List<RegionKey>();
            foreach (var (key, region) in _regions)
            {
                if (!region.Dirty) continue;
                region.Dirty = false;
                dirty.Add(key);
            }
            return dirty;
        }
    }

    /// <summary>Marks a region changed again, for a write that did not happen.</summary>
    public void MarkDirty(RegionKey key)
    {
        lock (_lock)
        {
            if (_regions.TryGetValue(key, out var region)) region.Dirty = true;
        }
    }

    /// <summary>What to make of a probe: the object is held with this CRC (<see cref="CacheProbe.Hit"/>,
    /// <paramref name="held"/> is it), held with another (it changed), or not held at all.</summary>
    public CacheProbe Probe(RegionKey key, uint localId, uint crc, out CachedObject held)
    {
        lock (_lock)
        {
            if (_regions.TryGetValue(key, out var region) && region.Objects.TryGetValue(localId, out held))
            {
                region.LastUsed = ++_clock;
                return held.Crc == crc ? CacheProbe.Hit : CacheProbe.CrcMiss;
            }
        }
        held = default;
        return CacheProbe.TotalMiss;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _regions.Clear();
            _totalBytes = 0;
        }
    }

    private void EvictOthersOverBudget(RegionKey keep)
    {
        while (_totalBytes > _maxBytes)
        {
            RegionKey? oldest = null;
            long oldestUse = long.MaxValue;
            foreach (var (key, region) in _regions)
            {
                if (key.Equals(keep) || region.LastUsed >= oldestUse) continue;
                oldest = key;
                oldestUse = region.LastUsed;
            }
            if (oldest is not { } victim) return; // only the region being written is left

            _totalBytes -= _regions[victim].Bytes;
            _regions.Remove(victim);
        }
    }
}
