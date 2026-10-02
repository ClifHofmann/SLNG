using LibreMetaverse;
using LibreMetaverse.Packets;
using SLNG.Core;

namespace SLNG.Net;

// GridSession, Covenant part (FEAT-LAND-05): the data behind the "Covenant" tab of the Land-Info window and,
// later, of the Region/Estate window. A covenant belongs to the ESTATE, so there is one per region.
//
// Two steps, exactly as the reference viewer does them (process_covenant_reply, llviewermessage.cpp:6782):
//   1. EstateCovenantRequest (UDP, reliable) -> EstateCovenantReply (UDP): estate name, estate owner,
//      timestamp and the covenant's ASSET ID. The reply does NOT carry the text.
//   2. When the id is not nil, the text is a notecard asset fetched with a UDP TransferRequest of source
//      type SimEstate (gAssetStorage->getEstateAsset, EstateAssetType 0 = covenant). There is no HTTP /
//      ViewerAsset route for it in the viewer, so none is built here.
// LibreMetaverse already has both halves (EstateTools.RequestCovenant and RequestCovenantNotecardAsync) and
// they are reused. Its EstateCovenantReply EVENT is not: it carries no simulator, and a reply racing a
// region change must not be filed under the wrong region, so the raw packet is read instead.
//
// THREADING (AGENTS.md): handlers run on LibreMetaverse network / thread-pool threads. They build an
// immutable record and raise an event; no World state is touched. The app marshals to the main thread.
public sealed partial class GridSession
{
    /// <summary>How long a requested covenant may stay unanswered before <see cref="CovenantFailed"/> fires.</summary>
    internal static readonly TimeSpan CovenantTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long the covenant notecard may take to arrive before the text is reported as failed.
    /// LibreMetaverse itself waits 15 s for the transfer header, so this is the outer bound.</summary>
    internal static readonly TimeSpan CovenantTextTimeout = TimeSpan.FromSeconds(30);

    private readonly CovenantInfoTracker _covenantTracker = new();

    // Serial of the request that is waiting for its reply; 0 when none is. Compare-exchanged by the reply
    // and by the timeout, so exactly one of them wins.
    private int _covenantSerial;
    private int _covenantPending;

    /// <summary>Raised on a NETWORK thread when covenant data is available: the answer to
    /// <see cref="RequestCovenant"/> (header first, with <see cref="CovenantInfo.TextState"/> Loading when
    /// the estate has a covenant), again when the text arrived or could not be fetched, and when the
    /// estate changed its covenant while the agent is there. A repeat of what was already shown is not
    /// raised unless it answers a request. Marshal to the main thread before touching UI.</summary>
    public event EventHandler<CovenantInfo>? CovenantReceived;

    /// <summary>Raised on a NETWORK thread when a requested covenant got no reply in time. Distinct from
    /// an estate with no covenant (a <see cref="CovenantReceived"/> with state None) and from a covenant
    /// whose text failed (state Failed).</summary>
    public event EventHandler<CovenantFailure>? CovenantFailed;

    /// <summary>The last covenant raised for the region the agent is in now, or null (none yet, or it was
    /// for another region). Reads state only; never sends. A covenant does not change per parcel, so a
    /// window that finds one here need not ask again.</summary>
    public CovenantInfo? LastCovenant
    {
        get
        {
            var held = _covenantTracker.Current;
            return held != null && held.RegionHandle == CurrentRegionHandle ? held : null;
        }
    }

    /// <summary>Asks the current region for its estate covenant. The answer arrives through
    /// <see cref="CovenantReceived"/>, or <see cref="CovenantFailed"/>. Returns false, and sends nothing,
    /// when not connected.</summary>
    public bool RequestCovenant()
    {
        var sim = _client.Network.CurrentSim;
        if (sim == null || !_client.Network.Connected) return false;

        int serial = Interlocked.Increment(ref _covenantSerial);
        Volatile.Write(ref _covenantPending, serial);
        _client.Estate.RequestCovenant();

        ulong handle = sim.Handle;
        _ = Task.Delay(CovenantTimeout).ContinueWith(_ =>
        {
            if (Interlocked.CompareExchange(ref _covenantPending, 0, serial) == serial)
                RaiseCovenantFailed(new CovenantFailure(handle));
        }, TaskScheduler.Default);
        return true;
    }

    private void RegisterCovenant() =>
        _client.Network.RegisterCallback(PacketType.EstateCovenantReply, OnCovenantReplyPacket);

    private void UnregisterCovenant()
    {
        _client.Network.UnregisterCallback(PacketType.EstateCovenantReply, OnCovenantReplyPacket);
        _covenantTracker.Reset();
        Volatile.Write(ref _covenantPending, 0);
    }

    private void OnCovenantReplyPacket(object? sender, PacketReceivedEventArgs e)
    {
        try
        {
            if (e.Packet is not EstateCovenantReplyPacket reply) return;

            // Only the agent's own region: the answer is filed under its handle.
            var sim = e.Simulator;
            if (sim == null || sim != _client.Network.CurrentSim) return;

            bool solicited = Interlocked.Exchange(ref _covenantPending, 0) != 0;
            var mapped = CovenantInfoMapper.From(reply, sim.Handle);
            var (raised, needsText) = _covenantTracker.OnReply(mapped, solicited);
            if (raised == null) return;

            RaiseCovenantReceived(raised);
            if (needsText) _ = FetchCovenantTextAsync(raised, sim);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Covenant] a covenant reply could not be handled: {ex.Message}");
        }
    }

    // Fetches the notecard and completes the record. Every outcome ends in OnText, so the tracker never
    // keeps a fetch "running" that is not.
    private async Task FetchCovenantTextAsync(CovenantInfo header, Simulator sim)
    {
        Guid id = header.CovenantId!.Value;
        string? text = null;
        try
        {
            using var cts = new CancellationTokenSource(CovenantTextTimeout);
            var asset = await _client.Estate.RequestCovenantNotecardAsync(new UUID(id), sim, cts.Token).ConfigureAwait(false);
            text = CovenantNotecard.Extract(asset?.AssetData);
        }
        catch (OperationCanceledException)
        {
            // Timed out: reported as a failed text below.
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Covenant] the covenant text could not be fetched: {ex.Message}");
        }

        try
        {
            var done = _covenantTracker.OnText(header.RegionHandle, id, text);
            if (done != null) RaiseCovenantReceived(done);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Covenant] the covenant text could not be stored: {ex.Message}");
        }
    }

    // A throwing subscriber must not unwind into LibreMetaverse's packet loop.
    private void RaiseCovenantReceived(CovenantInfo info)
    {
        try { CovenantReceived?.Invoke(this, info); }
        catch (Exception ex) { Console.Error.WriteLine($"[Covenant] subscriber threw: {ex.Message}"); }
    }

    private void RaiseCovenantFailed(CovenantFailure failure)
    {
        try { CovenantFailed?.Invoke(this, failure); }
        catch (Exception ex) { Console.Error.WriteLine($"[Covenant] subscriber threw: {ex.Message}"); }
    }
}
