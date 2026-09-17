using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using LibreMetaverse;
using LibreMetaverse.Imaging;
using LibreMetaverse.Messages.Linden;
using LibreMetaverse.Packets;
using LibreMetaverse.StructuredData;
using Microsoft.Extensions.Logging;
using SLNG.Core;

namespace SLNG.Net;

// GridSession, Environment part.
//
// Region and parcel environment (EEP/windlight), sun time, terrain patches and the
// parcel-properties poll.
//
// Split out of the single 10,042-line GridSession.cs -- a pure move, no member
// changed. The class is still one type; `partial` only spreads it over files that
// can be read, reviewed and merged independently.
public sealed partial class GridSession
{
    private void OnRegionInfoPacket(object? sender, PacketReceivedEventArgs e) => RepollEnvironment();

    /// <summary>Cancelled in <see cref="Dispose"/>; stops <see cref="ParcelEnvironmentPollLoopAsync"/>.</summary>
    private readonly CancellationTokenSource _parcelEnvironmentPollCts = new();

    /// <summary>How often <see cref="ParcelEnvironmentPollLoopAsync"/> re-checks the current
    /// parcel's environment (BUG-NET-09).
    ///
    /// A parcel-only Windlight/EEP edit by someone else has no signal SLNG can react to: the real
    /// viewer detects it from a <c>ParcelEnvironmentVersion</c> field inside an unsolicited
    /// <c>ParcelProperties</c> push (<c>llviewerparcelmgr.cpp</c>), but LibreMetaverse's
    /// <c>ParcelPropertiesMessage</c> never parses that field, and its public API exposes no raw
    /// LLSD for that message either (the delegate that would have -- see the commented-out
    /// <c>EventQueueCallback(string, OSD, Simulator)</c> overload in <c>Caps.cs</c> -- was replaced
    /// by a typed-only one before that field was ever added upstream). It cannot be read from
    /// outside the pinned package. Polling is the only way left.
    ///
    /// Deliberately set to a short 30s for the first live verification of this fix (per the user:
    /// "lass uns mal auf 30 sekunden gehen und wir gehen dann runter") -- once a parcel-only edit
    /// is confirmed to actually show up within one interval, this should be relaxed to something
    /// far less chatty (a couple of minutes) so a stationary session isn't asking a capability the
    /// vast majority of ticks find unchanged. This constant is the one place to change that.</summary>
    private static readonly TimeSpan ParcelEnvironmentPollInterval = TimeSpan.FromSeconds(30);

    /// <summary>Re-checks the current parcel's environment every <see cref="ParcelEnvironmentPollInterval"/>
    /// (BUG-NET-09). Deliberately reuses <see cref="RepollEnvironment"/> rather than fetching
    /// anything itself: that method already single-flights, respects <see cref="RepollMinInterval"/>,
    /// re-resolves the agent's current parcel and its EEP settings on every call (the
    /// <c>hasExt</c> branch of <see cref="FetchRegionEnvironmentAsync"/> does this unconditionally,
    /// not just at login), skips the legacy Windlight fetch, and publishes nothing unless the
    /// result actually changed. A tick when nothing changed costs exactly the same one HTTP GET a
    /// RegionInfo-triggered repoll would have cost anyway -- this adds a second trigger source, not
    /// a second request shape.</summary>
    private async Task ParcelEnvironmentPollLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(ParcelEnvironmentPollInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_client.Network.CurrentSim == null) continue;
                RepollEnvironment();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path -- Dispose cancels _parcelEnvironmentPollCts.
        }
    }

    /// <summary>Shortest gap between two environment re-polls.
    ///
    /// Not a guess: OpenSim's own <c>EnvironmentModule.UpdateEnvTime</c> refuses to push a client
    /// environment update more often than every 2.5 s ("this will be a conf option"). That is the
    /// server's own statement of the finest granularity at which an environment change is
    /// considered meaningful, so re-polling faster than it cannot learn anything new.</summary>
    private static readonly TimeSpan RepollMinInterval = TimeSpan.FromSeconds(2.5);

    private readonly object _repollLock = new();

    private bool _repollRunning;

    private bool _repollRequested;

    private DateTime _lastRepollUtc = DateTime.MinValue;

    private string? _lastEnvironmentFingerprint;

    /// <summary>The last parcel scope we actually established, carried forward so a poll that
    /// failed to establish one can still be compared on its region half. See
    /// <see cref="EnvironmentFingerprint"/> for what goes wrong without it.</summary>
    private int _lastParcelId = -1;

    private string? _lastParcelEnvironmentLlsd;

    /// <summary>Re-polls the environment capabilities after a RegionInfo packet.
    ///
    /// RegionInfo carries no environment payload -- it is only the "something about this region
    /// changed" signal -- so the answer is another capability fetch, same as at login. It stays
    /// unfiltered by intent: RegionInfo also arrives for estate and terrain edits, and the viewer
    /// re-polls on all of them (llenvironment.cpp:886) rather than trying to tell them apart.
    ///
    /// What it must NOT stay is unbounded. This was a bare <c>Task.Run</c> per packet, and on a
    /// busy region that is a request flood aimed at someone else's server: measured on OSGrid's
    /// Lbsa Plaza, 2649 re-polls in one hour-long session -- about 45 a minute, sustained, each
    /// firing three HTTP capability GETs and each re-delivering a byte-identical 6060-character
    /// payload. Roughly 8000 requests from one client. The real viewer receives the same packets
    /// and survives them because <c>requestRegion</c> issues one coalesced request, not three
    /// uncoordinated ones.
    ///
    /// Three bounds, in order of how much each removes:
    /// 1. One re-poll at a time. Overlapping fetches collapse into a single latch, so a burst
    ///    becomes one trailing refresh instead of a pile-up of concurrent HTTP calls.
    /// 2. <see cref="RepollMinInterval"/> between fetches.
    /// 3. Nothing is published unless the payload actually changed. This is what keeps the
    ///    downstream cost at zero: no parse, no event, no on-screen log, and no rewriting the
    ///    Phase-A LLSD dump file with bytes identical to the ones already in it.</summary>
    private void RepollEnvironment()
    {
        lock (_repollLock)
        {
            _repollRequested = true;
            if (_repollRunning) return;
            _repollRunning = true;
        }

        _ = Task.Run(RepollEnvironmentLoopAsync);
    }

    private async Task RepollEnvironmentLoopAsync()
    {
        try
        {
            while (true)
            {
                lock (_repollLock)
                {
                    if (!_repollRequested)
                    {
                        _repollRunning = false;
                        return;
                    }
                    _repollRequested = false;
                }

                var since = DateTime.UtcNow - _lastRepollUtc;
                if (since < RepollMinInterval)
                    await Task.Delay(RepollMinInterval - since).ConfigureAwait(false);
                _lastRepollUtc = DateTime.UtcNow;

                try
                {
                    // The legacy Windlight capability is a Phase-A diagnostic and a fallback for
                    // regions that offer nothing newer. Once EEP has answered there is nothing for
                    // it to add, so a live re-poll skips it -- a third of the requests, gone.
                    var (capture, environment) = await FetchRegionEnvironmentAsync(
                        includeLegacyAlongsideExt: false).ConfigureAwait(false);
                    if (capture == null || !string.IsNullOrEmpty(capture.Error))
                    {
                        if (capture?.Error?.Contains("ServiceUnavailable") == true)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
                        }
                        continue;
                    }

                    string fingerprint = FingerprintAndRememberParcel(capture);
                    if (fingerprint == _lastEnvironmentFingerprint) continue;
                    _lastEnvironmentFingerprint = fingerprint;

                    RegionEnvironmentCaptured?.Invoke(this, capture);
                    if (environment != null) RegionEnvironmentReceived?.Invoke(this, environment);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ENV] live update capture failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            // The loop owns the _repollRunning latch; losing it to an unexpected throw would wedge
            // re-polling off for the rest of the session.
            Console.WriteLine($"[ENV] repoll loop stopped: {ex.Message}");
            lock (_repollLock) { _repollRunning = false; }
        }
    }

    /// <summary>Fingerprints a capture and updates the remembered parcel scope.
    ///
    /// The environment is a per-parcel setting, and the parcel id comes from a UDP
    /// ParcelProperties round-trip that can simply not answer in time — <see
    /// cref="ResolveAgentParcelIdAsync"/> then returns -1 and the parcel LLSD stays null because
    /// we never got to ask for it. That is <b>"we did not find out"</b>, not "the parcel has no
    /// environment", and the two must not look the same to the change detector.
    ///
    /// They did. Every timed-out lookup flipped the id between -1 and the real one and the parcel
    /// LLSD between null and its value, so on a busy region the fingerprint differed on every
    /// single poll and the gate published every time despite being nominally in place. Measured on
    /// Lbsa Plaza after the gate landed: 217 republishes in 52 minutes, one per day-cycle tick,
    /// unbroken.
    ///
    /// So a poll that established no parcel is compared on the region half against the last parcel
    /// scope we did establish. The region half stays live — a genuine region-level change is still
    /// caught while the parcel half is unknown — and an unanswered lookup contributes nothing.</summary>
    private string FingerprintAndRememberParcel(RegionEnvironmentCapture c)
    {
        if (c.ParcelId >= 0)
        {
            _lastParcelId = c.ParcelId;
            _lastParcelEnvironmentLlsd = c.ParcelEnvironmentLlsd;
        }

        return EnvironmentFingerprint(
            c,
            c.ParcelId >= 0 ? c.ParcelId : _lastParcelId,
            c.ParcelId >= 0 ? c.ParcelEnvironmentLlsd : _lastParcelEnvironmentLlsd);
    }

    /// <summary>Everything that decides whether a fetched environment is the same one already
    /// published. The region handle is part of it so that crossing back into a region seen earlier
    /// still republishes -- the renderer's state moved on in between.
    ///
    /// The parcel scope is passed in rather than read off the capture, because an unresolved
    /// lookup must contribute the previous scope instead of a fresh "no parcel" —
    /// see <see cref="FingerprintAndRememberParcel"/>.</summary>
    internal static string EnvironmentFingerprint(
        RegionEnvironmentCapture c, int parcelId, string? parcelEnvironmentLlsd) =>
        string.Join('\u001f',
            c.RegionHandle, parcelId, c.DayLength, c.DayOffset,
            c.ParcelDayLength, c.ParcelDayOffset, c.IsDefault,
            c.ExtEnvironmentLlsd ?? string.Empty,
            parcelEnvironmentLlsd ?? string.Empty,
            c.LegacyEnvironmentLlsd ?? string.Empty);

    private void OnSimulatorViewerTimePacket(object? sender, PacketReceivedEventArgs e)
    {
        if (e.Packet is SimulatorViewerTimeMessagePacket timePacket)
        {
            // Update thread-safe properties from the packet payload
            SimUnixTime = timePacket.TimeInfo.UsecSinceStart;
            SunPhase = timePacket.TimeInfo.SunPhase;
        }
    }

    /// <summary>Guards <see cref="OnEventQueueRunning"/>'s environment capture against firing more
    /// than once per <c>Simulator</c> instance -- see that method's doc comment for why this exists
    /// (BUG-NET-08).</summary>
    private Simulator? _environmentCapturedForSim;

    /// <summary>Fetches the current region's environment through both environment capabilities and
    /// returns it verbatim, alongside which capabilities the simulator actually advertised.
    ///
    /// Both are asked for, not just the first that answers. The point of Phase A is to learn what
    /// this grid does -- knowing that a sim offers EEP *and* what its legacy Windlight fallback
    /// looks like is the whole reason to run it, and asking twice costs two HTTP GETs once per
    /// region.
    ///
    /// Note that LibreMetaverse's <c>RegionEnvironmentUpdated</c> event fires only as a result of
    /// these calls -- <c>EnvironmentManager</c> registers no EventQueue callback (verified in its
    /// source: the event is raised at exactly one place, inside the GET). So there is no push
    /// notification to subscribe to, and an environment changed server-side after login will not
    /// reach us until something re-polls. Anything that needs live updates has to poll or add the
    /// EventQueue handler upstream.</summary>
    public async Task<RegionEnvironmentCapture?> CaptureRegionEnvironmentAsync(CancellationToken cancellationToken = default)
        => (await FetchRegionEnvironmentAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false)).Capture;

    /// <summary>Does the actual fetching, and produces BOTH results from the one pair of requests:
    /// the raw capture and the parsed model. Kept as one call because they come from the same two
    /// HTTP GETs -- fetching twice to serve two consumers would double the cost for nothing, and
    /// re-parsing the capture's notation text would be a lossy way to reach the same place.</summary>
    /// <summary>Local id of the parcel the agent is standing on, or -1 if the simulator did not
    /// answer in time.
    ///
    /// Asks for the parcel under the agent rather than downloading the whole parcel map
    /// (<c>RequestAllSimParcelsAsync</c>, which walks the region in 750 ms steps and is far more
    /// traffic than one id is worth). The coordinate overload of
    /// <c>RequestParcelProperties</c> takes a bounding box, and a degenerate box at the agent's
    /// own position is exactly the "which parcel am I on" question — the same one the viewer's
    /// <c>LLViewerParcelMgr</c> asks before requesting a parcel environment.
    ///
    /// The reply arrives on a LibreMetaverse network thread, so the handler only completes a
    /// <see cref="TaskCompletionSource{TResult}"/> and touches no world state (AGENTS.md).</summary>
    private async Task<int> ResolveAgentParcelIdAsync(
        Simulator sim, CancellationToken cancellationToken)
    {
        // A sequence id of our own so a parcel-crossing push, or another consumer's request, cannot
        // be mistaken for the answer to this one.
        int sequenceId = Interlocked.Increment(ref _parcelSequenceId);
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnParcelProperties(object? sender, ParcelPropertiesEventArgs e)
        {
            if (e.SequenceID != sequenceId) return;
            completion.TrySetResult(e.Result == ParcelResult.Single ? e.Parcel.LocalID : -1);
        }

        _client.Parcels.ParcelProperties += OnParcelProperties;
        try
        {
            var pos = _client.Self.SimPosition;
            _client.Parcels.RequestParcelProperties(sim, pos.Y, pos.X, pos.Y, pos.X, sequenceId, false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ParcelLookupTimeout);
            using var registration = timeout.Token.Register(() => completion.TrySetResult(-1));
            return await completion.Task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A parcel id we could not obtain is not a failure worth aborting the environment fetch
            // over -- the region scope below is a correct, if less specific, answer.
            return -1;
        }
        finally
        {
            _client.Parcels.ParcelProperties -= OnParcelProperties;
        }
    }

    /// <summary>Gates the one-time diagnostic log of the resolved legacy EnvironmentSettings
    /// capability URI -- see its use in <see cref="FetchRegionEnvironmentAsync"/>.</summary>
    private bool _legacyEnvironmentCapLogged;

    private async Task<(RegionEnvironmentCapture? Capture, RegionEnvironmentEvent? Environment)>
        FetchRegionEnvironmentAsync(
            bool includeLegacyAlongsideExt = true, CancellationToken cancellationToken = default)
    {
        var sim = _client.Network.CurrentSim;
        if (sim == null) return (null, null);

        bool hasExt = sim.Caps?.CapabilityURI("ExtEnvironment") != null;
        var legacyCapUri = sim.Caps?.CapabilityURI("EnvironmentSettings");
        bool hasLegacy = legacyCapUri != null;

        // Diagnostic only, once per process: the sim hands this capability out as a per-region,
        // per-session URI (not a fixed host like the bake-texture CDN), so there is no way to
        // give a static address to check in an HTTP proxy -- print the actual resolved one the
        // first time it's seen, so a live capture can be filtered to exactly this request instead
        // of guessed at from the repeated "GET non-success: ServiceUnavailable" warning alone.
        if (hasLegacy && !_legacyEnvironmentCapLogged)
        {
            _legacyEnvironmentCapLogged = true;
            Console.Error.WriteLine($"[Environment] legacy EnvironmentSettings capability resolved to: {legacyCapUri}");
        }

        string? extLlsd = null;
        string? legacyLlsd = null;
        int dayLength = 0, dayOffset = 0;
        bool isDefault = false;
        string? error = null;

        int parcelId = -1;
        string? parcelLlsd = null;
        int parcelDayLength = 0, parcelDayOffset = 0;

        OSD? extSettings = null;
        OSD? legacySettings = null;
        OSD? parcelSettings = null;

        try
        {
            if (hasExt)
            {
                var extCap = sim.Caps!.CapabilityURI("ExtEnvironment")!;
                var (extMap, extStatus) = await GetCapabilityMapAsync(extCap, cancellationToken).ConfigureAwait(false);
                ExtEnvironmentMessage? ext = null;
                if (extMap != null)
                {
                    ext = new ExtEnvironmentMessage();
                    ext.Deserialize(extMap);
                }
                else if (extStatus.HasValue)
                {
                    error += $"[ExtEnv HTTP {extStatus}] ";
                }
                if (ext != null && !ext.Success)
                {
                    error += $"[ExtEnv Failed: {ext.Message}] ";
                }
                if (ext?.Environment is { } data)
                {
                    dayLength = data.DayLength;
                    dayOffset = data.DayOffset;
                    isDefault = data.IsDefault;
                    // Null DayCycle is not a failure: it means this scope inherits its parent's
                    // environment. The cap flags above are what tell the two apart downstream.
                    if (data.DayCycle != null)
                    {
                        extSettings = data.DayCycle;
                        extLlsd = OSDParser.SerializeLLSDNotationFormatted(data.DayCycle);
                    }
                }
            }

            // The environment is a PER-PARCEL setting. OpenSim's cap handler reads a `parcelid`
            // query parameter and resolves it through LandChannel.GetLandObject
            // (EnvironmentModule.cs:459-495); the viewer asks per parcel via
            // LLEnvironment::requestParcel and only falls back to the region. Asking for the region
            // alone therefore shows the REGION's sky on a parcel that overrides it — which on The
            // Dangazi Forest put our sun at +32 degrees while Firestorm, on "shared environment",
            // showed it at the horizon.
            //
            // A null DayCycle here means "this parcel inherits the region's" and is the common case,
            // so it is not treated as a failure.
            if (hasExt)
            {
                parcelId = await ResolveAgentParcelIdAsync(sim, cancellationToken).ConfigureAwait(false);
                if (parcelId >= 0)
                {
                    var parcelCap = new Uri($"{sim.Caps!.CapabilityURI("ExtEnvironment")}?parcel_id={parcelId}");
                    var (parcelMap, parcelStatus) = await GetCapabilityMapAsync(parcelCap, cancellationToken).ConfigureAwait(false);
                    if (parcelMap != null)
                    {
                        var parcelEnv = new ExtEnvironmentMessage();
                        parcelEnv.Deserialize(parcelMap);
                        if (parcelEnv.Environment is { DayCycle: not null } pdata)
                        {
                            parcelSettings = pdata.DayCycle;
                            parcelLlsd = OSDParser.SerializeLLSDNotationFormatted(pdata.DayCycle);
                            parcelDayLength = pdata.DayLength;
                            parcelDayOffset = pdata.DayOffset;
                        }
                    }
                    else if (parcelStatus.HasValue)
                    {
                        error += $"[ExtEnv Parcel HTTP {parcelStatus}] ";
                    }
                }
            }

            // Asked for alongside EEP only when someone is going to read it: at login, where the
            // point is to record what this grid actually offers. A region with no EEP capability
            // still always asks, because there it is not a diagnostic but the only source there is.
            if (hasLegacy && (includeLegacyAlongsideExt || !hasExt))
            {
                var (legacyMap, legacyStatus) = await GetCapabilityMapAsync(legacyCapUri!, cancellationToken).ConfigureAwait(false);
                if (legacyMap != null)
                {
                    var legacy = new LegacyEnvironmentMessage();
                    legacy.Deserialize(legacyMap);
                    if (legacy.Settings != null)
                    {
                        legacySettings = legacy.Settings;
                        legacyLlsd = OSDParser.SerializeLLSDNotationFormatted(legacy.Settings);
                    }
                }
                else if (legacyStatus.HasValue)
                {
                    error += $"[Legacy HTTP {legacyStatus}] ";
                }
            }
        }
        catch (Exception ex)
        {
            // Recorded rather than thrown: "the sim has no environment" and "we failed to ask"
            // look identical in the output otherwise, and telling them apart is the point.
            error += $"[Exception: {ex.Message}]";
        }

        var capture = new RegionEnvironmentCapture(
            sim.Handle,
            sim.Name ?? string.Empty,
            hasExt,
            hasLegacy,
            extLlsd,
            legacyLlsd,
            dayLength,
            dayOffset,
            isDefault,
            error,
            parcelId,
            parcelLlsd,
            parcelDayLength,
            parcelDayOffset);

        // Resolution order matches the viewer's: the PARCEL's own environment wins over the
        // region's (LLEnvironment::requestParcel, and OpenSim resolves by position through
        // LandChannel.GetLandObject), then EEP over legacy Windlight — the latter is the newer
        // protocol and carries a full day cycle where the old capability can only describe one
        // fixed sky. Falling back rather than preferring one exclusively is what lets the same code
        // path serve an OpenSim region offering only the old capability.
        //
        // Each scope brings its OWN day length and offset. Reusing the region's with a parcel's day
        // cycle would evaluate the right curve at the wrong time of day.
        var (settings, source, length, offset) =
            parcelSettings != null ? (parcelSettings, EnvironmentSource.ExtendedEnvironment, parcelDayLength, parcelDayOffset) :
            extSettings != null ? (extSettings, EnvironmentSource.ExtendedEnvironment, dayLength, dayOffset) :
            legacySettings != null ? (legacySettings, EnvironmentSource.LegacyWindlight, dayLength, dayOffset) :
            (null, EnvironmentSource.Default, dayLength, dayOffset);

        var cycle = settings != null
            ? EnvironmentLlsdParser.ParseDayCycle(
                settings,
                length > 0 ? length : DayCycle.Default.DayLengthSeconds,
                offset,
                legacyWindlight: source == EnvironmentSource.LegacyWindlight)
            : DayCycle.Default;

        return (capture, new RegionEnvironmentEvent(sim.Handle, cycle, source));
    }

    private void OnLandPatchReceived(object? sender, LandPatchReceivedEventArgs e)
    {
        TerrainPatchReceived?.Invoke(this, new TerrainPatchEvent(
            e.Simulator.Handle,
            e.X,
            e.Y,
            e.PatchSize,
            e.HeightMap,
            (int)e.Simulator.SizeX,
            (int)e.Simulator.SizeY
        ));
    }

    /// <summary>The name of the parcel the agent is currently standing on, or null/empty if unknown.</summary>
    public string? CurrentParcelName => _currentParcelName;

    /// <summary>Requests the simulator to push parcel properties for the agent's current position.</summary>
    public void RequestCurrentParcelProperties()
    {
        var sim = _client.Network.CurrentSim;
        if (sim == null || !_client.Network.Connected) return;
        var pos = _client.Self.SimPosition;
        int seq = Interlocked.Increment(ref _parcelSequenceId);
        _client.Parcels.RequestParcelProperties(sim, pos.Y, pos.X, pos.Y, pos.X, seq, false);
    }

    private void OnParcelPropertiesReceived(object? sender, LibreMetaverse.ParcelPropertiesEventArgs e)
    {
        if (e.Result == LibreMetaverse.ParcelResult.Single && !string.IsNullOrWhiteSpace(e.Parcel.Name))
        {
            _currentParcelName = e.Parcel.Name.Trim();
        }
    }
}
