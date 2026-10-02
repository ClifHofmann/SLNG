using LibreMetaverse;
using LibreMetaverse.Packets;
using SLNG.Core;

namespace SLNG.Net;

// GridSession, Object owners part (FEAT-LAND-03): the list behind the "Object Owners" table of the Land-Info
// "Objects" tab. The counts above it are not here: they ride in ParcelProperties (see ParcelInfoMapper.PrimsFrom).
//
// One request, one or more replies, exactly as the reference viewer does it (onClickRefresh, llfloaterland.cpp:1562):
//   * ParcelObjectOwnersRequest (UDP, reliable): AgentData + ParcelData.LocalID. LibreMetaverse already has it
//     (ParcelManager.RequestObjectOwners) and it is reused.
//   * ParcelObjectOwnersReply: Data (Variable) rows of OwnerID / IsGroupOwned / Count / OnlineStatus. Linden Lab's
//     simulators send it through the EventQueue (message_template.msg marks the UDP form UDPDeprecated), OpenSim as a
//     plain UDP packet. LibreMetaverse raises an event for the EventQueue form only, and that event carries the
//     simulator, so it is used; the UDP form is read from the raw packet. Neither carries a parcel id, so the reply
//     is filed under the last request (ParcelObjectOwnersTracker).
// The viewer never asks by itself: only the Refresh button does. Neither does this class.
//
// THREADING (AGENTS.md): handlers run on LibreMetaverse network / thread-pool threads. They build an immutable
// record and raise an event; no World state is touched. The app marshals to the main thread.
public sealed partial class GridSession
{
    /// <summary>How long a requested owner list may stay unanswered before <see cref="ParcelObjectOwnersFailed"/>
    /// fires. The viewer has no timeout at all (its list stays on "Searching..." for ever when the sim stays
    /// silent); an unanswered request is how OpenSim says no.</summary>
    internal static readonly TimeSpan ParcelObjectOwnersTimeout = TimeSpan.FromSeconds(10);

    private readonly ParcelObjectOwnersTracker _objectOwnersTracker = new();

    /// <summary>Raised on a NETWORK thread for every reply to <see cref="RequestParcelObjectOwners"/>, with the
    /// CUMULATIVE list so far (a long list can come in several replies; the first one starts it, later ones add to
    /// it). A reply for no request that is known is dropped. Marshal to the main thread before touching UI.</summary>
    public event EventHandler<ParcelObjectOwners>? ParcelObjectOwnersReceived;

    /// <summary>Raised on a NETWORK thread when a requested list got no reply in time. Distinct from a parcel with
    /// no objects, which arrives through <see cref="ParcelObjectOwnersReceived"/> with no owners. A reply that
    /// comes later still raises <see cref="ParcelObjectOwnersReceived"/>.</summary>
    public event EventHandler<ParcelObjectOwnersFailure>? ParcelObjectOwnersFailed;

    /// <summary>The list built so far for the last request, or null when none was answered yet or the last request
    /// was for another region. Reads state only; never sends.</summary>
    public ParcelObjectOwners? LastParcelObjectOwners
    {
        get
        {
            var held = _objectOwnersTracker.Current;
            return held != null && held.RegionHandle == CurrentRegionHandle ? held : null;
        }
    }

    /// <summary>Asks the current region who owns objects on parcel <paramref name="localId"/>. The answer arrives
    /// through <see cref="ParcelObjectOwnersReceived"/>, or <see cref="ParcelObjectOwnersFailed"/>.
    ///
    /// <para>Returns false, and sends nothing, when not connected or <paramref name="localId"/> is not a parcel
    /// (0 or less). A second call for the same parcel while the first is still waiting for its reply sends nothing
    /// and returns true: the one answer serves both, so a double click on Refresh is one request.</para>
    ///
    /// <para>The sim answers only an agent who may manage the parcel; the client does not try to guess that (the
    /// viewer greys its Refresh button from group powers it holds, which are not exposed here). A refused agent
    /// simply gets no reply, which this reports as <see cref="ParcelObjectOwnersFailed"/>.</para></summary>
    public bool RequestParcelObjectOwners(int localId)
    {
        var sim = _client.Network.CurrentSim;
        if (sim == null || !_client.Network.Connected || localId <= 0) return false;

        ulong handle = sim.Handle;
        var (send, serial) = _objectOwnersTracker.Begin(handle, localId);
        if (!send) return true;

        _client.Parcels.RequestObjectOwners(sim, localId);

        _ = Task.Delay(ParcelObjectOwnersTimeout).ContinueWith(_ =>
        {
            if (_objectOwnersTracker.OnTimeout(serial) is { } failed)
                RaiseParcelObjectOwnersFailed(new ParcelObjectOwnersFailure(failed.Region, failed.LocalId));
        }, TaskScheduler.Default);
        return true;
    }

    private void RegisterObjectOwners()
    {
        // The EventQueue form (Second Life): an event with the simulator.
        _client.Parcels.ParcelObjectOwnersReply += OnObjectOwnersEvent;
        // The UDP form (OpenSim): LibreMetaverse registers no handler for it.
        _client.Network.RegisterCallback(PacketType.ParcelObjectOwnersReply, OnObjectOwnersPacket);
    }

    private void UnregisterObjectOwners()
    {
        _client.Parcels.ParcelObjectOwnersReply -= OnObjectOwnersEvent;
        _client.Network.UnregisterCallback(PacketType.ParcelObjectOwnersReply, OnObjectOwnersPacket);
        _objectOwnersTracker.Reset();
    }

    private void OnObjectOwnersEvent(object? sender, ParcelObjectOwnersReplyEventArgs e)
    {
        try { HandleObjectOwners(e.Simulator, ParcelObjectOwnersMapper.RowsFrom(e.PrimOwners)); }
        catch (Exception ex) { Console.Error.WriteLine($"[ObjectOwners] a reply could not be handled: {ex.Message}"); }
    }

    private void OnObjectOwnersPacket(object? sender, PacketReceivedEventArgs e)
    {
        try
        {
            if (e.Packet is not ParcelObjectOwnersReplyPacket reply) return;
            HandleObjectOwners(e.Simulator, ParcelObjectOwnersMapper.RowsFrom(reply));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ObjectOwners] a reply could not be handled: {ex.Message}");
        }
    }

    private void HandleObjectOwners(Simulator? sim, IReadOnlyList<OwnerRow> rows)
    {
        // Only the agent's own region: a neighbour's list is not what the window asked for.
        if (sim == null || sim != _client.Network.CurrentSim) return;

        var list = _objectOwnersTracker.OnReply(sim.Handle, rows);
        if (list != null) RaiseParcelObjectOwnersReceived(list);
    }

    // A throwing subscriber must not unwind into LibreMetaverse's packet loop.
    private void RaiseParcelObjectOwnersReceived(ParcelObjectOwners owners)
    {
        try { ParcelObjectOwnersReceived?.Invoke(this, owners); }
        catch (Exception ex) { Console.Error.WriteLine($"[ObjectOwners] subscriber threw: {ex.Message}"); }
    }

    private void RaiseParcelObjectOwnersFailed(ParcelObjectOwnersFailure failure)
    {
        try { ParcelObjectOwnersFailed?.Invoke(this, failure); }
        catch (Exception ex) { Console.Error.WriteLine($"[ObjectOwners] subscriber threw: {ex.Message}"); }
    }
}
