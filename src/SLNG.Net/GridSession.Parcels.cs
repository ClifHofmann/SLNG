using LibreMetaverse;
using LibreMetaverse.Packets;
using SLNG.Core;
using ParcelInfo = SLNG.Core.ParcelInfo; // LibreMetaverse has a ParcelInfo too

namespace SLNG.Net;

// GridSession, Parcels part (FEAT-LAND-01): the data behind the Land-Info "General" tab.
//
// Two messages make one record:
//   * ParcelProperties (EventQueue, or UDP on a grid without caps) -- everything except traffic;
//   * ParcelDwellReply (UDP) -- "traffic" and the parcel's global UUID.
// The first is asked for with RequestParcelInfoAt/Here, or pushed by the sim when the agent crosses
// into another parcel. The second is asked for here, once per newly shown parcel, exactly like the
// viewer does when it selects land (llviewerparcelmgr.cpp:1880-1885).
//
// The existing OnParcelPropertiesReceived (GridSession.Environment.cs, current parcel NAME) and
// ResolveAgentParcelIdAsync are left as they are: they listen to the same LibreMetaverse event and
// match on their own positive sequence ids; this part matches on a different, negative one (see
// ParcelInfoMapper.Classify).
//
// THREADING (AGENTS.md): every handler runs on a LibreMetaverse network thread. They build an
// immutable record and raise an event; no World state is touched. The app marshals to the main thread.
public sealed partial class GridSession
{
    /// <summary>How long a requested parcel lookup may stay unanswered before
    /// <see cref="ParcelInfoFailed"/> fires with <see cref="ParcelInfoFailureReason.TimedOut"/>.</summary>
    internal static readonly TimeSpan ParcelInfoTimeout = TimeSpan.FromSeconds(5);

    private readonly ParcelInfoTracker _parcelInfoTracker = new();

    // Serial of the lookup that is waiting for its answer; 0 when none is. Compare-exchanged by the
    // reply and by the timeout, so exactly one of them wins.
    private int _parcelInfoSerial;
    private int _parcelInfoPending;

    /// <summary>Raised on a NETWORK thread when parcel data is available: the answer to
    /// <see cref="RequestParcelInfoAt"/> / <see cref="RequestParcelInfoHere"/>, a push because the
    /// agent walked into another parcel (or the parcel was edited), and again when the traffic
    /// figure arrives for it (same parcel, <see cref="ParcelInfo.Dwell"/> and
    /// <see cref="ParcelInfo.ParcelId"/> now filled). A repeat of what was already shown is not raised
    /// unless it answers a request. Marshal to the main thread before touching UI.</summary>
    public event EventHandler<ParcelInfo>? ParcelInfoReceived;

    /// <summary>Raised on a NETWORK thread when a requested lookup got no parcel: refused by the sim
    /// or not answered in time. Distinct from a public or empty parcel, which arrives through
    /// <see cref="ParcelInfoReceived"/>.</summary>
    public event EventHandler<ParcelInfoFailure>? ParcelInfoFailed;

    /// <summary>The last parcel record raised, or null. Reads state only; never sends.</summary>
    public ParcelInfo? LastParcelInfo => _parcelInfoTracker.Current;

    /// <summary>Asks the current region for the parcel at a region-local position (metres). The
    /// answer arrives through <see cref="ParcelInfoReceived"/>, or <see cref="ParcelInfoFailed"/>.
    /// Returns false, and sends nothing, when not connected.</summary>
    public bool RequestParcelInfoAt(float localX, float localY)
    {
        var sim = _client.Network.CurrentSim;
        if (sim == null || !_client.Network.Connected) return false;

        var box = ParcelInfoMapper.RequestBox(localX, localY, sim.SizeX, sim.SizeY);
        int serial = Interlocked.Increment(ref _parcelInfoSerial);
        Volatile.Write(ref _parcelInfoPending, serial);
        _client.Parcels.RequestParcelProperties(
            sim, box.North, box.East, box.South, box.West, ParcelInfoMapper.RequestSequenceId, true);

        ulong handle = sim.Handle;
        _ = Task.Delay(ParcelInfoTimeout).ContinueWith(_ =>
        {
            if (Interlocked.CompareExchange(ref _parcelInfoPending, 0, serial) == serial)
                RaiseParcelInfoFailed(new ParcelInfoFailure(handle, ParcelInfoFailureReason.TimedOut));
        }, TaskScheduler.Default);
        return true;
    }

    /// <summary>Asks for the parcel under the agent; see <see cref="RequestParcelInfoAt"/>.</summary>
    public bool RequestParcelInfoHere()
    {
        var pos = _client.Self.SimPosition;
        return RequestParcelInfoAt(pos.X, pos.Y);
    }

    private void RegisterParcelInfo()
    {
        _client.Parcels.ParcelProperties += OnParcelInfoProperties;
        // The raw packet, not LibreMetaverse's ParcelDwellReply event: that event only fires while
        // Settings.Parcel.AlwaysRequestDwell is on (its default), and this must not hang on a setting.
        _client.Network.RegisterCallback(PacketType.ParcelDwellReply, OnParcelDwellPacket);
    }

    private void UnregisterParcelInfo()
    {
        _client.Parcels.ParcelProperties -= OnParcelInfoProperties;
        _client.Network.UnregisterCallback(PacketType.ParcelDwellReply, OnParcelDwellPacket);
        _parcelInfoTracker.Reset();
        Volatile.Write(ref _parcelInfoPending, 0);
    }

    private void OnParcelInfoProperties(object? sender, ParcelPropertiesEventArgs e)
    {
        try
        {
            var kind = ParcelInfoMapper.Classify(e.SequenceID);
            if (kind == ParcelReplyKind.Ignore) return;

            // Only the agent's own region: a neighbour's parcels are not what the window follows,
            // and local ids repeat from region to region.
            var sim = e.Simulator;
            if (sim == null || sim != _client.Network.CurrentSim) return;

            bool requested = kind == ParcelReplyKind.Requested;
            if (requested) Interlocked.Exchange(ref _parcelInfoPending, 0);

            if (e.Result != ParcelResult.Single)
            {
                // PARCEL_RESULT_NO_DATA, or a multi-parcel answer to a one-cell question.
                if (requested) RaiseParcelInfoFailed(new ParcelInfoFailure(sim.Handle, ParcelInfoFailureReason.NoData));
                return;
            }

            var info = ParcelInfoMapper.From(e.Parcel, sim.Handle, sim.Access, sim.ProductName, sim.Flags);
            var raised = _parcelInfoTracker.OnProperties(info, requested);
            if (raised == null) return;

            RaiseParcelInfoReceived(raised);

            // Local id 0 is public land in a selection; the viewer never asks for its dwell.
            if (raised.LocalId != 0) _client.Parcels.RequestDwell(sim, raised.LocalId);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ParcelInfo] a parcel reply could not be handled: {ex.Message}");
        }
    }

    private void OnParcelDwellPacket(object? sender, PacketReceivedEventArgs e)
    {
        try
        {
            if (e.Packet is not ParcelDwellReplyPacket reply) return;
            var merged = _parcelInfoTracker.OnDwell(
                e.Simulator.Handle, reply.Data.LocalID, reply.Data.ParcelID.Guid, reply.Data.Dwell);
            if (merged != null) RaiseParcelInfoReceived(merged);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ParcelInfo] a dwell reply could not be handled: {ex.Message}");
        }
    }

    // A throwing subscriber must not unwind into LibreMetaverse's packet loop.
    private void RaiseParcelInfoReceived(ParcelInfo info)
    {
        try { ParcelInfoReceived?.Invoke(this, info); }
        catch (Exception ex) { Console.Error.WriteLine($"[ParcelInfo] subscriber threw: {ex.Message}"); }
    }

    private void RaiseParcelInfoFailed(ParcelInfoFailure failure)
    {
        try { ParcelInfoFailed?.Invoke(this, failure); }
        catch (Exception ex) { Console.Error.WriteLine($"[ParcelInfo] subscriber threw: {ex.Message}"); }
    }
}
