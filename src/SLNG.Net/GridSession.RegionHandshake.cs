using System.Reflection;
using LibreMetaverse;
using LibreMetaverse.Packets;
using SLNG.Net.ObjectCache;

namespace SLNG.Net;

// GridSession, the region handshake as the reference viewer answers it. FEAT-NET-04.
//
// The reply to RegionHandshake carries the object-cache flags: "my cache is empty, don't probe"
// or not. The reference viewer loads its cache first and sends ONE reply with the truth
// (llviewerregion.cpp:3201-3222). LibreMetaverse's handler sends 0x7 -- empty -- and there is no
// setting for it. A second reply behind it only helps where the simulator reads the latest flags;
// one that acts on the first reply would never be told. Second Life's Agni simulators, measured
// with the second reply, sent no probe at all -- which is what a first-reply simulator looks like.
//
// So the handler is replaced: the same assignments LibreMetaverse 3.1.6 makes
// (NetworkManager.cs:1399-1458), the reply with the flags the cache calls for, and the same three
// internals set at the end. Those are internal to LibreMetaverse, hence reflection -- checked
// before anything is touched, so a LibreMetaverse that no longer looks like this one leaves the
// library's own handler in place and the second-reply fallback does the best it can.
public sealed partial class GridSession
{
    private EventHandler<PacketReceivedEventArgs>? _libreMetaverseHandshake;
    private FieldInfo? _simulatorConnected;
    private FieldInfo? _simulatorHandshakeComplete;
    private FieldInfo? _simulatorConnectedEvent;
    private bool _handshakeTakenOver;

    /// <summary>Whether the handshake is answered by us rather than by LibreMetaverse. False means
    /// the fallback is in use; logged at login so a session can say which it was.</summary>
    internal bool HandshakeTakenOver => _handshakeTakenOver;

    internal bool TryTakeOverRegionHandshake()
    {
        var method = typeof(NetworkManager).GetMethod(
            "RegionHandshakeHandler", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        _simulatorConnected = typeof(Simulator).GetField("connected", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        _simulatorHandshakeComplete = typeof(Simulator).GetField("handshakeComplete", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        _simulatorConnectedEvent = typeof(Simulator).GetField("ConnectedEvent", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        if (method is null
            || _simulatorConnected?.FieldType != typeof(bool)
            || _simulatorHandshakeComplete?.FieldType != typeof(bool)
            || _simulatorConnectedEvent?.FieldType != typeof(ManualResetEventSlim))
        {
            Console.WriteLine("[ObjectCache] the region handshake is not as LibreMetaverse 3.1.6 has it; " +
                              "leaving its handler and answering a second time instead");
            return false;
        }

        try
        {
            _libreMetaverseHandshake = (EventHandler<PacketReceivedEventArgs>)Delegate.CreateDelegate(
                typeof(EventHandler<PacketReceivedEventArgs>), _client.Network, method);
            _client.Network.UnregisterCallback(PacketType.RegionHandshake, _libreMetaverseHandshake);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Puts LibreMetaverse's own handler back, for the next session that does not use the cache.</summary>
    private void GiveBackRegionHandshake()
    {
        if (!_handshakeTakenOver || _libreMetaverseHandshake is null) return;
        _client.Network.RegisterCallback(PacketType.RegionHandshake, _libreMetaverseHandshake);
        _handshakeTakenOver = false;
    }

    /// <summary>What <c>NetworkManager.RegionHandshakeHandler</c> does, with the cache's flags in
    /// the reply. However it goes, the handshake is completed: a session that never finishes
    /// connecting to its region is worse than one whose cache flags were wrong.</summary>
    private void HandleRegionHandshake(PacketReceivedEventArgs e)
    {
        var handshake = (RegionHandshakePacket)e.Packet;
        var simulator = e.Simulator;
        try
        {
            simulator.ID = handshake.RegionInfo.CacheID;

            simulator.IsEstateManager = handshake.RegionInfo.IsEstateManager;
            simulator.Name = Utils.BytesToString(handshake.RegionInfo.SimName);
            simulator.SimOwner = handshake.RegionInfo.SimOwner;
            simulator.TerrainBase0 = handshake.RegionInfo.TerrainBase0;
            simulator.TerrainBase1 = handshake.RegionInfo.TerrainBase1;
            simulator.TerrainBase2 = handshake.RegionInfo.TerrainBase2;
            simulator.TerrainBase3 = handshake.RegionInfo.TerrainBase3;
            simulator.TerrainDetail0 = handshake.RegionInfo.TerrainDetail0;
            simulator.TerrainDetail1 = handshake.RegionInfo.TerrainDetail1;
            simulator.TerrainDetail2 = handshake.RegionInfo.TerrainDetail2;
            simulator.TerrainDetail3 = handshake.RegionInfo.TerrainDetail3;
            simulator.TerrainHeightRange00 = handshake.RegionInfo.TerrainHeightRange00;
            simulator.TerrainHeightRange01 = handshake.RegionInfo.TerrainHeightRange01;
            simulator.TerrainHeightRange10 = handshake.RegionInfo.TerrainHeightRange10;
            simulator.TerrainHeightRange11 = handshake.RegionInfo.TerrainHeightRange11;
            simulator.TerrainStartHeight00 = handshake.RegionInfo.TerrainStartHeight00;
            simulator.TerrainStartHeight01 = handshake.RegionInfo.TerrainStartHeight01;
            simulator.TerrainStartHeight10 = handshake.RegionInfo.TerrainStartHeight10;
            simulator.TerrainStartHeight11 = handshake.RegionInfo.TerrainStartHeight11;
            simulator.WaterHeight = handshake.RegionInfo.WaterHeight;
            simulator.Flags = (RegionFlags)handshake.RegionInfo.RegionFlags;
            simulator.BillableFactor = handshake.RegionInfo.BillableFactor;
            simulator.Access = (SimAccess)handshake.RegionInfo.SimAccess;

            simulator.RegionID = handshake.RegionInfo2.RegionID;
            simulator.ColoLocation = Utils.BytesToString(handshake.RegionInfo3.ColoName);
            simulator.CPUClass = handshake.RegionInfo3.CPUClassID;
            simulator.CPURatio = handshake.RegionInfo3.CPURatio;
            simulator.ProductName = Utils.BytesToString(handshake.RegionInfo3.ProductName);
            simulator.ProductSku = Utils.BytesToString(handshake.RegionInfo3.ProductSKU);

            if (handshake.RegionInfo4 != null && handshake.RegionInfo4.Length > 0)
            {
                simulator.Protocols = (RegionProtocols)handshake.RegionInfo4[0].RegionProtocols;
                // Yes, overwrite region flags if we have extended version of them
                simulator.Flags = (RegionFlags)handshake.RegionInfo4[0].RegionFlagsExtended;
            }

            var key = new RegionKey(simulator.Handle, handshake.RegionInfo.CacheID.Guid);
            ForgetProbesForArrival(key.Handle);
            RememberKey(key);
            LoadFromDisk(key);
            bool empty = _objectCache.IsEmpty(key);
            Console.WriteLine($"[ObjectCache] {simulator.Name} ({key.Handle}): {_objectCache.Count(key)} objects held, " +
                              $"replying that the cache is {(empty ? "empty" : "not empty")}");

            var reply = new RegionHandshakeReplyPacket
            {
                AgentData = { AgentID = _client.Self.AgentID, SessionID = _client.Self.SessionID },
                RegionInfo = { Flags = ObjectCacheProtocol.HandshakeFlags(empty) },
            };
            reply.Header.Reliable = true; // "a crucial message for establishing a connection" -- the viewer says so too
            _client.Network.SendPacket(reply, simulator);

            if (!empty) ScheduleRestoreFromCache(simulator, key);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ObjectCache] answering the handshake failed, completing it without the cache: {ex.Message}");
            _client.Network.SendPacket(new RegionHandshakeReplyPacket
            {
                AgentData = { AgentID = _client.Self.AgentID, SessionID = _client.Self.SessionID },
                RegionInfo = { Flags = ObjectCacheProtocol.HandshakeFlags(cacheIsEmpty: true) },
            }, simulator);
        }
        finally
        {
            // We're officially connected to this sim
            _simulatorConnected!.SetValue(simulator, true);
            _simulatorHandshakeComplete!.SetValue(simulator, true);
            ((ManualResetEventSlim)_simulatorConnectedEvent!.GetValue(simulator)!).Set();
        }
    }
}
