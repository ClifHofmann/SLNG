using System.Collections.Concurrent;
using LibreMetaverse;
using LibreMetaverse.Packets;

namespace SLNG.Net;

// GridSession, region-stream probe. BUG-NET-21.
//
// Returning to a region after a teleport left it at a fraction of its content, for good. The log
// could not say whether the simulator sent less, or whether what it sent was dropped on our side,
// because nothing counted what arrived. This counts packets per region at the packet layer -- before
// LibreMetaverse or anything of ours has a say -- and remembers what camera the interest list was
// last told about. Read together with the world's own entity count, that separates the two.
public sealed partial class GridSession
{
    private enum StreamPacket { ObjectUpdate, Compressed, Cached, Terse, Kill, KilledIds, Terrain, Handshake }

    private readonly ConcurrentDictionary<ulong, long[]> _streamCounts = new();

    private volatile float _lastCameraFar;
    private System.Numerics.Vector3 _lastCameraPosition;
    private long _agentUpdatesSent;
    private long _agentUpdatesSkipped;

    private void RegisterRegionStreamProbe()
    {
        _client.Network.RegisterCallback(PacketType.ObjectUpdate, OnStreamObjectUpdate);
        _client.Network.RegisterCallback(PacketType.ObjectUpdateCompressed, OnStreamObjectUpdate);
        _client.Network.RegisterCallback(PacketType.ObjectUpdateCached, OnStreamObjectUpdate);
        _client.Network.RegisterCallback(PacketType.ImprovedTerseObjectUpdate, OnStreamObjectUpdate);
        _client.Network.RegisterCallback(PacketType.KillObject, OnStreamObjectUpdate);
        _client.Network.RegisterCallback(PacketType.LayerData, OnStreamObjectUpdate);
        _client.Network.RegisterCallback(PacketType.RegionHandshake, OnStreamObjectUpdate);
    }

    private void UnregisterRegionStreamProbe()
    {
        _client.Network.UnregisterCallback(PacketType.ObjectUpdate, OnStreamObjectUpdate);
        _client.Network.UnregisterCallback(PacketType.ObjectUpdateCompressed, OnStreamObjectUpdate);
        _client.Network.UnregisterCallback(PacketType.ObjectUpdateCached, OnStreamObjectUpdate);
        _client.Network.UnregisterCallback(PacketType.ImprovedTerseObjectUpdate, OnStreamObjectUpdate);
        _client.Network.UnregisterCallback(PacketType.KillObject, OnStreamObjectUpdate);
        _client.Network.UnregisterCallback(PacketType.LayerData, OnStreamObjectUpdate);
        _client.Network.UnregisterCallback(PacketType.RegionHandshake, OnStreamObjectUpdate);
    }

    private void OnStreamObjectUpdate(object? sender, PacketReceivedEventArgs e)
    {
        var counts = _streamCounts.GetOrAdd(e.Simulator.Handle, _ => new long[8]);
        switch (e.Packet)
        {
            case ObjectUpdatePacket p:
                Interlocked.Add(ref counts[(int)StreamPacket.ObjectUpdate], p.ObjectData.Length);
                break;
            case ObjectUpdateCompressedPacket p:
                Interlocked.Add(ref counts[(int)StreamPacket.Compressed], p.ObjectData.Length);
                break;
            case ObjectUpdateCachedPacket p:
                Interlocked.Add(ref counts[(int)StreamPacket.Cached], p.ObjectData.Length);
                break;
            case ImprovedTerseObjectUpdatePacket p:
                Interlocked.Add(ref counts[(int)StreamPacket.Terse], p.ObjectData.Length);
                break;
            case LayerDataPacket:
                Interlocked.Increment(ref counts[(int)StreamPacket.Terrain]);
                break;
            case RegionHandshakePacket:
                Interlocked.Increment(ref counts[(int)StreamPacket.Handshake]);
                break;
            case KillObjectPacket p:
                Interlocked.Increment(ref counts[(int)StreamPacket.Kill]);
                Interlocked.Add(ref counts[(int)StreamPacket.KilledIds], p.ObjectData.Length);
                break;
        }
    }

    /// <summary>What the simulator has streamed us for this region since the session began, in
    /// objects per packet type, and the interest camera we last told it about.</summary>
    public string DescribeRegionStream(ulong regionHandle)
    {
        _streamCounts.TryGetValue(regionHandle, out var c);
        c ??= new long[8];
        long Get(StreamPacket k) => Interlocked.Read(ref c[(int)k]);
        var cam = _lastCameraPosition;
        return $"streamed full={Get(StreamPacket.ObjectUpdate)} compressed={Get(StreamPacket.Compressed)} " +
               $"cached={Get(StreamPacket.Cached)} terse={Get(StreamPacket.Terse)} " +
               $"kill={Get(StreamPacket.Kill)}({Get(StreamPacket.KilledIds)} ids) " +
               $"terrainPackets={Get(StreamPacket.Terrain)} handshakes={Get(StreamPacket.Handshake)} | " +
               $"{DescribeObjectCache()} | " +
               $"AgentUpdates sent={Interlocked.Read(ref _agentUpdatesSent)} " +
               $"skipped={Interlocked.Read(ref _agentUpdatesSkipped)}, " +
               $"last camera <{cam.X:0},{cam.Y:0},{cam.Z:0}> far={_lastCameraFar:0}";
    }

    /// <summary>The app skipped an AgentUpdate because the local agent was still keyed to the
    /// region it had left (BUG-NET-13). Counted so a skip that never ends is visible.</summary>
    public void NoteAgentUpdateSkipped() => Interlocked.Increment(ref _agentUpdatesSkipped);
}
