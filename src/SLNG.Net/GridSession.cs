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

/// <summary>
/// A connection to a single Second Life / OpenSim grid. Wraps the LibreMetaverse
/// <see cref="GridClient"/> and exposes an engine-agnostic login API. This is the
/// seam between the protocol stack and the rest of SLNG: nothing above this layer
/// sees a LibreMetaverse type.
/// </summary>
public sealed partial class GridSession : IDisposable, IWorldEventSource
{
    /// <summary>How long to wait for a ParcelProperties reply before giving up and using the
    /// region's environment. Short on purpose: this sits in the login path, and the region scope is
    /// a correct fallback, so a slow sim must not hold up the sky.</summary>
    private static readonly TimeSpan ParcelLookupTimeout = TimeSpan.FromSeconds(4);

    private readonly GridClient _client;

    /// <summary>Set from <see cref="LoginCredentials.GridLoginUri"/> at the start of every login
    /// attempt (success or failure -- the URI is known before the handshake even starts). TPV
    /// Policy §2.b: an SL grid must not receive an export of other creators' decoded wearable
    /// textures, which is exactly what <see cref="DumpPreview"/>'s bake-input dumps are. Gates that
    /// dump; also gates the SSB refusals on <see cref="BakeAvatarAsync"/>/
    /// <see cref="CreateTestSkinAsync"/>/<see cref="RebakeAvatar"/>. Harmless anywhere else, since
    /// OpenSim has no such restriction.
    ///
    /// RESTORED 2026-09-02: this field, its assignment below, the DumpPreview gate, both SSB
    /// guards, RebakeAvatar's SSB branch, and OnSimChanged/BUG-NET-04's teleport-cleanup wiring
    /// were all silently lost from this file between being tested (confirmed working via
    /// godot.log evidence, ~13:48) and a later commit (~15:15) made from a partial/stale copy of
    /// this session's changes. Re-applied from the original reasoning after the loss was found by
    /// grepping for a comment string that no longer existed anywhere in the file.</summary>
    private bool _isLindenGrid;

    /// <summary>The Linden grid's short name ("agni", "aditi"), parsed from
    /// <see cref="LoginCredentials.GridLoginUri"/> at the same point <see cref="_isLindenGrid"/> is
    /// set. Null off a Linden grid. BUG-AVATAR-02: this is the piece
    /// <see cref="FetchBakeTextureDataAsync"/> needs to build the bake-texture CDN host
    /// (<c>bake-texture.glb.{grid}.lindenlab.com</c>) -- both known login hosts
    /// (<c>login.agni.lindenlab.com</c> / <c>login.aditi.lindenlab.com</c>) carry the grid name as
    /// the URI's second label, which is what this parses.</summary>
    private string? _lindenGridShortName;

    /// <summary>Correlates a ParcelProperties reply with our own request. The simulator also pushes
    /// ParcelProperties unprompted on a parcel crossing, so matching on the sequence id is what
    /// keeps an unrelated push from being read as our answer.</summary>
    private int _parcelSequenceId;

    private volatile string? _currentParcelName;

    /// <summary>Guards <see cref="TeleportToAsync"/>/<see cref="TeleportToLandmarkAsync"/>/
    /// <see cref="TeleportToGlobalPosition"/> against running concurrently. LibreMetaverse's
    /// <c>AgentManager</c> tracks "the" in-flight teleport in a SINGLE shared field
    /// (<c>_teleportTcs</c>), used by every <c>TeleportAsync</c> overload -- and only the
    /// landmark-UUID overload checks <c>teleportStatus == TeleportStatus.Progress</c> before
    /// starting a new one; the region-handle overload this class's map/landmark-fallback paths
    /// use has NO such guard. Two overlapping calls therefore silently cross-wire: a response
    /// meant for the first attempt can complete the SECOND one's task instead (or vice versa).
    /// Live-tested 2026-08-28 -- a user re-clicking "Teleport" before the first attempt resolved
    /// (a natural reaction when nothing appears to happen for a few seconds) produced exactly
    /// this: results reporting <c>success=false</c> with <c>message="Teleport finished"</c>, the
    /// literal string LibreMetaverse writes ONLY on a genuine success. Rejecting a second attempt
    /// outright -- rather than letting both silently corrupt each other -- is the only fix
    /// available from this side of a vendored NuGet package. 0/1 via Interlocked, not a bool:
    /// multiple call sites, no single lock object to pair with a plain flag.</summary>
    private int _teleportInProgress;

    // Shared cache for both avatar (Creator/Owner/...) and group names -- both are keyed by
    // UUID and populated via the same resolve-and-notify flow, so one cache/event pair covers
    // both instead of duplicating the plumbing per name kind.
    private readonly ConcurrentDictionary<Guid, string> _nameCache = new();

    // The three ExtraParams latches (BUG-NET-25, FEAT-ANIMESH-01). Each exists because
    // LibreMetaverse cannot say what an object's CURRENT ExtraParams contain, and OpenSim signals
    // "switched off" by OMITTING a block rather than sending an off marker, so the only way to
    // know a block is gone is to read the bytes of the update that left it out. Fed by the raw
    // packet callbacks (OnRawObjectUpdatePacket for ObjectUpdate, OnObjectUpdateCompressedRaw for
    // ObjectUpdateCompressed), registered alongside ObjectManager's own handlers, since the
    // high-level Primitive/PrimEventArgs API exposes no such signal. See ExtraParamsScan.
    //
    // All three are keyed by region AS WELL AS LocalID -- LocalIDs are handed out per region and a
    // neighbour (MultipleSims) routinely reuses the same number --, hold only the objects that
    // matter (never one entry per prim in the region), and are pruned on kill and on region
    // disconnect, so no entry outlives its object.

    // Objects that once showed a Light (0x20) block: true while the latest ExtraParams still carry
    // it, false once they no longer do. Primitive.Light is only ever WRITTEN, inside
    // SetExtraParamsFromBytes' Light case, so after the block disappears it reports the last-enabled
    // state forever -- even across a relog. "False" is the one fact the library cannot give us;
    // "no entry" means trust the Primitive. See LatchLight / EffectiveLight.
    private readonly ConcurrentDictionary<(ulong Region, uint LocalId), bool> _lightObjects = new();

    // The Reflection Probe (0x90) block of the latest ExtraParams, for the objects that have one.
    // LibreMetaverse does not parse this block at all: ExtraParamType.ReflectionProbe = 0x90 is in
    // its enum, but SetExtraParamsFromBytes has no branch for it and steps over the payload, so the
    // high-level API cannot express "this object is a mirror". See ReflectionProbeParams.
    private readonly ConcurrentDictionary<(ulong Region, uint LocalId), SLNG.Core.ReflectionProbeParams> _reflectionProbes = new();

    // The objects whose latest ExtraParams carry an Extended Mesh (0x70) block with the animated-mesh
    // bit set (FEAT-ANIMESH-01). LibreMetaverse 3.1.6 does decode it (Primitive.ExtendedMeshFlags),
    // but into a field that is never cleared when the block goes away, so the same latch applies.
    private readonly ConcurrentDictionary<(ulong Region, uint LocalId), bool> _animatedMeshObjects = new();

    /// <summary>MVP3-3 Phase 1: the last <c>x-mv:</c> media-version string a fetch was already
    /// queued for, per LocalID. LibreMetaverse raises no event when a prim's MOAP media changes
    /// (verified: no <c>ObjectMedia</c> event is ever raised in the pinned 3.1.3), so this is the
    /// gate that replicates LLVOVolume::processUpdateMessage's staleness check
    /// (llvovolume.cpp:2640-2660) and keeps a region full of untouched media prims from re-fetching
    /// every time they simply re-enter the interest list -- the same caps-flood lesson as
    /// BUG-NET-11.</summary>
    private readonly ConcurrentDictionary<uint, string> _lastMediaVersionByLocalId = new();

    /// <summary>Bounds concurrent <c>ObjectMedia</c> GETs the same way <c>_textureFetchSemaphore</c>
    /// bounds texture fetches -- lower than that one's 8 because MOAP prims are far rarer than
    /// textured faces, so a whole region's worth arriving at once is a much smaller burst.</summary>
    private static readonly System.Threading.SemaphoreSlim _mediaFetchSemaphore = new(4, 4);

    /// <summary>The region's current sun direction, in SL coordinates, pointing FROM the region
    /// TOWARD the sun. Zero until the first SimulatorViewerTimeMessage arrives.
    ///
    /// The sim has been sending this all along (LibreMetaverse decodes it into
    /// <c>GridManager.SunDirection</c>); nothing here ever read it, so the renderer lit every
    /// region with one hardcoded angle regardless of the region's actual time of day. On a
    /// rotationally symmetric object such as a column that puts the highlight on a different side
    /// than the real viewer, which is indistinguishable from a mirrored texture by eye.
    ///
    /// Snapshotted into a local before being returned: LibreMetaverse writes this from a network
    /// thread, and a Vector3 assignment is not atomic, so a caller reading the property directly
    /// could observe a half-updated vector. The sun moves slowly enough that a one-frame-stale
    /// value is irrelevant; a torn one is not.</summary>
    public System.Numerics.Vector3 SunDirection
    {
        get
        {
            var d = _client.Grid.SunDirection;
            return new System.Numerics.Vector3(d.X, d.Y, d.Z);
        }
    }

    public event EventHandler<ChatMessageEvent>? ChatMessageReceived;

    public event EventHandler<ObjectUpdateEvent>? ObjectUpdateReceived;

    public event EventHandler<AvatarUpdateEvent>? AvatarUpdateReceived;

    public event EventHandler<ObjectRemovedEvent>? ObjectRemovedReceived;

    /// <summary>The region's current simulated UNIX time (server time), extracted from SimulatorViewerTimeMessage.
    /// Used to synchronize the EEP day cycle perfectly with the region.</summary>
    public ulong SimUnixTime { get; private set; }

    /// <summary>The region's current sun phase [0.0 - 2.0 * PI], extracted from SimulatorViewerTimeMessage.</summary>
    public float SunPhase { get; private set; }
    public event EventHandler<ObjectPropertiesEvent>? ObjectPropertiesReceived;

    public event EventHandler<PhysicsPropertiesEvent>? PhysicsPropertiesReceived;

    public event EventHandler<ObjectMediaEvent>? ObjectMediaReceived;

    /// <summary>FEAT-ANIMESH-02: the animations an animated-mesh prim has been told to play. Raised
    /// on a LibreMetaverse worker thread, like every other event here.</summary>
    public event EventHandler<ObjectAnimationEvent>? ObjectAnimationReceived;

    public event EventHandler<NameResolvedEvent>? NameResolved;

    public event EventHandler<NameResolvedEvent>? DisplayNameResolved;

    /// <summary>A resident's Display Name changed and the grid told us. Raised in addition to
    /// <see cref="DisplayNameResolved"/> (which is what repaints the nametag): this one carries the
    /// OLD name too, so the user can be told "X is now known as Y" the way the reference viewer
    /// does. Fires on a network thread.</summary>
    public event EventHandler<DisplayNameChangedEvent>? DisplayNameChanged;

    public event EventHandler<AlertMessageEvent>? AlertMessageReceived;

    /// <summary>FEAT-UI-34: the region the agent stands in announced a restart. Raised in
    /// addition to <see cref="AlertMessageReceived"/>, not instead of it: the alert text is still
    /// the record, this is the deadline. Fires on a network thread; a repeat notice for the same
    /// restart raises it again with the time that is left.</summary>
    public event EventHandler<RegionRestartEvent>? RegionRestartReceived;

    /// <summary>A region's event queue has stopped delivering and is not coming back by itself
    /// (BUG-NET-20). Raised once per region, then at most every ten minutes while it lasts.</summary>
    /// <remarks>
    /// Worth surfacing to the user rather than only logging, because the consequences are all
    /// invisible: that region stops delivering group chat invitations, teleport progress,
    /// ObjectMedia, and parcel/environment pushes. Everything simply stops working quietly, which
    /// is indistinguishable from "nobody is talking" and "this object has no media".
    ///
    /// <para>Raised on a LibreMetaverse logging thread, like every other event here — the handler
    /// marshals.</para>
    /// </remarks>
    public event EventHandler<EventQueueStalledEvent>? EventQueueStalled;

    /// <summary>The session is over; the client must return to the login screen. BUG-NET-22.</summary>
    /// <remarks>
    /// Raised at most once per session. Every path that ends a session converges here, including
    /// the one BUG-NET-20 detects, so the client has one thing to handle rather than a list.
    /// </remarks>
    public event EventHandler<SessionEndedEvent>? SessionEnded;

    private int _sessionEndRaised;

    /// <summary>Raises <see cref="SessionEnded"/> exactly once.</summary>
    /// <remarks>
    /// Once, because the paths overlap by design: ending the session ourselves over a dead event
    /// queue makes LibreMetaverse raise its own <c>Disconnected</c> a moment later, and the user
    /// must not be told twice — nor be sent back to the login screen while already on the way
    /// there.
    /// </remarks>
    private void RaiseSessionEnded(SessionEndReason reason, string message)
    {
        if (System.Threading.Interlocked.Exchange(ref _sessionEndRaised, 1) != 0) return;

        // BUG-INV-13: keep what this session learned. The app saves both caches on its own quit and
        // logout, but only while still connected -- and a session the grid ends never is by the
        // time anybody clicks anything. The store is still whole here: LibreMetaverse drops it only
        // when its InventoryManager is disposed, not on a disconnect. Before the event, because the
        // app tears the session down in reaction to it. A local write with no network wait, so it
        // is fine on whichever library thread noticed the end.
        SaveInventoryCache();
        SaveDisplayNameCache();

        SessionEnded?.Invoke(this, new SessionEndedEvent(reason, message));
    }

    private void OnNetworkDisconnected(object? sender, DisconnectedEventArgs e)
    {
        // Our own logout is not news: whoever called it is already showing the login screen.
        if (e.Reason == NetworkManager.DisconnectType.ClientInitiated) return;

        var reason = e.Reason switch
        {
            NetworkManager.DisconnectType.SimShutdown => SessionEndReason.SimShutdown,
            NetworkManager.DisconnectType.NetworkTimeout => SessionEndReason.NetworkTimeout,
            _ => SessionEndReason.ServerInitiated,
        };
        Console.Error.WriteLine($"[Net] session ended ({e.Reason}): {e.Message}");
        RaiseSessionEnded(reason, e.Message ?? string.Empty);
    }

    private readonly SLNG.Core.EventQueueHealth _eventQueueHealth = new();

    /// <summary>Sees every LibreMetaverse log line before it is printed. Returns false to swallow
    /// it.</summary>
    /// <remarks>
    /// Two jobs, and the second is why this is a filter rather than a passive listener. It spots a
    /// stalled event queue (BUG-NET-20) — the one condition the library reports nowhere else — and
    /// it stops the resulting flood: the report that started this had <b>65 identical lines with
    /// nothing between them</b>, which hid every other diagnostic in the session. Losing a
    /// diagnostic to a noisy log is the same harm as not writing it.
    ///
    /// <para>The FIRST failure for a region is still printed verbatim. If this detection is ever
    /// wrong, the raw evidence is in the log exactly once, which is enough to notice and not
    /// enough to drown anything.</para>
    ///
    /// <para>Called on a LibreMetaverse network thread. <see cref="SLNG.Core.EventQueueHealth"/>
    /// is not thread-safe, hence the lock; it is contended about once per second at worst.</para>
    /// </remarks>
    /// <summary>Logs out because the region we are in has stopped answering. BUG-NET-22.</summary>
    /// <remarks>
    /// A real logout, not a dropped socket: the grid is told, so the avatar does not linger for
    /// everyone else until the session times out. Runs off the log callback's thread because
    /// <c>Logout</c> blocks on the server's acknowledgement, and that callback is on
    /// LibreMetaverse's own path -- blocking it would stall the library that has to deliver the
    /// acknowledgement.
    /// </remarks>
    private void EndSessionOverDeadEventQueue(string regionName)
    {
        // Raised BEFORE the logout, so the client starts leaving immediately. The logout is
        // courtesy to the grid; the user should not wait on it.
        RaiseSessionEnded(SessionEndReason.EventQueueDead, regionName);

        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                if (_client.Network.Connected) _client.Network.Logout();
            }
            catch (Exception ex)
            {
                // The session is over either way -- this is only about telling the grid.
                Console.Error.WriteLine($"[Net] logout after a dead event queue failed: {ex.Message}");
            }
        });
    }

    private bool FilterLibreMetaverseLog(Microsoft.Extensions.Logging.LogLevel level, string message)
    {
        if (!SLNG.Core.EventQueueHealth.TryReadEventQueueFailure(message, out var simulatorText))
            return true;

        string regionName = SLNG.Core.EventQueueHealth.RegionNameFromSimulatorText(simulatorText);
        if (string.IsNullOrEmpty(simulatorText)) return true; // unrecognisable: keep the line

        bool announce;
        int count;
        bool first;
        lock (_eventQueueHealth)
        {
            first = _eventQueueHealth.FailureCount(simulatorText) == 0;
            announce = _eventQueueHealth.ReportFailure(simulatorText, DateTime.UtcNow);
            count = _eventQueueHealth.FailureCount(simulatorText);
        }

        if (announce)
        {
            Console.Error.WriteLine(
                $"[EventQueue] {regionName}: the region's event queue has failed {count} times in a row and is " +
                "not recovering -- its capability is gone (a region restart does this). Group chat invitations, " +
                "teleport progress, object media and parcel/environment changes will not arrive from this region " +
                "until you reconnect to it. Further identical warnings are suppressed.");

            EventQueueStalled?.Invoke(this, new SLNG.Core.EventQueueStalledEvent(regionName, count));

            // BUG-NET-22: if it is the region we are STANDING IN, the session is over -- log out
            // and say so, rather than leave the user in a world that no longer answers.
            //
            // This is the reference viewer's own conclusion, in its own words
            // (lleventpoll.cpp:273-287): "At this point we have given up and the viewer will not
            // receive HTTP messages from the simulator. IMs, teleports, about land, selecting
            // land, region crossing and more will all fail. They are essentially disconnected
            // from the region even though some things may still work. Since things won't get
            // better until they relog we force a disconnect now." -- and forceDisconnect
            // (llappviewer.cpp:5018) sends a real logout first, it does not just drop the socket.
            //
            // The main/child distinction is the viewer's too, and it is the load-bearing half:
            // a NEIGHBOUR's dead queue stops that queue and nothing else, because you are not in
            // it. Ours compares region NAMES where the viewer compares host addresses -- we only
            // have the name, because the only place this failure surfaces at all is a log line.
            if (!string.IsNullOrEmpty(regionName)
                && string.Equals(regionName, CurrentRegionName, StringComparison.OrdinalIgnoreCase))
            {
                EndSessionOverDeadEventQueue(regionName);
            }
        }

        return first;
    }

    public event EventHandler<TerrainPatchEvent>? TerrainPatchReceived;

    public event EventHandler<TerrainSettingsEvent>? TerrainSettingsReceived;

    public event EventHandler<RegionDisconnectedEvent>? RegionDisconnectedReceived;

    public event EventHandler<AvatarAppearanceEvent>? AvatarAppearanceReceived;

    public event EventHandler<AvatarAnimationEvent>? AvatarAnimationReceived;

    /// <summary>FEAT-UI-16: the set of items the local avatar is wearing may have changed (a bake
    /// completed, an appearance relay arrived). Raised on a LibreMetaverse network thread — marshal
    /// before touching a scene node. The inventory "Worn" tab also polls while visible, so a missed
    /// raise (e.g. an attachment attach/detach, which has no clean LMV event) self-heals.</summary>
    public event EventHandler? WornItemsChanged;

    public event EventHandler<FriendStatusEvent>? FriendStatusChanged;

    public event EventHandler<InstantMessageEvent>? InstantMessageReceived;

    public event EventHandler<ScriptDialogEvent>? ScriptDialogReceived;

    /// <summary>An in-world script is asking for permission over the agent (llRequestPermissions).
    /// Answer with <see cref="RespondToScriptPermissionRequest"/>. Raised on a LibreMetaverse
    /// network thread — marshal before touching a scene node.</summary>
    public event EventHandler<ScriptPermissionRequestEvent>? ScriptPermissionRequested;

    /// <summary>M5-3 Phase 2: the agent's group memberships, after <see cref="RequestGroups"/>.
    /// Raised on a LibreMetaverse network thread — marshal before touching a scene node.</summary>
    public event EventHandler<GroupsUpdatedEvent>? GroupsUpdated;

    /// <inheritdoc cref="GroupsUpdated"/>
    public event EventHandler<GroupChatMessageEvent>? GroupChatMessageReceived;

    /// <inheritdoc cref="GroupsUpdated"/>
    public event EventHandler<GroupChatJoinedEvent>? GroupChatJoined;

    /// <inheritdoc cref="GroupsUpdated"/>
    public event EventHandler<GroupInvitationEvent>? GroupInvitationReceived;

    /// <summary>BUG-INV-04: another avatar or an in-world object offered the agent an inventory
    /// item. Answer with <see cref="RespondToInventoryOffer"/>. Raised on a LibreMetaverse network
    /// thread — marshal before touching a scene node.</summary>
    public event EventHandler<InventoryOfferEvent>? InventoryOfferReceived;

    /// <summary>BUG-NET-27: somebody offered the agent a teleport, or asked for one. Answer with
    /// <see cref="AcceptTeleportOffer"/> / <see cref="DeclineTeleportOffer"/> (an offer) or
    /// <see cref="AnswerTeleportRequest"/> (a request). Raised on a LibreMetaverse network thread —
    /// marshal before touching a scene node.</summary>
    public event EventHandler<TeleportOfferEvent>? TeleportOfferReceived;

    /// <summary>Avatar-profile replies (FEAT-UI-13). All fired off a LibreMetaverse network
    /// thread after <see cref="RequestAvatarProfile"/> — consumers must marshal before touching a
    /// scene node. Neutral DTOs only; no LibreMetaverse type crosses this boundary.</summary>
    public event EventHandler<AvatarPropertiesEvent>? AvatarPropertiesReceived;

    /// <inheritdoc cref="AvatarPropertiesReceived"/>
    public event EventHandler<AvatarInterestsEvent>? AvatarInterestsReceived;

    /// <inheritdoc cref="AvatarPropertiesReceived"/>
    public event EventHandler<AvatarGroupsEvent>? AvatarGroupsReceived;

    /// <inheritdoc cref="AvatarPropertiesReceived"/>
    public event EventHandler<AvatarPicksEvent>? AvatarPicksReceived;

    /// <inheritdoc cref="AvatarPropertiesReceived"/>
    public event EventHandler<AvatarPickDetailEvent>? AvatarPickDetailReceived;

    /// <inheritdoc cref="AvatarPropertiesReceived"/>
    public event EventHandler<AvatarClassifiedsEvent>? AvatarClassifiedsReceived;

    /// <summary>Real, server-driven login handshake progress -- relayed 1:1 from LibreMetaverse's
    /// own <c>NetworkManager.LoginProgress</c> (see <see cref="LoginAsync"/>), not simulated. UI
    /// should treat these as advisory only: on a direct (non-redirected) login some stages
    /// (notably <see cref="LoginStage.Redirecting"/>) never fire.</summary>
    public event EventHandler<LoginProgressEvent>? LoginProgress;

    /// <summary>FEAT-UI-18: real teleport progress, relayed from LibreMetaverse's
    /// <c>Self.TeleportProgress</c> by every teleport path here (region-handle, landmark,
    /// global-position). A synthetic <see cref="TeleportStage.Started"/> is raised the moment a
    /// request is sent, and a terminal <see cref="TeleportStage.Finished"/> /
    /// <see cref="TeleportStage.Failed"/> once the awaited call returns, so a consumer can drive a
    /// show/hide overlay purely off this event without also polling the returned
    /// <see cref="TeleportResult"/>. Raised on a LibreMetaverse network thread -- marshal before
    /// touching UI, same as <see cref="LoginProgress"/>.</summary>
    public event EventHandler<TeleportProgressEvent>? TeleportProgress;

    /// <summary>Fired when we connect to a NEW primary/current simulator -- i.e. on login and on
    /// every teleport/region-crossing that changes which region we're actually in. NOT fired for
    /// LibreMetaverse's other SimConnected occurrences, e.g. a neighbor sim connected only for
    /// interest-list purposes near a region border (see OnSimConnected's e.Simulator ==
    /// CurrentSim guard) -- those aren't "we moved," so recentering on them would be wrong.
    /// Payload is the new region's handle. Consumers: RenderConfig.SetRegionOrigin (the floating-
    /// origin recenter) is the reason this exists -- see Boot.cs's subscription.</summary>
    public event EventHandler<ulong>? RegionConnected;

    /// <summary>Fired once per region, after its HTTP CAPS have actually been seeded -- unlike
    /// <see cref="RegionConnected"/> (raised from <c>SimConnected</c>), which fires BEFORE the caps
    /// handshake completes, so <c>CapabilityURI(...)</c> can still report a capability absent on a
    /// sim that genuinely has it (see <see cref="OnEventQueueRunning"/>'s doc comment -- the same
    /// reasoning FEAT-ENV-01's environment fetch is already built on). Any outbound send gated on a
    /// specific capability (hover height's <c>AgentPreferences</c> POST, FEAT-AVATAR-03) belongs
    /// here, not on <see cref="RegionConnected"/> -- that mistake shipped once (silently no-op'd on
    /// every relogin) and cost a live bug report to catch, because the LOCAL render doesn't depend
    /// on the cap at all and looked correct throughout.
    ///
    /// Deduped to once per <see cref="Simulator"/> instance, same guard as the environment capture
    /// this reuses. Raised from a background thread -- consumers must marshal before touching world
    /// state or a scene node.</summary>
    public event EventHandler<ulong>? RegionCapabilitiesReady;

    /// <summary>Fired once per region, after its capabilities are up, with a raw snapshot of the
    /// region's Windlight / EEP environment (FEAT-ENV-01 Phase A). Diagnostic for now: the payload
    /// carries the settings LLSD as text, because nothing parses it yet.
    ///
    /// Raised from a background thread (the capability fetch is async HTTP), like every other
    /// event on this class -- consumers must marshal before touching world state or a scene node.</summary>
    public event EventHandler<RegionEnvironmentCapture>? RegionEnvironmentCaptured;

    /// <summary>Fired once per region with its environment parsed into the engine-neutral model
    /// (FEAT-ENV-01 Phase B). This is what the renderer consumes; <see cref="RegionEnvironmentCaptured"/>
    /// is the raw evidence behind it, and both are raised for the same region.
    ///
    /// Raised from a background thread -- marshal before touching a scene node.</summary>
    public event EventHandler<RegionEnvironmentEvent>? RegionEnvironmentReceived;

    /// <summary>MVP2-3: the current region's full avatar radar snapshot (LibreMetaverse's
    /// <c>CoarseLocationUpdate</c>), replacing whatever snapshot preceded it. Fired off a
    /// background network thread -- consumers (the minimap) must buffer and drain on the main
    /// thread, same as every other event here.</summary>
    public event EventHandler<NearbyAvatarsEvent>? NearbyAvatarsUpdated;

    /// <summary>MVP2-3: one grid-map region tile resolved, either from an explicit
    /// <see cref="RequestMapBlocks"/>/<see cref="ResolveRegionByNameAsync"/> call or from
    /// LibreMetaverse's own cache. Multiple tiles from one <see cref="RequestMapBlocks"/> call
    /// each raise this once. Fired off a background thread -- marshal before touching a scene node.</summary>
    public event EventHandler<MapRegionInfo>? RegionDiscovered;

    internal void RaiseChatMessage(ChatMessageEvent e) => ChatMessageReceived?.Invoke(this, e);

    internal void RaiseObjectUpdate(ObjectUpdateEvent e) => ObjectUpdateReceived?.Invoke(this, e);

    internal void RaiseAvatarUpdate(AvatarUpdateEvent e) => AvatarUpdateReceived?.Invoke(this, e);

    internal void RaiseDisplayNameResolved(NameResolvedEvent e) => DisplayNameResolved?.Invoke(this, e);

    internal void RaiseObjectRemoved(ObjectRemovedEvent e) => ObjectRemovedReceived?.Invoke(this, e);

    internal void RaiseObjectProperties(ObjectPropertiesEvent e) => ObjectPropertiesReceived?.Invoke(this, e);

    internal void RaiseObjectMedia(ObjectMediaEvent e) => ObjectMediaReceived?.Invoke(this, e);

    internal void RaiseObjectAnimation(ObjectAnimationEvent e) => ObjectAnimationReceived?.Invoke(this, e);

    internal void RaiseTerrainPatch(TerrainPatchEvent e) => TerrainPatchReceived?.Invoke(this, e);

    internal void RaiseTerrainSettings(TerrainSettingsEvent e) => TerrainSettingsReceived?.Invoke(this, e);

    internal void RaiseRegionDisconnected(RegionDisconnectedEvent e) => RegionDisconnectedReceived?.Invoke(this, e);

    internal void RaiseAvatarAppearance(AvatarAppearanceEvent e) => AvatarAppearanceReceived?.Invoke(this, e);

    internal void RaiseAvatarAnimation(AvatarAnimationEvent e) => AvatarAnimationReceived?.Invoke(this, e);

    public GridSession()
    {
        // Quiet LibreMetaverse's own console logger (set before the client/logger initializes).
        // On a busy grid Info floods stdout ("Received a resend of already processed packet",
        // texture-pipeline chatter), which is why this was clamped.
        //
        // WARNING, not Error, since 2026-08-31. Clamping to Error hid the entire appearance/bake
        // diagnostic surface, and the FEAT-AVATAR-01 investigation spent days blind to it -- every
        // line that would have said WHY a bake produced nothing is Warn or below:
        //   "Baker produced no texture data for {bakeType}"
        //   "Texture {id} failed to download, one or more bakes will be incomplete"
        //   "Wearable {id} ({type}) failed to download or wrong asset type"
        //   "One or more agent wearables failed to download, appearance will be incomplete"
        // Warnings are rare and are exactly the "this silently did not work" class. Set
        // SLNG_LMV_DEBUG=1 for the full Debug trace (per-texture, per-wearable, per-bake timings)
        // when actually chasing a bake.
        var lmvLevel = Environment.GetEnvironmentVariable("SLNG_LMV_DEBUG") is "1" or "true" or "TRUE"
            ? Microsoft.Extensions.Logging.LogLevel.Debug
            : Microsoft.Extensions.Logging.LogLevel.Warning;
        LibreMetaverse.Settings.LogLevel = lmvLevel;

        // Settings.LogLevel alone does not silence LibreMetaverse: it builds its own console logger
        // factory on first use, and that factory carries its own minimum level. Debug lines from the
        // bake ("[Bake]: created head master bake", "[XBakes]: Number of alpha wearable textures")
        // printed on every channel of every bake despite the level being Warning. Hand it a factory
        // filtered to the level actually wanted.
        try
        {
            // The relay prints exactly what AddSimpleConsole printed, and additionally lets us
            // READ what LibreMetaverse says. That is not a convenience: a dead event queue is
            // reported nowhere else in the library (BUG-NET-20), because the server's error comes
            // back as HTTP 200 and every liveness flag stays green.
            LibreMetaverse.Logger.SetLoggerFactory(
                Microsoft.Extensions.Logging.LoggerFactory.Create(b => b
                    .SetMinimumLevel(lmvLevel)
                    .AddProvider(new LibreMetaverseLogRelay("SLNG", FilterLibreMetaverseLog))),
                "SLNG");
        }
        catch (Exception ex)
        {
            // Never worth failing a login over log plumbing.
            Console.Error.WriteLine($"[Net] could not install the LibreMetaverse log filter: {ex.Message}");
        }

        // BakeLayer.LoadResourceLayer (client-side avatar bake compositing, e.g. head_color.tga)
        // resolves default system-avatar layer textures via ResourceDir + "static_assets", and
        // ResourceDir defaults to the CWD-relative "linden". That's wrong under Godot: the NuGet
        // package's content files (linden/character/*, linden/static_assets/*) land next to
        // LibreMetaverse.dll in the build output, but Godot's CWD at runtime is the project root
        // -- so the lookup missed every time ("Failed opening resource ...\linden\static_assets\
        // head_color.tga"). Point it at the assembly's own directory instead, same fix
        // AvatarRenderer.cs already applies for the sibling "linden/character" mesh files.
        var asmLocation = typeof(LibreMetaverse.Settings).Assembly.Location;
        var libremetaverseDir = string.IsNullOrEmpty(asmLocation) ? AppContext.BaseDirectory : System.IO.Path.GetDirectoryName(asmLocation);
        if (string.IsNullOrEmpty(libremetaverseDir)) libremetaverseDir = AppContext.BaseDirectory;

        if (!string.IsNullOrEmpty(libremetaverseDir))
        {
            var lindenDir = System.IO.Path.Combine(libremetaverseDir, "linden");
            LibreMetaverse.Settings.ResourceDir = lindenDir;

            // Fixing ResourceDir alone isn't enough: the NuGet package's own layout puts every
            // default bake-layer .tga (head_color.tga, upperbody_color.tga, ...) under
            // "linden/character/", but BakeLayer.LoadResourceLayer always looks in
            // "<ResourceDir>/static_assets/" -- a mismatch inside the package itself, not
            // something ResourceDir can route around. Mirror the .tga files into static_assets/
            // once so the lookup finds them; best-effort since a missing layer just falls back
            // to a flat placeholder color (see AvatarRenderer's neutral-skin-tone comment).
            try
            {
                var characterDir = System.IO.Path.Combine(lindenDir, "character");
                var staticAssetsDir = System.IO.Path.Combine(lindenDir, "static_assets");
                if (System.IO.Directory.Exists(characterDir) && System.IO.Directory.Exists(staticAssetsDir))
                {
                    foreach (var tgaFile in System.IO.Directory.EnumerateFiles(characterDir, "*.tga"))
                    {
                        var dest = System.IO.Path.Combine(staticAssetsDir, System.IO.Path.GetFileName(tgaFile));
                        if (!System.IO.File.Exists(dest))
                            System.IO.File.Copy(tgaFile, dest);
                    }

                    BakeResourceLayers.UpscaleAll(lindenDir, Console.Error.WriteLine);
                }
            }
            catch (System.IO.IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        _client = new GridClient();

        // LibreMetaverse keeps its own on-disk asset cache (the wearable / animation / gesture /
        // sound assets it downloads for the bake pipeline). Its directory defaults to
        // "<Settings.ResourceDir>/cache" -- and ResourceDir was just repointed above at the folder
        // the assembly lives in. In an installed build that folder is %ProgramFiles%\PurisViewer,
        // which a normal-privilege process cannot write to: every SaveAssetToCache then throws
        // UnauthorizedAccessException ("Failed saving asset to cache (Access denied)") and the
        // cache never populates, so each session re-downloads the same wearables from the grid --
        // needless load on a rate-limited grid. Redirect it to a per-user writable location, the
        // same %LOCALAPPDATA%\SLNG root SelfAppearanceCache already uses.
        try
        {
            _client.Settings.AssetCache.Dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
                "SLNG", "lmv-asset-cache");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Net] could not redirect the LibreMetaverse asset cache: {ex.Message}");
        }

        // MUST stay true. This single flag gates LibreMetaverse's entire appearance/bake
        // workflow: Simulator_OnCapabilitiesReceived (the on-login / on-region-change trigger),
        // the AgentWearablesUpdate bake trigger, and the sim's RebakeAvatarTextures request are
        // ALL no-ops when it's false (see AppearanceManager.cs lines 3160, 2993, 3011). With it
        // false the client never asks the sim to composite bakes (server-side baking) and never
        // client-side bakes + uploads + AgentSetAppearance (OpenSim), so the LOCAL agent's baked
        // textures are never produced. The only self AvatarAppearance we then receive is the sim's
        // initial unprompted relay carrying a placeholder texture entry (a single blank,
        // alpha=0 sentinel repeated across every bake channel), and nothing ever replaces it --
        // the system body/head stays fully opaque instead of being hidden under a worn mesh body.
        // Other avatars are unaffected because THEIR viewers produce their bakes and the sim
        // relays them to us with real, per-channel ids.
        // DISABLED 2026-08-02 -- this WRITES to the user's account, it does not merely render.
        //
        // With this on, LibreMetaverse computes its own bake and sends AgentSetAppearance, which
        // the simulator PERSISTS. If that computation is wrong, it overwrites the real appearance
        // server-side for every viewer, not just ours. Confirmed exactly that way: the user logged
        // in with Firestorm and saw a correct avatar, which deformed again seconds later -- i.e.
        // Firestorm was reading back what we had written.
        //
        // Turning it on originally fixed a genuine LOCAL problem (the self-avatar never baked and
        // stayed visibly unbaked). That trade is not acceptable: a local rendering fault is
        // recoverable by relogging, a corrupted stored appearance is not. Re-enable only once our
        // bake output has been verified against the real viewer's, and preferably behind an
        // explicit opt-in -- an experimental viewer should not silently rewrite account data.
        //
        // CONFIRMED 2026-08-02 by LogVisualParamHealth(): Appearance.MyVisualParameters is EMPTY.
        // So every AgentSetAppearance we sent carried no shape at all, and the simulator replaced
        // the stored shape with defaults. That is the whole explanation for the squat, deformed
        // avatar, and it is why a rebake feature must stay blocked -- it would take the same path.
        //
        // 2026-08-31 -- the REASON is now measured, not suspected, and it was never the baking.
        // LibreMetaverse's ENCODER is broken: AppearanceManager.MakeAppearancePacket fills the 218
        // wire slots by iterating VisualParams.Params -- ALL 672 params, including the
        // never-transmitted group-1/2 ones -- and taking the first 218, while the wire order is
        // VisualParams.Group0ParamIds (the 253 TRANSMITTED ids). The two agree for 23 slots and
        // diverge from index 23 on: 195 of 218 values land on the WRONG parameter. Pinned by
        // SLNG.Assets.Tests.VisualParamOrderTests, and confirmed against the reference viewer,
        // which filters by param group where LibreMetaverse does not
        // (indra/llappearanceutility/llprocessparams.cpp:155-163).
        //
        // That single upstream bug explains 2026-08-02 (deformed), 2026-08-29 (flat) and
        // 2026-08-31 (torn rigged head) alike. The BAKING was never implicated: it demonstrably
        // worked for the eleven days this flag was on (80577c6 "fix self-avatar bake pipeline",
        // 2026-07-22 -> 783220e, 2026-08-02), and turning it off is what left the self avatar
        // permanently unbaked.
        //
        // RE-ENABLED 2026-08-31, now that all three failure modes are closed:
        //   1. Param order      -- AgentAppearanceParams rebuilds the array in wire order and
        //                          SendCorrectedAppearance replaces LibreMetaverse's packet with
        //                          it, refusing to send unless VerifyRoundTrip passes.
        //   2. Renderer input   -- OnAppearanceSet no longer hands MyVisualParameters (encoder
        //                          order) to the shape service; that was the torn rigged head.
        //   3. Empty bake slots -- MergeBakeSlots fills any missing bake from the simulator's own
        //                          last relay and refuses to send an incomplete set.
        // OFF AGAIN 2026-08-31, same day. Turning it on exposed a hole in the correction itself:
        // SendCorrectedAppearance bailed out with "no decoded wearables to build a shape from" on
        // the LOGIN bake, which left LibreMetaverse's scrambled packet standing as the last word.
        // An early return there is worse than useless -- once LMV has sent, the only safe move is
        // to overwrite it, never to stay silent. Fixed by falling back to the simulator's own last
        // relay for the params, but this flag stays off until that fallback has been verified
        // in-world, because with it on EVERY login writes to the account before a human can react.
        _client.Settings.Agent.SendAppearance = false;

        // Use the HTTP GetTexture CAP instead of the legacy UDP image transfer. UDP transfers
        // time out and hand back truncated JPEG2000 streams on busy grids (the "Tile part
        // length inconsistent" decode failures / white untextured objects); HTTP is reliable.
        //
        // BUG-NET-05/06 briefly set this false to stop repeated "Failed to fetch texture ...
        // Forbidden" log spam for world-object textures -- but this flag does not gate
        // FetchTextureDataAsync (SLNG's own world-object fetch always uses its own HTTP Range
        // path regardless of it, see that method's own comment) and IS what
        // GridClientBakingTextureProvider.RequestTextureAsync -- LibreMetaverse.Assets.
        // RequestImageAsync -> RequestImageInternal -- uses to decide HTTP vs. the legacy UDP
        // TexturePipeline for every avatar-bake texture fetch. Turning it off forced bake
        // fetches onto exactly the unreliable UDP path this comment already warns about, while
        // not touching the actual spam source at all. The real fix for the 403 spam is in
        // FetchTextureViaHttpRangeAsync: a 403 is a deliberate, permanent denial, not a transient
        // failure, and was wrongly marked retryable there -- see its own comment.
        _client.Settings.TexturePipeline.Enabled = true;
        _client.Settings.TexturePipeline.UseHttpTextures = true;

        // BUG-NET-03: connect to neighbor simulators so terrain/objects past the 256 m border
        // render ("Man kann nicht über die Sim-Grenze sehen"). LibreMetaverse 3.1.3 defaults this
        // to FALSE (AgentSettings.cs:10), and with it off NetworkManager.EnableSimulatorHandler
        // (NetworkManager.cs:1463) drops every EnableSimulator the grid sends outright -- no child
        // circuit is ever opened, so the world simply ends at the current region's edge. Nothing
        // else in this class filters neighbor data: OnObjectUpdate / OnLandPatchReceived /
        // OnKillObject / OnSimConnected(terrain settings) all already pass e.Simulator.Handle
        // straight through, the ECS World is keyed by (regionHandle, localId), and
        // RenderConfig.ToGodot offsets by the region's global corner -- so once the circuits exist
        // the neighbor content flows to the correct world position on its own. DisableSimulator
        // (handled by LibreMetaverse) then fires our OnSimDisconnected -> World.RemoveRegion to
        // unload a region we've moved away from.
        _client.Settings.Agent.MultipleSims = true;
        // BUG-NET-23: LibreMetaverse's own AgentThrottle send is what stopped every outgoing packet
        // for the rest of a session. With this on, each simulator connect calls Throttle.Set twice
        // (Simulator.ConnectAsync after the handshake, NetworkManager.Connect after SimConnected),
        // and Set ends in UdpThrottle.Update, which swaps in new TokenBucketRateLimiters and
        // disposes the old ones at once -- while OutgoingPacketHandler may be holding or awaiting
        // one of them (UdpThrottle.cs:104-117). The ObjectDisposedException escapes that loop's
        // inner try and ends it for good (NetworkManager.cs:1086-1125; 3.1.3 through 3.1.6 and
        // master). The swap buys nothing here: SLNG never changes Client.Throttle, so every Update
        // rebuilt identical buckets. So the library's send is off, and OnSimConnected sends the
        // same packet without the swap; the outgoing buckets keep the rates of the first connect.
        _client.Settings.Agent.SendThrottle = false;
        // BUG-INV-12: the inventory and Display Name caches open while the login response is
        // processed. Registered after GridClient's constructor has registered its own managers, so
        // this runs after AgentManager has taken the agent id and InventoryManager has built the
        // store from the skeleton -- see OnLoginResponseOpenCaches.
        _client.Network.RegisterLoginResponseCallback(OnLoginResponseOpenCaches);
        _client.Network.RegisterLoginResponseCallback(OnLoginResponseRememberMapServer);
        _client.Self.ChatFromSimulator += OnChatFromSimulator;
        _client.Objects.ObjectUpdate += OnObjectUpdate;
        _client.Objects.TerseObjectUpdate += OnTerseObjectUpdate;
        // FEAT-ANIMESH-02: what an animated mesh has been told to play. The library raises this for
        // every ObjectAnimation packet, empty lists included; the region's seed request already
        // asks for the capability OpenSim gates the message on (Caps.AllCapabilities).
        _client.Objects.ObjectAnimation += OnObjectAnimation;
        // Repairs the particle system LibreMetaverse loses on every compressed update -- see
        // CompressedParticleRepair. Registered here rather than replacing the library's handler,
        // so it runs after it: LibreMetaverse decodes the object as usual (correctly, apart from
        // the particles) and this puts the particles back.
        _client.Network.RegisterCallback(PacketType.ObjectUpdateCompressed, OnObjectUpdateCompressedRaw);
        RegisterRegionStreamProbe(); // BUG-NET-21
        _client.Objects.AvatarUpdate += OnAvatarUpdate;
        _client.Objects.ObjectPropertiesFamily += OnObjectPropertiesFamily;
        _client.Objects.ObjectProperties += OnObjectPropertiesFull;
        _client.Objects.PhysicsProperties += OnPhysicsProperties;
        _client.Avatars.UUIDNameReply += OnUUIDNameReply;
        _client.Avatars.DisplayNameUpdate += OnDisplayNameUpdate;
        _client.Groups.GroupNamesReply += OnGroupNamesReply;
        _client.Groups.CurrentGroups += OnCurrentGroups;
        _client.Self.GroupChatJoined += OnGroupChatJoined;
        _client.Self.AlertMessage += OnAlertMessage;
        // FEAT-ECON-01 takes the bare figure from MoneyBalance; MVP5-2 takes the transaction
        // record from MoneyBalanceReply, which is where the other party, the amount and the
        // reason live. Both are raised from the same packet, so subscribing to both costs
        // nothing and keeps "what is my balance" separate from "somebody paid you".
        _client.Self.MoneyBalance += OnMoneyBalance;
        _client.Self.MoneyBalanceReply += OnMoneyBalanceReply;
        _client.Objects.PayPriceReply += OnPayPriceReply;
        _client.Objects.KillObject += OnKillObject;
        _client.Objects.KillObjects += OnKillObjects;
        _client.Terrain.LandPatchReceived += OnLandPatchReceived;
        _client.Network.SimConnected += OnSimConnected;
        _client.Network.SimDisconnected += OnSimDisconnected;
        // BUG-NET-22: nothing was listening for the whole session ending. A region restart left
        // the client sitting in an empty world, still drawing an avatar, with a minimap showing
        // a region that is no longer there.
        _client.Network.Disconnected += OnNetworkDisconnected;
        // BUG-NET-04: a teleport to a DISTANT region left the old region's terrain/objects
        // rendered indefinitely -- "nach dem Teleport sehe ich noch die sim auf der ich gerade
        // war". See OnSimChanged's doc comment for the mechanism; wired here, next to the two
        // events it complements.
        _client.Network.SimChanged += OnSimChanged;
        // Environment (FEAT-ENV-01) hangs off EventQueueRunning, not SimConnected: both the
        // ExtEnvironment and EnvironmentSettings capabilities are HTTP CAPS, and at SimConnected
        // the cap seed has not necessarily been fetched yet, so CapabilityURI would report them
        // absent on a sim that has them.
        _client.Network.EventQueueRunning += OnEventQueueRunning;
        _client.Avatars.AvatarAppearance += OnAvatarAppearance;
        _client.Avatars.AvatarAnimation += OnAvatarAnimation;
        _client.Appearance.AppearanceSet += OnAppearanceSet;
        _client.Friends.FriendOnline += OnFriendOnline;
        _client.Friends.FriendOffline += OnFriendOffline;
        _client.Friends.FriendshipTerminated += OnFriendshipTerminated;
        _client.Self.IM += OnInstantMessage;
        _client.Self.ScriptDialog += OnScriptDialog;
        // Without this subscription the simulator's question is never even seen: LibreMetaverse's
        // ScriptQuestionHandler early-returns when nothing is listening. A script that has to ask
        // before it can act then waits forever -- which is what a pose stand whose menu does
        // nothing actually is.
        _client.Self.ScriptQuestion += OnScriptQuestion;
        // FEAT-UI-29: the sim's own word on which group is active -- at login and
        // after every ActivateGroup. Never inferred from our own request.
        _client.Self.AgentDataReply += OnAgentDataReply;
        // FEAT-UI-13: avatar profile replies. A single AvatarPropertiesRequest packet
        // (RequestAvatarProperties) makes the sim send Properties + Interests + Groups; Picks and
        // Classifieds have their own request/reply pairs (see RequestAvatarProfile).
        _client.Avatars.AvatarPropertiesReply += OnAvatarPropertiesReply;
        _client.Avatars.AvatarNotesReply += OnAvatarNotesReply; // FEAT-UI-39
        _client.Avatars.AvatarInterestsReply += OnAvatarInterestsReply;
        _client.Avatars.AvatarGroupsReply += OnAvatarGroupsReply;
        _client.Avatars.AvatarPicksReply += OnAvatarPicksReply;
        _client.Avatars.PickInfoReply += OnPickInfoReply;
        _client.Avatars.AvatarClassifiedReply += OnAvatarClassifiedReply;

        // MVP2-3: region radar (minimap) and grid-map tile resolution.
        _client.Grid.CoarseLocationUpdate += OnCoarseLocationUpdate;
        _client.Grid.GridRegion += OnGridRegion;
        _client.Parcels.ParcelProperties += OnParcelPropertiesReceived;
        RegisterParcelInfo(); // FEAT-LAND-01

        // Coexists with ObjectManager's own internal ObjectUpdate handler (packet callbacks are
        // multicast) -- see _lightObjects for why this is needed.
        _client.Network.RegisterCallback(PacketType.ObjectUpdate, OnRawObjectUpdatePacket);

        // FEAT-ENV-02: Intercept raw SimulatorViewerTimeMessage to sync the server's time
        _client.Network.RegisterCallback(PacketType.SimulatorViewerTimeMessage, OnSimulatorViewerTimePacket);

        // FEAT-ENV-01: a live environment change announces itself as a RegionInfo message, NOT as
        // an EventQueue event. This listened on RegisterEventCallback("ExtEnvironment") first,
        // which can never fire -- no EventQueue message of that name exists; it is a capability
        // name.
        //
        // What actually happens (OpenSim EnvironmentModule.WindlightRefresh, and the same shape on
        // SL): the server branches on what the viewer announced it can do. Requesting the
        // ExtEnvironment capability sets CapsFlags.AdvEnv (0x8000) for us, and that branch calls
        // HandleRegionInfoRequest -- a RegionInfo packet. Only a viewer WITHOUT that flag gets the
        // EventQueue "WindlightRefresh" event instead; being EEP-capable takes us off that path.
        //
        // The real viewer wires it exactly here too: llenvironment.cpp:886 subscribes to
        // LLRegionInfoModel's update callback and calls requestRegion() from it.
        _client.Network.RegisterCallback(PacketType.RegionInfo, OnRegionInfoPacket);

        // BUG-NET-09: a PARCEL-only environment edit (someone changes just the parcel you're
        // standing on, not the whole region) has no reachable push signal at all -- see this
        // field's own doc comment for why -- so the only way to notice one is to ask again
        // periodically. Started here, for the session's lifetime; stopped in Dispose.
        _ = Task.Run(() => ParcelEnvironmentPollLoopAsync(_parcelEnvironmentPollCts.Token));
    }

    /// <summary>True once a login has succeeded and the circuit is up.</summary>
    public bool IsConnected => _client.Network.Connected;

    /// <summary>The handle of the region the agent is currently in.</summary>
    public ulong CurrentRegionHandle => _client.Network.CurrentSim?.Handle ?? 0;

    /// <summary>The name of the region the agent is currently in, or empty if not connected.</summary>
    public string CurrentRegionName => _client.Network.CurrentSim?.Name ?? string.Empty;

    /// <summary>Agent UUID of the logged-in avatar, or empty until connected.</summary>
    public string AgentId => _client.Self.AgentID.ToString();

    /// <summary>Display name ("First Last") of the logged-in avatar -- used to tell the local
    /// user's own chat lines apart from everyone else's without threading a separate flag
    /// through ChatMessageEvent.</summary>
    public string AgentName => _client.Self.Name;

    /// <summary>The local ID of the object the agent is currently sitting on, or 0 if standing.</summary>
    public uint SittingOnLocalId => _client.Self.SittingOn;

    public void Dispose()
    {
        _parcelEnvironmentPollCts.Cancel();
        _parcelEnvironmentPollCts.Dispose();
        try { _wearableRebakeCts?.Cancel(); _wearableRebakeCts?.Dispose(); } catch { }
        _client.Network.UnregisterLoginResponseCallback(OnLoginResponseOpenCaches);
        _client.Self.ChatFromSimulator -= OnChatFromSimulator;
        _client.Objects.ObjectUpdate -= OnObjectUpdate;
        _client.Objects.TerseObjectUpdate -= OnTerseObjectUpdate;
        _client.Objects.ObjectAnimation -= OnObjectAnimation;
        _client.Self.AgentDataReply -= OnAgentDataReply;
        _client.Objects.ObjectPropertiesFamily -= OnObjectPropertiesFamily;
        _client.Objects.ObjectProperties -= OnObjectPropertiesFull;
        _client.Objects.PhysicsProperties -= OnPhysicsProperties;
        _client.Avatars.UUIDNameReply -= OnUUIDNameReply;
        _client.Avatars.DisplayNameUpdate -= OnDisplayNameUpdate;
        _client.Groups.GroupNamesReply -= OnGroupNamesReply;
        _client.Groups.CurrentGroups -= OnCurrentGroups;
        _client.Self.GroupChatJoined -= OnGroupChatJoined;
        _client.Self.AlertMessage -= OnAlertMessage;
        // The next session's regions have nothing to do with this one's (BUG-NET-20).
        lock (_eventQueueHealth) _eventQueueHealth.Clear();
        lock (_departureLock) { _objectsAtDeparture.Clear(); _departureOrder.Clear(); } // BUG-NET-21
        _client.Objects.KillObject -= OnKillObject;
        _client.Objects.KillObjects -= OnKillObjects;
        _client.Terrain.LandPatchReceived -= OnLandPatchReceived;
        _client.Network.SimConnected -= OnSimConnected;
        _client.Network.SimDisconnected -= OnSimDisconnected;
        _client.Network.SimChanged -= OnSimChanged;
        _client.Appearance.AppearanceSet -= OnAppearanceSet;
        _client.Friends.FriendOnline -= OnFriendOnline;
        _client.Friends.FriendOffline -= OnFriendOffline;
        _client.Friends.FriendshipTerminated -= OnFriendshipTerminated;
        _client.Self.IM -= OnInstantMessage;
        _client.Self.ScriptDialog -= OnScriptDialog;
        _client.Self.MoneyBalanceReply -= OnMoneyBalanceReply;
        _client.Avatars.AvatarPropertiesReply -= OnAvatarPropertiesReply;
        _client.Avatars.AvatarNotesReply -= OnAvatarNotesReply;
        _client.Avatars.AvatarInterestsReply -= OnAvatarInterestsReply;
        _client.Avatars.AvatarGroupsReply -= OnAvatarGroupsReply;
        _client.Avatars.AvatarPicksReply -= OnAvatarPicksReply;
        _client.Avatars.PickInfoReply -= OnPickInfoReply;
        _client.Avatars.AvatarClassifiedReply -= OnAvatarClassifiedReply;
        _client.Grid.CoarseLocationUpdate -= OnCoarseLocationUpdate;
        _client.Grid.GridRegion -= OnGridRegion;
        _client.Parcels.ParcelProperties -= OnParcelPropertiesReceived;
        UnregisterParcelInfo();
        _currentParcelName = null;
        _client.Network.UnregisterCallback(PacketType.ObjectUpdate, OnRawObjectUpdatePacket);
        UnregisterRegionStreamProbe();
        UnregisterObjectCache();
        Logout();
    }
}
