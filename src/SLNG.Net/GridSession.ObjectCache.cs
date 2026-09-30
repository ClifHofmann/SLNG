using System.Reflection;
using LibreMetaverse;
using LibreMetaverse.Packets;
using SLNG.Net.ObjectCache;

namespace SLNG.Net;

// GridSession, object cache. FEAT-NET-04; the reasoning and the viewer/OpenSim/LibreMetaverse facts
// are in docs/specs/FEAT-NET-04-object-cache.md.
//
// The reference viewer does not ask a simulator for what it already holds: the simulator probes
// (ObjectUpdateCached: id + CRC), the viewer recognises objects it has, and asks for the rest.
// SLNG told the simulator its cache was empty (LibreMetaverse's hard-coded 0x7) and asked for
// everything, every time. This wires up the other half: remember every compressed object update,
// say so in the handshake, answer probes from the store and request only what is missing.
public sealed partial class GridSession
{
    private readonly ObjectCacheStore _objectCache = new();

    private ObjectCacheDisk? _objectCacheDisk;
    private Timer? _objectCacheSaveTimer;
    private readonly HashSet<RegionKey> _cacheLoaded = new();
    private readonly Dictionary<ulong, RegionKey> _cacheKeyByHandle = new();

    /// <summary>How often regions that changed are written out while we stay in them. They are also
    /// written when we leave one and when the session ends; this is for the crash in between.</summary>
    private static readonly TimeSpan ObjectCacheSaveInterval = TimeSpan.FromMinutes(5);

    private EventHandler<PacketReceivedEventArgs>? _replayCompressed;
    private bool _objectCacheWired;

    private long _cacheStored;
    private long _cacheHits;
    private long _cacheCrcMisses;
    private long _cacheTotalMisses;
    private long _cacheRequested;
    private readonly HashSet<ulong> _cacheProbeLogged = new();

    /// <summary>Whether the cache is used at all. Off leaves LibreMetaverse exactly as it is: the
    /// handshake says "empty" and every cached object is asked for. Read at login; changing it
    /// afterwards takes effect with the next session.</summary>
    public bool ObjectCacheEnabled { get; set; } = true;

    /// <summary>Where the cache files live -- a directory the client owns (the app resolves
    /// <c>user://</c>; <c>src/</c> must not know Godot's virtual filesystem). Without it the cache
    /// works for this session only. Call before <see cref="LoginAsync"/>.</summary>
    public void UseObjectCacheDirectory(string directory)
        => _objectCacheDisk = new ObjectCacheDisk(directory);

    /// <summary>Forgets every object the cache holds, in memory and on disk. What the next arrival in
    /// a region costs is then what it cost before there was a cache.</summary>
    public void ClearObjectCache()
    {
        _objectCache.Clear();
        lock (_cacheLoaded) _cacheLoaded.Clear();
        _objectCacheDisk?.Clear();
    }

    private void RegisterObjectCache()
    {
        if (!ObjectCacheEnabled || _objectCacheWired) return;

        _replayCompressed = FindLibreMetaverseCompressedHandler();
        if (_replayCompressed is null)
        {
            Console.Error.WriteLine("[ObjectCache] disabled: LibreMetaverse's compressed-update handler was not found");
            return;
        }

        // The probe is ours now. LibreMetaverse's own answer is to request every cached object.
        _client.Settings.World.AlwaysRequestObjects = false;
        _client.Network.RegisterCallback(PacketType.RegionHandshake, OnCacheRegionHandshake);
        _client.Network.RegisterCallback(PacketType.ObjectUpdateCached, OnCacheProbe);
        _client.Network.RegisterCallback(PacketType.ObjectUpdateCompressed, OnCacheStore);
        if (_objectCacheDisk is not null)
            _objectCacheSaveTimer = new Timer(_ => SaveDirtyObjectCaches(), null, ObjectCacheSaveInterval, ObjectCacheSaveInterval);
        _objectCacheWired = true;
    }

    private void UnregisterObjectCache()
    {
        if (!_objectCacheWired) return;
        _objectCacheSaveTimer?.Dispose();
        _objectCacheSaveTimer = null;
        SaveDirtyObjectCaches();
        _client.Network.UnregisterCallback(PacketType.RegionHandshake, OnCacheRegionHandshake);
        _client.Network.UnregisterCallback(PacketType.ObjectUpdateCached, OnCacheProbe);
        _client.Network.UnregisterCallback(PacketType.ObjectUpdateCompressed, OnCacheStore);
        _client.Settings.World.AlwaysRequestObjects = true;
        _objectCacheWired = false;
    }

    /// <summary>LibreMetaverse decodes a compressed block into a <c>Primitive</c> and raises the
    /// events our pipeline already converts. A cached block has to go the same way, and there is no
    /// public way in: the handler is protected. Fetched once, as a delegate.</summary>
    private EventHandler<PacketReceivedEventArgs>? FindLibreMetaverseCompressedHandler()
    {
        var method = typeof(ObjectManager).GetMethod(
            "ObjectUpdateCompressedHandler", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (method is null) return null;
        try
        {
            return (EventHandler<PacketReceivedEventArgs>)Delegate.CreateDelegate(
                typeof(EventHandler<PacketReceivedEventArgs>), _client.Objects, method);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static RegionKey CacheKey(Simulator sim) => new(sim.Handle, sim.ID.Guid);

    /// <summary>LibreMetaverse has already replied, saying the cache is empty. When it is not, say
    /// so: the simulator reads the flags when it decides between probes and full updates, which
    /// happens a moment after the handshake.</summary>
    private void OnCacheRegionHandshake(object? sender, PacketReceivedEventArgs e) => Guarded(() => CacheRegionHandshake(e));

    private void CacheRegionHandshake(PacketReceivedEventArgs e)
    {
        if (e.Packet is not RegionHandshakePacket handshake) return;

        var key = new RegionKey(e.Simulator.Handle, handshake.RegionInfo.CacheID.Guid);
        RememberKey(key);
        LoadFromDisk(key);
        bool empty = _objectCache.IsEmpty(key);
        Console.WriteLine($"[ObjectCache] {Utils.BytesToString(handshake.RegionInfo.SimName)} ({key.Handle}): " +
                          $"{_objectCache.Count(key)} objects held, telling the simulator the cache is " +
                          $"{(empty ? "empty" : "not empty")}");
        if (empty) return; // what LibreMetaverse sent already says so

        _client.Network.SendPacket(new RegionHandshakeReplyPacket
        {
            AgentData = { AgentID = _client.Self.AgentID, SessionID = _client.Self.SessionID },
            RegionInfo = { Flags = ObjectCacheProtocol.HandshakeFlags(cacheIsEmpty: false) },
        }, e.Simulator);
    }

    /// <summary>A cache is an optimisation: a packet it cannot make sense of is a packet it did not
    /// help with, never an exception on the network thread. Said once, so a bad packet stream does
    /// not flood the log.</summary>
    private void Guarded(Action work)
    {
        try
        {
            work();
        }
        catch (Exception ex)
        {
            if (Interlocked.Increment(ref _cacheFaults) == 1)
                Console.Error.WriteLine($"[ObjectCache] a packet could not be handled: {ex}");
        }
    }

    private long _cacheFaults;

    private void RememberKey(RegionKey key)
    {
        lock (_cacheKeyByHandle) _cacheKeyByHandle[key.Handle] = key;
    }

    /// <summary>The reference viewer reads the region's file before it answers the handshake; so
    /// does this. Once per region per session: after that memory is the truth.</summary>
    private void LoadFromDisk(RegionKey key)
    {
        if (_objectCacheDisk is null) return;
        lock (_cacheLoaded)
        {
            if (!_cacheLoaded.Add(key)) return;
        }
        if (!_objectCacheDisk.TryLoad(key, out var objects) || objects.Count == 0) return;

        _objectCache.Load(key, objects);
        Console.WriteLine($"[ObjectCache] read {objects.Count} objects for region {key.Handle} from disk");
    }

    /// <summary>Writes the region's objects out, now, on a worker -- a region we have just left.</summary>
    private void SaveRegionInBackground(ulong regionHandle)
    {
        if (_objectCacheDisk is null) return;
        RegionKey key;
        lock (_cacheKeyByHandle)
        {
            if (!_cacheKeyByHandle.TryGetValue(regionHandle, out key)) return;
        }
        _ = Task.Run(() => SaveRegion(key));
    }

    private void SaveRegion(RegionKey key)
    {
        var disk = _objectCacheDisk;
        if (disk is null) return;

        var objects = _objectCache.Snapshot(key);
        if (objects.Count == 0) return;
        if (disk.TrySave(key, objects))
            Console.WriteLine($"[ObjectCache] saved {objects.Count} objects for region {key.Handle}");
        else
            _objectCache.MarkDirty(key); // try again at the next opportunity
    }

    private void SaveDirtyObjectCaches()
    {
        if (_objectCacheDisk is null) return;
        try
        {
            foreach (var key in _objectCache.TakeDirty()) SaveRegion(key);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ObjectCache] saving failed: {ex.Message}");
        }
    }

    /// <summary>Every compressed update is a full description of a static object. Keep it.</summary>
    private void OnCacheStore(object? sender, PacketReceivedEventArgs e) => Guarded(() => CacheStore(e));

    private void CacheStore(PacketReceivedEventArgs e)
    {
        if (e.Packet is not ObjectUpdateCompressedPacket packet) return;

        var key = CacheKey(e.Simulator);
        RememberKey(key);
        foreach (var block in packet.ObjectData)
        {
            if (!CompressedObjectBlock.TryRead(block.Data, out var head)) continue;
            if (!IsCacheablePCode(head.PCode)) continue;

            _objectCache.Put(key, new CachedObject(head.LocalId, head.Crc, block.UpdateFlags, block.Data));
            Interlocked.Increment(ref _cacheStored);
        }
    }

    /// <summary>Prims, grass and trees are what a region is made of. An avatar is never cached.</summary>
    private static bool IsCacheablePCode(byte pcode)
        => pcode == (byte)PCode.Prim || pcode == (byte)PCode.Grass
           || pcode == (byte)PCode.Tree || pcode == (byte)PCode.NewTree;

    /// <summary>The simulator says which objects it would send and what state they are in. Build the
    /// ones we hold in exactly that state, ask for the rest.</summary>
    private void OnCacheProbe(object? sender, PacketReceivedEventArgs e) => Guarded(() => CacheProbeReceived(e));

    private void CacheProbeReceived(PacketReceivedEventArgs e)
    {
        if (e.Packet is not ObjectUpdateCachedPacket probe || _replayCompressed is null) return;

        var sim = e.Simulator;
        var key = CacheKey(sim);
        var hits = new List<ObjectUpdateCompressedPacket.ObjectDataBlock>();
        var misses = new List<CacheMiss>();

        foreach (var block in probe.ObjectData)
        {
            switch (_objectCache.Probe(key, block.ID, block.CRC, out var held))
            {
                case CacheProbe.Hit:
                    hits.Add(new ObjectUpdateCompressedPacket.ObjectDataBlock { UpdateFlags = block.UpdateFlags, Data = held.Block });
                    break;
                case CacheProbe.CrcMiss:
                    misses.Add(new CacheMiss(block.ID, CacheMissType.Crc));
                    break;
                default:
                    misses.Add(new CacheMiss(block.ID, CacheMissType.Total));
                    break;
            }
        }

        Interlocked.Add(ref _cacheHits, hits.Count);
        Interlocked.Add(ref _cacheCrcMisses, misses.Count(m => m.Type == CacheMissType.Crc));
        Interlocked.Add(ref _cacheTotalMisses, misses.Count(m => m.Type == CacheMissType.Total));
        LogFirstProbe(sim, probe.ObjectData.Length, hits.Count);

        if (hits.Count > 0) Replay(sim, hits);
        if (misses.Count > 0) RequestMisses(sim, misses);
    }

    private void LogFirstProbe(Simulator sim, int probed, int hits)
    {
        lock (_cacheProbeLogged)
        {
            if (!_cacheProbeLogged.Add(sim.Handle)) return;
        }
        Console.WriteLine($"[ObjectCache] first probe from {sim.Name} ({sim.Handle}): {probed} objects, {hits} held");
    }

    /// <summary>Hands cached blocks to LibreMetaverse as if they had just arrived. Our own handler
    /// for compressed updates follows, because it puts back the particle system LibreMetaverse drops
    /// from every one of them.</summary>
    private void Replay(Simulator sim, List<ObjectUpdateCompressedPacket.ObjectDataBlock> blocks)
    {
        const int PerPacket = 100;
        for (int i = 0; i < blocks.Count; i += PerPacket)
        {
            var packet = new ObjectUpdateCompressedPacket
            {
                RegionData = { RegionHandle = sim.Handle, TimeDilation = ushort.MaxValue },
                ObjectData = blocks.GetRange(i, Math.Min(PerPacket, blocks.Count - i)).ToArray(),
            };
            var args = new PacketReceivedEventArgs(packet, sim);
            try
            {
                _replayCompressed!(this, args);
                OnObjectUpdateCompressedRaw(this, args);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ObjectCache] replaying {packet.ObjectData.Length} cached objects failed: {ex.Message}");
            }
        }
    }

    private void RequestMisses(Simulator sim, List<CacheMiss> misses)
    {
        foreach (var message in ObjectCacheProtocol.Messages(misses))
        {
            var request = new RequestMultipleObjectsPacket
            {
                AgentData = { AgentID = _client.Self.AgentID, SessionID = _client.Self.SessionID },
                ObjectData = new RequestMultipleObjectsPacket.ObjectDataBlock[message.Count],
            };
            for (int i = 0; i < message.Count; i++)
                request.ObjectData[i] = new RequestMultipleObjectsPacket.ObjectDataBlock
                {
                    ID = message[i].LocalId,
                    CacheMissType = (byte)message[i].Type,
                };
            _client.Network.SendPacket(request, sim);
            Interlocked.Add(ref _cacheRequested, message.Count);
        }
    }

    /// <summary>One line of the cache's bookkeeping, appended to <see cref="DescribeRegionStream"/>.</summary>
    private string DescribeObjectCache()
        => ObjectCacheEnabled
            ? $"cache stored={Interlocked.Read(ref _cacheStored)} hit={Interlocked.Read(ref _cacheHits)} " +
              $"crcMiss={Interlocked.Read(ref _cacheCrcMisses)} totalMiss={Interlocked.Read(ref _cacheTotalMisses)} " +
              $"requested={Interlocked.Read(ref _cacheRequested)} ({_objectCache.TotalBytes / 1024} KB)"
            : "cache off";
}
