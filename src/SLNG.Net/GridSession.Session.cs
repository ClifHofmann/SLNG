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

// GridSession, Session part.
//
// Login, region connect/disconnect, capabilities, teleport, map lookups and the
// maturity preference.
//
// Split out of the single 10,042-line GridSession.cs -- a pure move, no member
// changed. The class is still one type; `partial` only spreads it over files that
// can be read, reviewed and merged independently.
public sealed partial class GridSession
{
    private void OnSimConnected(object? sender, LibreMetaverse.SimConnectedEventArgs e)
    {
        var sim = e.Simulator;
        var startHeights = new float[] { sim.TerrainStartHeight00, sim.TerrainStartHeight01, sim.TerrainStartHeight10, sim.TerrainStartHeight11 };
        var heightRanges = new float[] { sim.TerrainHeightRange00, sim.TerrainHeightRange01, sim.TerrainHeightRange10, sim.TerrainHeightRange11 };

        RaiseTerrainSettings(new TerrainSettingsEvent(
            sim.Handle,
            sim.TerrainDetail0.Guid, sim.TerrainDetail1.Guid, sim.TerrainDetail2.Guid, sim.TerrainDetail3.Guid,
            startHeights, heightRanges,
            sim.WaterHeight,
            (int)sim.SizeX, (int)sim.SizeY
        ));

        // LibreMetaverse also raises SimConnected for a neighbor sim connected only for
        // interest-list purposes near a region border -- not a region WE moved into. Only treat
        // this as "we moved" (and recenter the floating origin) when it's the primary sim; by this
        // point NetworkManager.Connect has already called SetCurrentSim if this connection was the
        // default one, so CurrentSim reliably reflects that.
        if (sim == _client.Network.CurrentSim)
        {
            _appearanceReadinessLogged = false; // FEAT-AVATAR-01: re-check the wearable-edit path for the new region
            // BUG-NET-13: pair with WorldSimulation's [RegionData] lines so a live session can see
            // whether the sim re-sends terrain + objects after a teleport back into a region we
            // previously tore down. "[RegionEnter] X" with no following "[RegionData] ... for X" is
            // the sim not streaming (interest list / camera), not a render bug.
            Console.WriteLine($"[RegionEnter] {sim.Name} ({sim.Handle}) is now the current region");
            RegionConnected?.Invoke(this, sim.Handle);
        }
        else
        {
            // BUG-NET-03: a neighbor circuit just came up. Infrequent lifecycle event -- log it
            // unconditionally so a live session can confirm the cross-border fetch is working
            // (or see exactly which neighbors the grid offered and we connected).
            Console.WriteLine($"[Neighbor] connected {sim.Name} ({sim.Handle}) {NeighborDir(sim.Handle)}");
        }
    }

    /// <summary>Compass direction of a neighbor region handle relative to the current sim, for the
    /// BUG-NET-03 diagnostic line. Region grid is 256 m per step.</summary>
    private string NeighborDir(ulong neighborHandle)
    {
        var cur = _client.Network.CurrentSim;
        if (cur == null) return "";
        long dx = ((long)(uint)(neighborHandle >> 32) - (long)(uint)(cur.Handle >> 32)) / 256;
        long dy = ((long)(uint)(neighborHandle & 0xFFFFFFFF) - (long)(uint)(cur.Handle & 0xFFFFFFFF)) / 256;
        string ns = dy > 0 ? "N" : dy < 0 ? "S" : "";
        string ew = dx > 0 ? "E" : dx < 0 ? "W" : "";
        return $"{ns}{ew} ({dx:+0;-0;0},{dy:+0;-0;0})";
    }

    /// <summary>BUG-NET-04: fires whenever <c>Network.CurrentSim</c> changes -- both on a normal
    /// walking border-crossing AND on a teleport, which <see cref="OnSimConnected"/> alone cannot
    /// tell apart (it only knows "a sim connected", not "we left one behind"). The existing
    /// cleanup path -- the grid sends <c>DisableSimulator</c> for a region we've left, which
    /// <see cref="OnSimDisconnected"/> turns into <c>World.RemoveRegion</c> -- works for a border
    /// crossing (the old region genuinely becomes/stays a live BUG-NET-03 neighbor circuit, and
    /// the grid decides when to drop it). It does **not** reliably work for a teleport to a
    /// distant region: measured live, "nach dem Teleport sehe ich noch die sim auf der ich gerade
    /// war" -- the old region's terrain and objects stayed rendered, un-recentered, because
    /// nothing forced the cleanup client-side and the originating sim's own `DisableSimulator`
    /// either never arrived or arrived too late to matter for what the user was already seeing.
    ///
    /// Fix: if the PREVIOUS sim is now more than one region-grid step (256 m) away from the NEW
    /// current sim -- i.e. it cannot possibly be a legitimate BUG-NET-03 neighbor of where we are
    /// now -- close its circuit immediately instead of waiting for the server. A same-grid walking
    /// crossing (dx/dy always ≤ 1) is left entirely alone: that is BUG-NET-03's existing, working
    /// path, and this must not race or duplicate it.
    ///
    /// BUG-NET-13: the eager path now calls <c>DisconnectSim</c> rather than only raising a
    /// synthetic <c>RegionDisconnectedReceived</c> -- leaving the origin circuit connected let it
    /// keep streaming updates that re-created entities in the just-removed region (RID leak,
    /// disposed-texture continuations, NaN-transform flood). See the inline comment below.</summary>
    private void OnSimChanged(object? sender, LibreMetaverse.SimChangedEventArgs e)
    {
        _currentParcelName = null;
        var oldSim = e.PreviousSimulator;
        var newSim = _client.Network.CurrentSim;
        if (oldSim == null || newSim == null || oldSim.Handle == newSim.Handle) return;

        var (dx, dy) = RegionGridOffset(oldSim.Handle, newSim.Handle);
        if (Math.Abs(dx) <= 1 && Math.Abs(dy) <= 1) return; // still a plausible neighbor -- leave to DisableSimulator

        // BUG-NET-13: the first cut of this fix only raised a synthetic RegionDisconnectedReceived.
        // That unloaded the world region but left LibreMetaverse's connection to the origin sim
        // OPEN -- and with Settings.Agent.MultipleSims = true nothing gates ObjectUpdate /
        // AvatarUpdate / TerseObjectUpdate on CurrentSim, so the origin sim kept streaming updates
        // that re-created entities in the region we had just removed. That create/teardown churn
        // leaked Mesh/Instance RIDs, fired asset-decode continuations against already-disposed
        // ImageTextures, and fed half-populated transforms into the renderer (the "Vector3 cannot
        // be normalized" flood). For a distant, non-adjacent teleport the origin sim's own
        // DisableSimulator is not guaranteed to arrive at all (the whole reason this path exists),
        // so the stale stream can run for the rest of the session.
        //
        // A real viewer closes that circuit itself on a long teleport instead of waiting for a
        // server packet. Do the same: DisconnectSim sends CloseCircuit, drops the sim from
        // LibreMetaverse's Simulators list (so its packets stop being dispatched), and fires
        // SimDisconnected -- which OnSimDisconnected already turns into the same
        // RegionDisconnectedReceived -> World.RemoveRegion cleanup. Wrapped so teleport cleanup can
        // never throw on the network thread; the synthetic invoke stays as the fallback.
        Console.WriteLine($"[Teleport] left {oldSim.Name} ({oldSim.Handle}) {dx:+0;-0;0},{dy:+0;-0;0} region-steps away -- closing the stale circuit, not waiting for DisableSimulator");
        try
        {
            _client.Network.DisconnectSim(oldSim, sendCloseCircuit: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Teleport] DisconnectSim({oldSim.Name}) failed: {ex.Message} -- falling back to world-only unload");
            RegionDisconnectedReceived?.Invoke(this, new RegionDisconnectedEvent(oldSim.Handle));
        }
    }

    /// <summary>Captures the region's environment once its capabilities are live (FEAT-ENV-01
    /// Phase A). Fire-and-forget on purpose: nothing in the login path waits on the environment,
    /// and a sim that never answers must not stall the connection.
    ///
    /// "Once" used to be an assumption, not a guarantee, and it was wrong (BUG-NET-08). This
    /// method's own name comes from LibreMetaverse's event -- <c>EventQueueRunning</c> -- which
    /// reads like a one-time "the long-poll is up" signal. It is not: LMV's
    /// <c>EventQueueClient.ConnectedResponseHandler</c> calls its <c>OnConnected</c> callback (the
    /// thing that raises this event) on EVERY successful <c>EventQueueGet</c> response, not just
    /// the first -- its own source comment ("the event queue is starting up for the first time")
    /// is simply wrong; there is no first-time gate in the code. On SL, `EventQueueGet` polls
    /// resolve roughly once a second under normal traffic, so without the guard below this handler
    /// -- and the full <see cref="FetchRegionEnvironmentAsync"/> call inside it, including the
    /// legacy Windlight fetch -- ran about once a second for the entire session, unthrottled by
    /// <see cref="RepollEnvironmentLoopAsync"/>'s cooldown (a completely different call site). This
    /// is what was still producing a sustained "503 cap invocation rate exceeded" burst on Agni
    /// even after <c>BUG-NET-07</c> made that cooldown reachable -- BUG-NET-07 fixed a real dead
    /// branch, but this was the dominant source of the traffic the whole time.</summary>
    private void OnEventQueueRunning(object? sender, LibreMetaverse.EventQueueRunningEventArgs e)
    {
        if (e.Simulator != _client.Network.CurrentSim) return;
        if (ReferenceEquals(e.Simulator, _environmentCapturedForSim)) return;
        _environmentCapturedForSim = e.Simulator;

        // FEAT-AVATAR-01: report whether system-wearable edits are safe here. Logged once per
        // region, at EventQueueRunning rather than SimConnected because the UpdateAvatarAppearance
        // capability is only resolvable after the caps handshake completes.
        LogAppearanceEditReadiness();

        // Same reason as above -- the UpdateAvatarAppearance cap the watchdog nudges through is
        // only resolvable once the caps handshake is done. Armed once per session, not per region.
        ArmSelfBakeWatchdog();

        // Fall back to the last cached self appearance if the sim never sends one this login.
        ArmSelfAppearanceRestore();

        // Re-request any Current-Outfit attachment the sim failed to rez on login.
        ArmAttachmentReconcile();

        // FEAT-AVATAR-03 (and any future cap-gated outbound send): the caps handshake for this
        // region is done now, unlike at RegionConnected.
        RegionCapabilitiesReady?.Invoke(this, e.Simulator.Handle);

        // MVP3-3: the ObjectMedia cap races region entry the exact same way -- see
        // FetchAndPublishObjectMediaAsync's doc comment. Caps are confirmed resolved now, so
        // sweep every currently-known primitive once for any MOAP fetch that failed earlier.
        RetryPendingMediaFetches(e.Simulator);

        _ = Task.Run(async () =>
        {
            try
            {
                var (capture, environment) = await FetchRegionEnvironmentAsync().ConfigureAwait(false);
                if (capture != null)
                {
                    // Seeds the change detector, so the first RegionInfo to arrive after login
                    // does not republish the environment we just delivered.
                    _lastEnvironmentFingerprint = FingerprintAndRememberParcel(capture);
                    RegionEnvironmentCaptured?.Invoke(this, capture);
                }
                if (environment != null) RegionEnvironmentReceived?.Invoke(this, environment);
            }
            catch (Exception ex)
            {
                // A diagnostic capture must never take the session down with it.
                Console.WriteLine($"[ENV] capture failed: {ex.Message}");
            }
        });
    }

    /// <summary>Fetches one capability URI's LLSD directly through <c>HttpCapsClient</c>, bypassing
    /// LibreMetaverse's <c>EnvironmentManager.GetRegionEnvironmentAsync</c> /
    /// <c>GetLegacyEnvironmentAsync</c> / <c>GetParcelEnvironmentAsync</c> wrappers (BUG-NET-07).
    ///
    /// Those three all do the same thing on a non-2xx response: <c>Logger.Warn(...)</c> and return
    /// <c>null</c>. The actual <see cref="HttpStatusCode"/> is discarded inside the wrapper --
    /// nothing distinguishes "capability returned 503" from "capability returned nothing" once it
    /// gets back here. That silently broke the one thing that was supposed to stop repeated
    /// requests from re-triggering the sim's own rate limiter:
    /// <see cref="RepollEnvironmentLoopAsync"/>'s <c>capture?.Error?.Contains("ServiceUnavailable")</c>
    /// check, which can only ever see what <see cref="FetchRegionEnvironmentAsync"/> put in
    /// <c>error</c> -- and a swallowed 503 never puts anything there. Confirmed live on Agni: HTTP
    /// Toolkit showed a sustained burst of "503 cap invocation rate exceeded" responses to the same
    /// <c>EnvironmentSettings</c> cap URI, one every ~<see cref="RepollMinInterval"/>, forever --
    /// the 60-second cooldown existed in source but could never fire.
    ///
    /// Doing the GET ourselves costs nothing extra (same <c>HttpCapsClient</c>, same one request)
    /// and gives <see cref="FetchRegionEnvironmentAsync"/> the status code it needs to make that
    /// cooldown reachable.</summary>
    private async Task<(OSDMap? Map, HttpStatusCode? Status)> GetCapabilityMapAsync(
        Uri capUri, CancellationToken cancellationToken)
    {
        var http = _client.HttpCapsClient;
        if (http == null) return (null, null);

        var (response, data) = await http.GetAsync(capUri, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return (null, response.StatusCode);
        if (data == null) return (null, response.StatusCode);

        return (OSDParser.Deserialize(data) as OSDMap, response.StatusCode);
    }

    /// <summary>Teleports to a global position by resolving the containing region by name (the
    /// form Picks / the map give us). Region-local coordinates are the global position modulo the
    /// 256 m region grid — correct for standard regions; a varregion pick could land off-centre.</summary>
    public void TeleportToGlobalPosition(string regionName, double globalX, double globalY, double globalZ)
    {
        if (string.IsNullOrEmpty(regionName) || !_client.Network.Connected) return;
        // See _teleportInProgress's doc comment. This path is fire-and-forget by design (no
        // caller currently awaits a result), so the guard here only prevents it from cross-wiring
        // with a concurrent TeleportToAsync/TeleportToLandmarkAsync call, not with itself.
        if (System.Threading.Interlocked.CompareExchange(ref _teleportInProgress, 1, 0) != 0) return;
        var local = new Vector3(
            (float)(globalX - Math.Floor(globalX / 256.0) * 256.0),
            (float)(globalY - Math.Floor(globalY / 256.0) * 256.0),
            (float)globalZ);
        _ = TeleportToGlobalPositionCoreAsync(regionName, local);
    }

    private async Task TeleportToGlobalPositionCoreAsync(string regionName, Vector3 local)
    {
        // FEAT-UI-18: this path is fire-and-forget (no caller awaits a TeleportResult), so the
        // overlay is driven entirely by these events -- relay LibreMetaverse's own progress plus
        // a synthetic start/terminal, exactly as the awaited overloads do.
        _client.Self.TeleportProgress += OnLmvTeleportProgress;
        RaiseTeleportStage(TeleportStage.Started, string.Empty);
        try
        {
            bool ok = await _client.Self.TeleportAsync(regionName, local).ConfigureAwait(false);
            RaiseTeleportStage(ok ? TeleportStage.Finished : TeleportStage.Failed,
                ok ? string.Empty
                   : (!string.IsNullOrWhiteSpace(_client.Self.TeleportMessage) ? _client.Self.TeleportMessage : "Teleport failed."));
        }
        catch (Exception ex)
        {
            RaiseTeleportStage(TeleportStage.Failed, ex.Message);
        }
        finally
        {
            _client.Self.TeleportProgress -= OnLmvTeleportProgress;
            System.Threading.Interlocked.Exchange(ref _teleportInProgress, 0);
        }
    }

    /// <summary>MVP2-3: the region's full avatar radar snapshot. LibreMetaverse hands us the
    /// complete current-position list every time (not new/removed-only deltas -- see
    /// <c>CoarseLocationUpdateEventArgs</c>), so this always replaces rather than merges. Z is
    /// pre-scaled by LibreMetaverse (packet Z is a byte, *4 to metres) -- passed through as-is.</summary>
    private void OnCoarseLocationUpdate(object? sender, CoarseLocationUpdateEventArgs e)
    {
        var avatars = new List<NearbyAvatar>(e.Positions.Count);
        foreach (var kv in e.Positions)
            avatars.Add(new NearbyAvatar(kv.Key.Guid, new System.Numerics.Vector3(kv.Value.X, kv.Value.Y, kv.Value.Z)));
        NearbyAvatarsUpdated?.Invoke(this, new NearbyAvatarsEvent(e.Simulator.Handle, avatars));
    }

    private static MapRegionInfo ToMapRegionInfo(GridRegion r) =>
        new(r.Name, r.X, r.Y, r.RegionHandle, r.MapImageID.Guid);

    private void OnGridRegion(object? sender, GridRegionEventArgs e) =>
        RegionDiscovered?.Invoke(this, ToMapRegionInfo(e.Region));

    /// <summary>MVP2-3: requests grid-map tiles for the region-grid rectangle
    /// [minGridX,minGridY]..[maxGridX,maxGridY] (each unit = 256 m -- see <see cref="MapRegionInfo"/>).
    /// Results stream back asynchronously, one <see cref="RegionDiscovered"/> per tile; there may be
    /// many and there is no single "done" signal, matching the underlying packet protocol.</summary>
    public void RequestMapBlocks(int minGridX, int minGridY, int maxGridX, int maxGridY)
    {
        if (!_client.Network.Connected) return;
        _client.Grid.RequestMapBlocks(GridLayerType.Objects,
            (ushort)Math.Max(0, minGridX), (ushort)Math.Max(0, minGridY),
            (ushort)Math.Max(0, maxGridX), (ushort)Math.Max(0, maxGridY), false);
    }

    /// <summary>MVP2-3 region search: resolves a region name to its map tile info (name lookup is
    /// case-insensitive, per <c>GridManager.GetGridRegionAsync</c>). Returns null if the region
    /// doesn't exist or the lookup times out.</summary>
    public async Task<MapRegionInfo?> ResolveRegionByNameAsync(string regionName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(regionName) || !_client.Network.Connected) return null;
        var r = await _client.Grid.GetGridRegionAsync(regionName, GridLayerType.Objects, ct).ConfigureAwait(false);
        return r.HasValue ? ToMapRegionInfo(r.Value) : null;
    }

    /// <summary>MVP2-3: resolves the region tile containing a given region handle -- used to show
    /// the name/coordinates of wherever the map was just clicked.</summary>
    public async Task<MapRegionInfo?> ResolveRegionByHandleAsync(ulong regionHandle, CancellationToken ct = default)
    {
        if (!_client.Network.Connected) return null;
        var r = await _client.Grid.GetGridRegionAsync(regionHandle, GridLayerType.Objects, ct).ConfigureAwait(false);
        return r.HasValue ? ToMapRegionInfo(r.Value) : null;
    }

    /// <summary>FEAT-UI-18: maps a LibreMetaverse <c>TeleportEventArgs</c> onto the neutral
    /// <see cref="TeleportProgressEvent"/> and raises <see cref="TeleportProgress"/>.
    /// <c>TeleportStatus.None</c> is dropped (not a real in-flight stage), same as the
    /// login-stage mapping in <see cref="LoginAsync"/>. Subscribed to <c>Self.TeleportProgress</c>
    /// by every teleport path here.</summary>
    private void OnLmvTeleportProgress(object? sender, TeleportEventArgs e)
    {
        TeleportStage? stage = e.Status switch
        {
            TeleportStatus.Start => TeleportStage.Started,
            TeleportStatus.Progress => TeleportStage.Progress,
            TeleportStatus.Failed => TeleportStage.Failed,
            TeleportStatus.Finished => TeleportStage.Finished,
            TeleportStatus.Cancelled => TeleportStage.Cancelled,
            _ => null,
        };
        if (stage.HasValue)
            TeleportProgress?.Invoke(this, new TeleportProgressEvent(stage.Value, e.Message ?? string.Empty));
    }

    private void RaiseTeleportStage(TeleportStage stage, string message) =>
        TeleportProgress?.Invoke(this, new TeleportProgressEvent(stage, message));

    /// <summary>Raises the terminal <see cref="TeleportProgress"/> event for a completed attempt
    /// and returns the result unchanged -- a timeout produces no LibreMetaverse event, so an
    /// overlay listening only to relayed events would never be told to hide. Wrapped around every
    /// <c>return</c> in the teleport methods.</summary>
    private TeleportResult FinishTeleport(TeleportResult result)
    {
        RaiseTeleportStage(result.Success ? TeleportStage.Finished : TeleportStage.Failed,
            result.Success ? string.Empty : result.Message);
        return result;
    }

    /// <summary>MVP2-3: teleports to a region-local position in a specific region by handle --
    /// the map window's double-click-to-teleport. Mirrors <see cref="TeleportToLandmarkAsync"/>'s
    /// progress-message plumbing and post-teleport position resync.</summary>
    public async Task<TeleportResult> TeleportToAsync(ulong regionHandle, System.Numerics.Vector3 localPosition, CancellationToken ct = default)
    {
        if (!_client.Network.Connected) return new TeleportResult(false, "Not connected.");
        // See _teleportInProgress's doc comment: LibreMetaverse's region-handle TeleportAsync
        // overload has no re-entrancy guard of its own, and two overlapping calls cross-wire.
        if (System.Threading.Interlocked.CompareExchange(ref _teleportInProgress, 1, 0) != 0)
            return new TeleportResult(false, "A teleport is already in progress.");

        string lastMessage = string.Empty;
        void OnProgress(object? sender, TeleportEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.Message)) lastMessage = e.Message;
            OnLmvTeleportProgress(sender, e); // FEAT-UI-18: relay to the neutral TeleportProgress event
        }

        _client.Self.TeleportProgress += OnProgress;
        RaiseTeleportStage(TeleportStage.Started, string.Empty);
        try
        {
            bool success = await _client.Self
                .TeleportAsync(regionHandle, new Vector3(localPosition.X, localPosition.Y, localPosition.Z), ct)
                .ConfigureAwait(false);

            if (success) SyncLocalAgentPositionAfterTeleport();

            // See TeleportToLandmarkAsync's identical comment: TeleportMessage is the authoritative
            // final reason on failure (notably a timeout, where no TeleportProgress event ever
            // fires to update lastMessage at all); lastMessage is only a fallback.
            string msg = !string.IsNullOrWhiteSpace(_client.Self.TeleportMessage) ? _client.Self.TeleportMessage : lastMessage;
            return FinishTeleport(new TeleportResult(success, success ? string.Empty : msg));
        }
        catch (Exception ex)
        {
            return FinishTeleport(new TeleportResult(false, ex.Message));
        }
        finally
        {
            _client.Self.TeleportProgress -= OnProgress;
            System.Threading.Interlocked.Exchange(ref _teleportInProgress, 0);
        }
    }

    private void OnSimDisconnected(object? sender, SimDisconnectedEventArgs e)
    {
        // BUG-NET-03: with neighbor circuits (MultipleSims) this now also fires for a neighbor
        // the grid told us to drop (DisableSimulator) as we moved away from a border -- the
        // consumer (WorldSimulation) unloads that region's entities/terrain via World.RemoveRegion.
        Console.WriteLine($"[Neighbor] disconnected {e.Simulator.Name} ({e.Simulator.Handle})");
        RegionDisconnectedReceived?.Invoke(this, new RegionDisconnectedEvent(e.Simulator.Handle));
    }

    /// <summary>
    /// Attempts to log in to the grid described by <paramref name="credentials"/>.
    /// Uses LibreMetaverse's async login API; failures (including unreachable grids)
    /// are returned as a failed <see cref="LoginResult"/> rather than thrown.
    /// </summary>
    public async Task<LoginResult> LoginAsync(LoginCredentials credentials, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        // Known before the handshake even starts -- see _isLindenGrid's doc comment. Set
        // regardless of outcome: a failed attempt still needs the dump gate armed for whatever
        // bake diagnostics run before the next successful login.
        _isLindenGrid = IsLindenLabUri(credentials.GridLoginUri);
        _lindenGridShortName = ParseLindenGridShortName(credentials.GridLoginUri);

        // Relays LibreMetaverse's own login handshake stages (ConnectingToLogin, ReadingResponse,
        // Redirecting, ConnectingToSim, Success/Failed) out through the neutral LoginProgress event
        // -- real server-driven progress, not a simulated/time-based fake (see FEAT-UI-08). Fires on
        // whatever thread LibreMetaverse raises it on; subscribed for the duration of this one call.
        void OnLmvLoginProgress(object? sender, LoginProgressEventArgs e)
        {
            var stage = e.Status switch
            {
                LoginStatus.ConnectingToLogin => LoginStage.ConnectingToLogin,
                LoginStatus.ReadingResponse => LoginStage.ReadingResponse,
                LoginStatus.Redirecting => LoginStage.Redirecting,
                LoginStatus.ConnectingToSim => LoginStage.ConnectingToSim,
                LoginStatus.Success => LoginStage.Success,
                LoginStatus.Failed => LoginStage.Failed,
                _ => (LoginStage?)null, // LoginStatus.None -- not a real in-flight stage, don't surface it
            };
            if (stage.HasValue)
                LoginProgress?.Invoke(this, new LoginProgressEvent(stage.Value, e.Message));
        }

        _client.Network.LoginProgress += OnLmvLoginProgress;
        try
        {
            var login = new LoginParams(
                _client,
                credentials.FirstName,
                credentials.LastName,
                credentials.Password,
                credentials.Channel,
                credentials.Version,
                credentials.GridLoginUri)
            {
                Start = credentials.StartLocation
            };

            var response = await _client.Network
                .LoginWithResponseAsync(login, ct)
                .ConfigureAwait(false);

            if (response is null)
            {
                return LoginResult.Fail("no-response", "Grid returned no login response.");
            }

            if (response.Success)
            {
                // BUG-AVATAR-04: start the appearance clock HERE, at login success.
                //
                // It used to start in ArmSelfAppearanceRestore, which runs from OnEventQueueRunning
                // — after the caps handshake. On a healthy login the self AvatarAppearance relay
                // arrives before that, so the clock was not running yet and
                // `relay arrived N s after login` never printed: the measurement missed exactly the
                // logins it exists to measure (zero hits across every session, found 2026-09-09).
                // The number it produces is what turns EarlyRestoreDelay from an estimate into a
                // measurement.
                _selfAppearanceClock.Restart();
                System.Threading.Volatile.Write(ref _selfRelayLatencyLogged, 0);

                // FEAT-SL-02: the login response is the ONE place AccountMaturityMax and the
                // initial PreferredMaturity are ever populated -- without this, both stayed
                // permanently at their General/false-support defaults for the whole session,
                // regardless of what the account or grid actually allow (found chasing an unused-
                // event compiler warning; see MaturityPreferenceChanged below and
                // SupportsMaturityPreference's doc comment for the other two pieces of this same
                // gap). `agent_access_max` is the account's verified ceiling; `agent_region_access`
                // is the currently active pick -- both 2-letter codes ("PG"/"M"/"A"), OpenSim sends
                // empty strings for both, which MaturityAccess.FromShortString already treats as
                // General.
                AccountMaturityMax = MaturityAccess.FromShortString(response.AgentAccessMax);
                PreferredMaturity = MaturityAccess.FromShortString(response.AgentRegionAccess);

                // FEAT-AVATAR-01: ask the simulator for the worn wearable set. LibreMetaverse would
                // do this itself at login, but only under Settings.Agent.SendAppearance, which is
                // off -- so without this AppearanceManager.Wearables stays empty for the whole
                // session. Two things depend on it: the Worn tab ("Angezogen") marks every
                // Clothing/Bodypart layer "(nicht aktiv)" because GetWornItems can only see the COF
                // link, and a wearable edit has no worn set to build an appearance from.
                // Fire-and-forget: nothing in the login path should wait on it.
                _ = RequestWornWearablesViaLludpAsync(CancellationToken.None);
            }

            return response.Success
                ? LoginResult.Ok(response.AgentID.ToString(), response.SessionID.ToString(), response.Message)
                : LoginResult.Fail(response.Reason, response.Message);
        }
        catch (Exception ex)
        {
            // Surface the failure through the result; do not swallow it silently.
            return LoginResult.Fail("exception", ex.Message);
        }
        finally
        {
            _client.Network.LoginProgress -= OnLmvLoginProgress;
        }
    }

    /// <summary>Logs out if connected. Safe to call when already disconnected.</summary>
    public void Logout()
    {
        _isTyping = false;
        if (_client.Network.Connected)
        {
            _client.Network.Logout();
        }
    }

    /// <summary>Teleports to the region/position encoded in a landmark asset. LibreMetaverse
    /// resolves the landmark server-side from its asset UUID. If direct landmark teleport fails
    /// (e.g. server-side asset indexing delay on newly created landmarks), fetches the landmark
    /// asset bytes, decodes its region UUID and position, and teleports via region handle.</summary>
    public async Task<TeleportResult> TeleportToLandmarkAsync(Guid landmarkAssetId, CancellationToken ct = default)
    {
        if (!_client.Network.Connected)
            return new TeleportResult(false, "Not connected.");
        if (System.Threading.Interlocked.CompareExchange(ref _teleportInProgress, 1, 0) != 0)
            return new TeleportResult(false, "A teleport is already in progress.");

        string lastMessage = string.Empty;
        void OnProgress(object? sender, TeleportEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.Message))
                lastMessage = e.Message;
            OnLmvTeleportProgress(sender, e); // FEAT-UI-18: relay to the neutral TeleportProgress event
        }

        _client.Self.TeleportProgress += OnProgress;
        RaiseTeleportStage(TeleportStage.Started, string.Empty);
        try
        {
            // Attempt 1: Direct landmark teleport request
            bool success = await _client.Self
                .TeleportAsync(new UUID(landmarkAssetId), ct)
                .ConfigureAwait(false);

            // On failure, LibreMetaverse's own TeleportMessage is the authoritative FINAL reason
            // (e.g. "Teleport timed out." after WaitForTeleportAsync gives up) -- lastMessage is
            // only ever a transient progress narration and can be stale by the time we get here
            // (no TeleportProgress event fires for a timeout at all). Preferring lastMessage first
            // is backwards and was live-tested producing "Teleport started" as the shown failure
            // reason for what was actually a 40s timeout.
            string msg = !string.IsNullOrWhiteSpace(_client.Self.TeleportMessage) ? _client.Self.TeleportMessage : lastMessage;
            if (success)
            {
                SyncLocalAgentPositionAfterTeleport();
                return FinishTeleport(new TeleportResult(true, msg));
            }

            // Attempt 2: Fallback — fetch landmark asset, parse region ID & position, teleport by region handle
            var asset = await _client.Assets
                .RequestAssetAsync(new UUID(landmarkAssetId), AssetType.Landmark, priority: true, ct)
                .ConfigureAwait(false);

            if (asset is LibreMetaverse.Assets.AssetLandmark landmark)
            {
                landmark.Decode();
                if (landmark.RegionID != UUID.Zero)
                {
                    ulong? handle = await ResolveRegionHandleAsync(landmark.RegionID, ct).ConfigureAwait(false);
                    if (handle.HasValue)
                    {
                        bool fallbackSuccess = await _client.Self
                            .TeleportAsync(handle.Value, landmark.Position, ct)
                            .ConfigureAwait(false);

                        if (fallbackSuccess)
                        {
                            SyncLocalAgentPositionAfterTeleport();
                        }

                        string fallbackMsg = !string.IsNullOrWhiteSpace(_client.Self.TeleportMessage) ? _client.Self.TeleportMessage : lastMessage;
                        return FinishTeleport(new TeleportResult(fallbackSuccess, fallbackSuccess ? string.Empty : fallbackMsg));
                    }
                }
            }

            return FinishTeleport(new TeleportResult(false, msg));
        }
        catch (Exception ex)
        {
            return FinishTeleport(new TeleportResult(false, ex.Message));
        }
        finally
        {
            _client.Self.TeleportProgress -= OnProgress;
            System.Threading.Interlocked.Exchange(ref _teleportInProgress, 0);
        }
    }

    /// <summary>BUG-NET-17: reported live 2026-09-17 on Agni -- a SAME-region ("local") teleport
    /// moved the avatar's X/Y correctly but rendered her stuck at the OLD height, because
    /// WorldSimulation deliberately holds the local agent's Z at its own ground-clamped value
    /// during ordinary movement (to stop the network echo and the local ground-clamp fighting over
    /// Z every frame -- see ApplyAvatarUpdate's doc comment) and never overwrites a known
    /// SupportPlane with an absent one (a same-region teleport keeps the SAME entity, unlike a
    /// cross-region one, so that plane -- a REAL surface reading for wherever the avatar used to
    /// be -- survived and kept steering the ground clamp toward the old height). <c>IsTeleport:
    /// true</c> tells ApplyAvatarUpdate to take this event's Position verbatim, snap to it
    /// instantly, and clear the stale plane instead.</summary>
    private void SyncLocalAgentPositionAfterTeleport()
    {
        var sim = _client.Network.CurrentSim;
        if (sim == null) return;

        var pos = _client.Self.SimPosition;
        var rot = _client.Self.SimRotation;

        AvatarUpdateReceived?.Invoke(this, new AvatarUpdateEvent(
            sim.Handle,
            _client.Self.LocalID,
            _client.Self.AgentID.Guid,
            new System.Numerics.Vector3(pos.X, pos.Y, pos.Z),
            new System.Numerics.Quaternion(rot.X, rot.Y, rot.Z, rot.W),
            _client.Self.FirstName,
            _client.Self.LastName,
            IsLocalAgent: true,
            SittingOnLocalId: _client.Self.SittingOn,
            IsTeleport: true));
    }

    private async Task<ulong?> ResolveRegionHandleAsync(UUID regionId, CancellationToken ct)
    {
        if (_client.Network.CurrentSim != null && _client.Network.CurrentSim.ID == regionId)
        {
            return _client.Network.CurrentSim.Handle;
        }

        if (_client.Grid.RegionsByUUIDReadOnly.TryGetValue(regionId, out var cachedHandle))
        {
            return cachedHandle;
        }

        var tcs = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnRegionHandleReply(object? sender, RegionHandleReplyEventArgs e)
        {
            if (e.RegionID == regionId)
            {
                tcs.TrySetResult(e.RegionHandle);
            }
        }

        _client.Grid.RegionHandleReply += OnRegionHandleReply;
        try
        {
            _client.Grid.RequestRegionHandle(regionId);
            using var regCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            regCts.CancelAfter(TimeSpan.FromSeconds(5));
            using var reg = regCts.Token.Register(() => tcs.TrySetCanceled());

            return await tcs.Task.ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
        finally
        {
            _client.Grid.RegionHandleReply -= OnRegionHandleReply;
        }
    }

    // Restored Uncommitted Methods
    /// <summary>The account's verified maturity ceiling (<c>agent_access_max</c> from the login
    /// response) -- the highest <see cref="PreferredMaturity"/> can ever be set to, regardless of
    /// what the UI offers. Stays General until a successful login populates it (see
    /// <see cref="LoginAsync"/>).</summary>
    public SLNG.Core.MaturityLevel AccountMaturityMax { get; private set; } = SLNG.Core.MaturityLevel.General;

    /// <summary>The account's currently active maturity pick. Seeded from the login response's
    /// <c>agent_region_access</c>, then kept current by <see cref="SetPreferredMaturityAsync"/>
    /// echoing back whatever the <c>UpdateAgentInformation</c> capability actually accepted (which
    /// may be clamped below the requested level).</summary>
    public SLNG.Core.MaturityLevel PreferredMaturity { get; private set; } = SLNG.Core.MaturityLevel.General;

    /// <summary>Raised after a successful <see cref="SetPreferredMaturityAsync"/> call, once
    /// <see cref="PreferredMaturity"/> has already been updated to the server's actual (possibly
    /// clamped) answer. BUG-NET-10: this was declared and subscribed to
    /// (<c>MaturityPreferencesPage</c>) but never invoked -- caught by the compiler's
    /// "event is never used" (CS0067) warning, which was the only surviving trace of the gap.
    /// <see cref="SupportsMaturityPreference"/> and the login-response population above it were the
    /// other two pieces missing from the same spot; all three look like the same casualty this
    /// session already found and re-applied elsewhere in this file (see the process-failure note in
    /// <c>HANDOVER.md</c>) -- just not caught until this warning pointed at it.</summary>
    public event EventHandler<SLNG.Core.MaturityLevel>? MaturityPreferenceChanged;

    /// <summary>Whether the CURRENT region offers the <c>UpdateAgentInformation</c> capability.
    /// Computed live from the capability list, not cached: a stored flag here would go stale on
    /// every region crossing (a capability present on one region is not guaranteed on the next),
    /// and FEAT-SL-02's own spec already documents this as a live capability read, not a value set
    /// once at login. BUG-NET-10: this had been turned into a plain <c>{ get; private set; } =
    /// false</c> auto-property with nothing ever assigning it -- permanently false regardless of
    /// grid, contradicting the spec it was written against.</summary>
    public bool SupportsMaturityPreference =>
        _client.Network.CurrentSim?.Caps?.CapabilityURI("UpdateAgentInformation") != null;

    public async Task<(bool success, SLNG.Core.MaturityLevel actual, string error)> SetPreferredMaturityAsync(SLNG.Core.MaturityLevel level)
    {
        if (!IsConnected) return (false, SLNG.Core.MaturityLevel.General, "Not connected");
        var uri = _client.Network.CurrentSim?.Caps?.CapabilityURI("UpdateAgentInformation");
        if (uri == null) return (false, SLNG.Core.MaturityLevel.General, "Capability not available");

        try
        {
            var req = new LibreMetaverse.StructuredData.OSDMap();
            req["access_prefs"] = new LibreMetaverse.StructuredData.OSDMap
            {
                ["max"] = SLNG.Net.MaturityAccess.ToShortString(level)
            };
            var (res, data) = await _client.HttpCapsClient.PostAsync(uri, LibreMetaverse.StructuredData.OSDFormat.Xml, req, System.Threading.CancellationToken.None);
            if (res.IsSuccessStatusCode && data != null)
            {
                var body = LibreMetaverse.StructuredData.OSDParser.Deserialize(data) as LibreMetaverse.StructuredData.OSDMap;
                if (body != null && body.ContainsKey("access_prefs"))
                {
                    var prefs = body["access_prefs"] as LibreMetaverse.StructuredData.OSDMap;
                    if (prefs != null && prefs.ContainsKey("max"))
                    {
                        PreferredMaturity = SLNG.Net.MaturityAccess.FromShortString(prefs["max"].AsString());
                        MaturityPreferenceChanged?.Invoke(this, PreferredMaturity);
                        return (true, PreferredMaturity, "");
                    }
                }
            }
            return (false, PreferredMaturity, $"HTTP {(int)res.StatusCode}");
        }
        catch (Exception ex)
        {
            return (false, PreferredMaturity, ex.Message);
        }
    }

    public static (long dx, long dy) RegionGridOffset(ulong target, ulong from)
    {
        uint tx = (uint)(target >> 32);
        uint ty = (uint)target;
        uint fx = (uint)(from >> 32);
        uint fy = (uint)from;

        long dx = ((long)tx - (long)fx) / 256;
        long dy = ((long)ty - (long)fy) / 256;
        return (dx, dy);
    }

    public static LibreMetaverse.LoginParams BuildLoginParams(LibreMetaverse.GridClient client, LoginCredentials creds)
    {
        var login = client.Network.DefaultLoginParams(
            creds.FirstName, creds.LastName, creds.Password, creds.Channel, creds.Version);
        login.URI = creds.GridLoginUri?.ToString() ?? "";
        login.AgreeToTos = creds.AgreeToTos;
        login.ReadCritical = creds.ReadCritical;
        login.Start = string.IsNullOrEmpty(creds.StartLocation) ? "last" : creds.StartLocation;
        return login;
    }

    public static bool IsLindenLabUri(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return false;
        try
        {
            var u = new Uri(uri);
            return u.Host.EndsWith("lindenlab.com") || u.Host.EndsWith("secondlife.com");
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Parses "agni" or "aditi" out of a Linden login URI
    /// (<c>login.agni.lindenlab.com</c> / <c>login.aditi.lindenlab.com</c>) -- the grid short name
    /// baked into the <c>bake-texture.glb.{grid}.lindenlab.com</c> host
    /// <see cref="FetchBakeTextureDataAsync"/> builds. Null for anything that doesn't match this
    /// exact two-label pattern (OpenSim, or a Linden host shaped some other way).</summary>
    internal static string? ParseLindenGridShortName(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return null;
        try
        {
            var host = new Uri(uri).Host;
            var labels = host.Split('.');
            // "login" . "<grid>" . "lindenlab" . "com" -- exactly four labels, the second one is
            // the grid name. Anything else (a bare "lindenlab.com", a different subdomain shape)
            // is deliberately NOT guessed at.
            if (labels.Length == 4 && labels[0] == "login"
                && string.Equals(labels[2], "lindenlab", StringComparison.OrdinalIgnoreCase)
                && string.Equals(labels[3], "com", StringComparison.OrdinalIgnoreCase))
            {
                return labels[1].ToLowerInvariant();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }
}
