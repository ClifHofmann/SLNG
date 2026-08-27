using System.Collections.Concurrent;
using System.Net.Http;
using LibreMetaverse;
using LibreMetaverse.Packets;
using LibreMetaverse.StructuredData;
using SLNG.Core;

namespace SLNG.Net;

/// <summary>
/// A connection to a single Second Life / OpenSim grid. Wraps the LibreMetaverse
/// <see cref="GridClient"/> and exposes an engine-agnostic login API. This is the
/// seam between the protocol stack and the rest of SLNG: nothing above this layer
/// sees a LibreMetaverse type.
/// </summary>
public sealed class GridSession : IDisposable, IWorldEventSource
{
    /// <summary>How long to wait for a ParcelProperties reply before giving up and using the
    /// region's environment. Short on purpose: this sits in the login path, and the region scope is
    /// a correct fallback, so a slow sim must not hold up the sky.</summary>
    private static readonly TimeSpan ParcelLookupTimeout = TimeSpan.FromSeconds(4);

    private readonly GridClient _client;

    /// <summary>Correlates a ParcelProperties reply with our own request. The simulator also pushes
    /// ParcelProperties unprompted on a parcel crossing, so matching on the sequence id is what
    /// keeps an unrelated push from being read as our answer.</summary>
    private int _parcelSequenceId;

    // Shared cache for both avatar (Creator/Owner/...) and group names -- both are keyed by
    // UUID and populated via the same resolve-and-notify flow, so one cache/event pair covers
    // both instead of duplicating the plumbing per name kind.
    private readonly ConcurrentDictionary<Guid, string> _nameCache = new();

    // Whether the most recently seen raw ObjectUpdate packet for a given LocalID carried a
    // Light (0x20) ExtraParams block. Needed because Primitive.Light is a latch, not a live
    // value: LibreMetaverse only ever WRITES it inside SetExtraParamsFromBytes' Light case, and
    // OpenSim omits the block entirely (no explicit "off" marker) once a light is disabled --
    // so Primitive.Light keeps reporting the last-enabled state forever, even after the real
    // light is turned off server-side and even across a relog. Populated by a raw packet
    // callback (see OnRawObjectUpdatePacket) registered alongside ObjectManager's own internal
    // handler, since the high-level Primitive/PrimEventArgs API exposes no such signal.
    private readonly ConcurrentDictionary<uint, bool> _lightPresentByLocalId = new();

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
    public event EventHandler<NameResolvedEvent>? NameResolved;
    public event EventHandler<NameResolvedEvent>? DisplayNameResolved;
    public event EventHandler<AlertMessageEvent>? AlertMessageReceived;
    public event EventHandler<TerrainPatchEvent>? TerrainPatchReceived;
    public event EventHandler<TerrainSettingsEvent>? TerrainSettingsReceived;
    public event EventHandler<RegionDisconnectedEvent>? RegionDisconnectedReceived;
    public event EventHandler<AvatarAppearanceEvent>? AvatarAppearanceReceived;
    public event EventHandler<AvatarAnimationEvent>? AvatarAnimationReceived;
    public event EventHandler<FriendStatusEvent>? FriendStatusChanged;
    public event EventHandler<InstantMessageEvent>? InstantMessageReceived;
    public event EventHandler<ScriptDialogEvent>? ScriptDialogReceived;

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
    /// <summary>Fired when we connect to a NEW primary/current simulator -- i.e. on login and on
    /// every teleport/region-crossing that changes which region we're actually in. NOT fired for
    /// LibreMetaverse's other SimConnected occurrences, e.g. a neighbor sim connected only for
    /// interest-list purposes near a region border (see OnSimConnected's e.Simulator ==
    /// CurrentSim guard) -- those aren't "we moved," so recentering on them would be wrong.
    /// Payload is the new region's handle. Consumers: RenderConfig.SetRegionOrigin (the floating-
    /// origin recenter) is the reason this exists -- see Boot.cs's subscription.</summary>
    public event EventHandler<ulong>? RegionConnected;

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

    internal void RaiseChatMessage(ChatMessageEvent e) => ChatMessageReceived?.Invoke(this, e);
    internal void RaiseObjectUpdate(ObjectUpdateEvent e) => ObjectUpdateReceived?.Invoke(this, e);
    internal void RaiseAvatarUpdate(AvatarUpdateEvent e) => AvatarUpdateReceived?.Invoke(this, e);
    internal void RaiseObjectRemoved(ObjectRemovedEvent e) => ObjectRemovedReceived?.Invoke(this, e);
    internal void RaiseTerrainPatch(TerrainPatchEvent e) => TerrainPatchReceived?.Invoke(this, e);
    internal void RaiseTerrainSettings(TerrainSettingsEvent e) => TerrainSettingsReceived?.Invoke(this, e);
    internal void RaiseRegionDisconnected(RegionDisconnectedEvent e) => RegionDisconnectedReceived?.Invoke(this, e);
    internal void RaiseAvatarAppearance(AvatarAppearanceEvent e) => AvatarAppearanceReceived?.Invoke(this, e);
    internal void RaiseAvatarAnimation(AvatarAnimationEvent e) => AvatarAnimationReceived?.Invoke(this, e);

    public GridSession()
    {
        // Quiet LibreMetaverse's own console logger (set before the client/logger initializes).
        // On a busy grid it floods stdout with Info spam ("Received a resend of already
        // processed packet", texture-pipeline chatter); warnings/errors still come through.
        LibreMetaverse.Settings.LogLevel = Microsoft.Extensions.Logging.LogLevel.Error;

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
                }
            }
            catch (System.IO.IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        _client = new GridClient();
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
        _client.Settings.Agent.SendAppearance = false;

        // Use the HTTP GetTexture CAP instead of the legacy UDP image transfer. UDP transfers
        // time out and hand back truncated JPEG2000 streams on busy grids (the "Tile part
        // length inconsistent" decode failures / white untextured objects); HTTP is reliable.
        _client.Settings.TexturePipeline.Enabled = true;
        _client.Settings.TexturePipeline.UseHttpTextures = true;
        _client.Self.ChatFromSimulator += OnChatFromSimulator;
        _client.Objects.ObjectUpdate += OnObjectUpdate;
        _client.Objects.TerseObjectUpdate += OnTerseObjectUpdate;
        // Repairs the particle system LibreMetaverse loses on every compressed update -- see
        // CompressedParticleRepair. Registered here rather than replacing the library's handler,
        // so it runs after it: LibreMetaverse decodes the object as usual (correctly, apart from
        // the particles) and this puts the particles back.
        _client.Network.RegisterCallback(PacketType.ObjectUpdateCompressed, OnObjectUpdateCompressedRaw);
        _client.Objects.AvatarUpdate += OnAvatarUpdate;
        _client.Objects.ObjectPropertiesFamily += OnObjectPropertiesFamily;
        _client.Objects.ObjectProperties += OnObjectPropertiesFull;
        _client.Objects.PhysicsProperties += OnPhysicsProperties;
        _client.Avatars.UUIDNameReply += OnUUIDNameReply;
        _client.Avatars.DisplayNameUpdate += OnDisplayNameUpdate;
        _client.Groups.GroupNamesReply += OnGroupNamesReply;
        _client.Self.AlertMessage += OnAlertMessage;
        _client.Objects.KillObject += OnKillObject;
        _client.Objects.KillObjects += OnKillObjects;
        _client.Terrain.LandPatchReceived += OnLandPatchReceived;
        _client.Network.SimConnected += OnSimConnected;
        _client.Network.SimDisconnected += OnSimDisconnected;
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
        _client.Self.IM += OnInstantMessage;
        _client.Self.ScriptDialog += OnScriptDialog;
        // FEAT-UI-13: avatar profile replies. A single AvatarPropertiesRequest packet
        // (RequestAvatarProperties) makes the sim send Properties + Interests + Groups; Picks and
        // Classifieds have their own request/reply pairs (see RequestAvatarProfile).
        _client.Avatars.AvatarPropertiesReply += OnAvatarPropertiesReply;
        _client.Avatars.AvatarInterestsReply += OnAvatarInterestsReply;
        _client.Avatars.AvatarGroupsReply += OnAvatarGroupsReply;
        _client.Avatars.AvatarPicksReply += OnAvatarPicksReply;
        _client.Avatars.PickInfoReply += OnPickInfoReply;
        _client.Avatars.AvatarClassifiedReply += OnAvatarClassifiedReply;

        // Coexists with ObjectManager's own internal ObjectUpdate handler (packet callbacks are
        // multicast) -- see _lightPresentByLocalId for why this is needed.
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
    }

    private void OnRegionInfoPacket(object? sender, PacketReceivedEventArgs e) => RepollEnvironment();

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
                    if (capture == null) continue;

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

    /// <summary>Scans each object's raw ExtraParams bytes for a Light (0x20) block, independent
    /// of LibreMetaverse's own parsing -- see <see cref="_lightPresentByLocalId"/> for why this
    /// can't be read back from the high-level Primitive object. Byte layout matches
    /// Primitive.SetExtraParamsFromBytes: 1 count byte, then per entry a UInt16 type + UInt32
    /// length + that many payload bytes.</summary>
    private void OnRawObjectUpdatePacket(object? sender, PacketReceivedEventArgs e)
    {
        if (e.Packet is not ObjectUpdatePacket update) return;

        foreach (var block in update.ObjectData)
        {
            _lightPresentByLocalId[block.ID] = ExtraParamsContainsLight(block.ExtraParams);
        }
    }

    private void OnSimulatorViewerTimePacket(object? sender, PacketReceivedEventArgs e)
    {
        if (e.Packet is SimulatorViewerTimeMessagePacket timePacket)
        {
            // Update thread-safe properties from the packet payload
            SimUnixTime = timePacket.TimeInfo.UsecSinceStart;
            SunPhase = timePacket.TimeInfo.SunPhase;
        }
    }

    private static bool ExtraParamsContainsLight(byte[]? data)
    {
        if (data == null || data.Length < 1) return false;

        int i = 0;
        byte count = data[i++];
        for (int k = 0; k < count; k++)
        {
            if (i + 6 > data.Length) break;
            ushort type = Utils.BytesToUInt16(data, i); i += 2;
            uint len = Utils.BytesToUInt(data, i); i += 4;
            if (type == 0x20) return true; // ExtraParamType.Light
            i += (int)len;
        }
        return false;
    }

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
            RegionConnected?.Invoke(this, sim.Handle);
        }
    }

    /// <summary>Captures the region's environment once its capabilities are live (FEAT-ENV-01
    /// Phase A). Fire-and-forget on purpose: nothing in the login path waits on the environment,
    /// and a sim that never answers must not stall the connection.</summary>
    private void OnEventQueueRunning(object? sender, LibreMetaverse.EventQueueRunningEventArgs e)
    {
        if (e.Simulator != _client.Network.CurrentSim) return;

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

    private async Task<(RegionEnvironmentCapture? Capture, RegionEnvironmentEvent? Environment)>
        FetchRegionEnvironmentAsync(
            bool includeLegacyAlongsideExt = true, CancellationToken cancellationToken = default)
    {
        var sim = _client.Network.CurrentSim;
        if (sim == null) return (null, null);

        bool hasExt = sim.Caps?.CapabilityURI("ExtEnvironment") != null;
        bool hasLegacy = sim.Caps?.CapabilityURI("EnvironmentSettings") != null;

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
                var ext = await _client.Environment.GetRegionEnvironmentAsync(cancellationToken).ConfigureAwait(false);
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
                    var parcelEnv = await _client.Environment
                        .GetParcelEnvironmentAsync(parcelId, cancellationToken).ConfigureAwait(false);
                    if (parcelEnv?.Environment is { DayCycle: not null } pdata)
                    {
                        parcelSettings = pdata.DayCycle;
                        parcelLlsd = OSDParser.SerializeLLSDNotationFormatted(pdata.DayCycle);
                        parcelDayLength = pdata.DayLength;
                        parcelDayOffset = pdata.DayOffset;
                    }
                }
            }

            // Asked for alongside EEP only when someone is going to read it: at login, where the
            // point is to record what this grid actually offers. A region with no EEP capability
            // still always asks, because there it is not a diagnostic but the only source there is.
            if (hasLegacy && (includeLegacyAlongsideExt || !hasExt))
            {
                var legacy = await _client.Environment.GetLegacyEnvironmentAsync(cancellationToken).ConfigureAwait(false);
                if (legacy?.Settings != null)
                {
                    legacySettings = legacy.Settings;
                    legacyLlsd = OSDParser.SerializeLLSDNotationFormatted(legacy.Settings);
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

    private void OnChatFromSimulator(object? sender, ChatEventArgs e)
    {
        // StartTyping/StopTyping are the "..." typing indicator other viewers show next to a
        // name -- they carry no message text at all. Forwarding them here unfiltered showed up
        // as a chat log line with a timestamp and sender name but nothing after the colon, once
        // per keystroke-session per person (live-tested: reported as "irgendwie fehlen hier im
        // chat texte" against a busy multi-avatar conversation, where every blank line lined up
        // exactly with the sender starting/stopping typing right before/after a real message).
        if (e.Type == ChatType.StartTyping || e.Type == ChatType.StopTyping) return;

        // Drop truly empty chat (an object emitting "" on channel 0 as a heartbeat/clear -- e.g. a
        // worn radio), same as the Linden/Firestorm nearby-chat handler which skips on
        // mText.empty(). Strictly IsNullOrEmpty, not whitespace, so a deliberate " " separator
        // line from a script still shows.
        if (string.IsNullOrEmpty(e.Message)) return;

        ChatMessageReceived?.Invoke(this, new ChatMessageEvent(
            e.FromName,
            e.Message,
            (byte)e.Type,
            e.SourceID.Guid,
            e.SourceType == ChatSourceType.Agent));
    }

    private void OnAvatarUpdate(object? sender, AvatarUpdateEventArgs e)
    {
        bool isLocalAgent = e.Avatar.ID == _client.Self.AgentID;
        ResolveSeatedTransform(e.Simulator, e.Avatar.Position, e.Avatar.Rotation, e.Avatar.ParentID,
            out var worldPos, out var worldRot);

        AvatarUpdateReceived?.Invoke(this, new AvatarUpdateEvent(
            e.Simulator.Handle,
            e.Avatar.LocalID,
            e.Avatar.ID.Guid,
            new System.Numerics.Vector3(worldPos.X, worldPos.Y, worldPos.Z),
            new System.Numerics.Quaternion(worldRot.X, worldRot.Y, worldRot.Z, worldRot.W),
            e.Avatar.FirstName,
            e.Avatar.LastName,
            isLocalAgent,
            e.Avatar.Scale.Z,
            new System.Numerics.Vector3(e.Avatar.Velocity.X, e.Avatar.Velocity.Y, e.Avatar.Velocity.Z),
            e.TimeDilation / 65535.0f,
            e.Avatar.ParentID,
            ToSupportPlane(e.Avatar.CollisionPlane)));
    }

    /// <summary>SL's collision plane, converted at the boundary — no LibreMetaverse type may cross
    /// a public boundary of SLNG.Net (AGENTS.md).
    ///
    /// This is the simulator's own answer to what an avatar is standing on, produced by its Havok
    /// physics and shipped in the avatar's update. It is present only in the 140- and 76-byte
    /// ObjectData layouts; LibreMetaverse leaves it at default otherwise, so an all-zero plane is
    /// reported as "not sent" rather than as a degenerate plane through the origin. Both readings
    /// are wrong to clamp against, but only one of them is honest about why.
    ///
    /// The viewer keeps the same value as <c>LLVOAvatar::mFootPlane</c> and tests it with
    /// <c>isExactlyClear()</c> for exactly this reason (llworld.cpp:570).</summary>
    private static System.Numerics.Vector4? ToSupportPlane(LibreMetaverse.Vector4 plane)
    {
        if (plane.X == 0f && plane.Y == 0f && plane.Z == 0f && plane.W == 0f) return null;
        return new System.Numerics.Vector4(plane.X, plane.Y, plane.Z, plane.W);
    }

    /// <summary>MVP2-1: once an avatar sits, its wire Position/Rotation become relative to the
    /// seat prim (0 if standing) -- mirrors LibreMetaverse's own AgentManager.SimPosition/
    /// SimRotation walk (AgentManager.cs, verified against the vendored source), generalized here
    /// to ANY avatar (not just the local agent, which is all LMV itself resolves) since GridSession
    /// is the one seam where every avatar's transform gets converted to world space regardless of
    /// who it belongs to -- nothing downstream (WorldSimulation, the renderer, the camera) needs to
    /// know or special-case a seated avatar's transform at all. A no-op (returns the input
    /// unchanged) when parentLocalId is 0.</summary>
    private static void ResolveSeatedTransform(
        LibreMetaverse.Simulator sim, LibreMetaverse.Vector3 relPos, LibreMetaverse.Quaternion relRot,
        uint parentLocalId, out LibreMetaverse.Vector3 worldPos, out LibreMetaverse.Quaternion worldRot)
    {
        worldPos = relPos;
        worldRot = relRot;
        if (parentLocalId == 0) return;

        if (!sim.ObjectsPrimitives.TryGetValue(parentLocalId, out var seat) || seat == null) return;

        worldPos = seat.Position + relPos * seat.Rotation;
        worldRot = relRot * seat.Rotation;

        // Walk up a linked-seat's own parent chain (e.g. sitting on a child prim of a vehicle) --
        // same loop LMV's SimPosition runs, position-only (LMV's own algorithm does not further
        // rotate by each ancestor, so this deliberately doesn't either).
        var p = seat;
        while (p != null && p.ParentID != 0)
        {
            if (sim.ObjectsAvatars.TryGetValue(p.ParentID, out var av) && av != null)
            {
                p = av;
                worldPos += p.Position;
            }
            else if (sim.ObjectsPrimitives.TryGetValue(p.ParentID, out p) && p != null)
            {
                worldPos += p.Position;
            }
            else
            {
                break;
            }
        }
    }

    private void OnObjectPropertiesFamily(object? sender, ObjectPropertiesFamilyEventArgs e)
    {
        // ObjectPropertiesFamily never carries CreatorID (LibreMetaverse leaves
        // Primitive.ObjectProperties.CreatorID unset for this message type) -- only the full
        // ObjectProperties message below has it. WorldSimulation.ApplyObjectProperties knows not
        // to let this empty value stomp a CreatorID already learned from the full message.
        ObjectPropertiesReceived?.Invoke(this, new ObjectPropertiesEvent(
            e.Simulator.Handle,
            e.Properties.ObjectID.Guid,
            e.Properties.Name ?? "",
            e.Properties.Description ?? "",
            e.Properties.CreatorID.Guid,
            e.Properties.OwnerID.Guid,
            e.Properties.GroupID.Guid,
            e.Properties.Permissions.OwnerMask.HasFlag(PermissionMask.Move),
            e.Properties.Permissions.OwnerMask.HasFlag(PermissionMask.Modify),
            e.Properties.Permissions.OwnerMask.HasFlag(PermissionMask.Copy),
            e.Properties.Permissions.OwnerMask.HasFlag(PermissionMask.Transfer)
        ));
    }

    /// <summary>The full ObjectProperties message, sent automatically by the simulator when an
    /// object is selected (see GridSession.SelectObject). Unlike the family variant, this one
    /// includes CreatorID.</summary>
    private void OnObjectPropertiesFull(object? sender, ObjectPropertiesEventArgs e)
    {
        ObjectPropertiesReceived?.Invoke(this, new ObjectPropertiesEvent(
            e.Simulator.Handle,
            e.Properties.ObjectID.Guid,
            e.Properties.Name ?? "",
            e.Properties.Description ?? "",
            e.Properties.CreatorID.Guid,
            e.Properties.OwnerID.Guid,
            e.Properties.GroupID.Guid,
            e.Properties.Permissions.OwnerMask.HasFlag(PermissionMask.Move),
            e.Properties.Permissions.OwnerMask.HasFlag(PermissionMask.Modify),
            e.Properties.Permissions.OwnerMask.HasFlag(PermissionMask.Copy),
            e.Properties.Permissions.OwnerMask.HasFlag(PermissionMask.Transfer)
        ));
    }

    /// <summary>Unlike ObjectUpdate/ObjectProperties, this only arrives after an explicit
    /// object-select request (see SelectObject) -- delivered asynchronously over the EventQueue
    /// CAP, not plain UDP, so it can land well after the select call returns.</summary>
    private void OnPhysicsProperties(object? sender, PhysicsPropertiesEventArgs e)
    {
        var p = e.PhysicsProperties;
        byte shapeByte = (byte)p.PhysicsShapeType;
        PhysicsPropertiesReceived?.Invoke(this, new PhysicsPropertiesEvent(
            e.Simulator.Handle,
            p.LocalID,
            shapeByte <= 2 ? (SLNG.Core.PrimPhysicsShapeType)shapeByte : SLNG.Core.PrimPhysicsShapeType.Prim,
            p.Density,
            p.Friction,
            p.Restitution,
            p.GravityMultiplier
        ));
    }

    private void OnUUIDNameReply(object? sender, UUIDNameReplyEventArgs e)
    {
        var idsToRequest = new System.Collections.Generic.List<UUID>();
        foreach (var kvp in e.Names)
        {
            var id = kvp.Key.Guid;
            _nameCache[id] = kvp.Value;
            NameResolved?.Invoke(this, new NameResolvedEvent(id, kvp.Value));
            idsToRequest.Add(kvp.Key);
        }
        if (idsToRequest.Count > 0)
        {
            try { _client.Avatars.GetDisplayNamesAsync(idsToRequest); } catch { /* Ignore if not supported/disabled */ }
        }
    }

    private void OnDisplayNameUpdate(object? sender, DisplayNameUpdateEventArgs e)
    {
        var id = e.DisplayName.ID.Guid;
        string? displayName = e.DisplayName.DisplayName;
        if (!string.IsNullOrEmpty(displayName))
        {
            DisplayNameResolved?.Invoke(this, new NameResolvedEvent(id, displayName));
        }
    }

    private void OnGroupNamesReply(object? sender, GroupNamesEventArgs e)
    {
        foreach (var kvp in e.GroupNames)
        {
            var id = kvp.Key.Guid;
            var name = string.IsNullOrEmpty(kvp.Value) ? "(unknown group)" : kvp.Value;
            _nameCache[id] = name;
            NameResolved?.Invoke(this, new NameResolvedEvent(id, name));
        }
    }

    /// <summary>The sim's urgent-message channel -- covers rejections that otherwise fail
    /// completely silently, e.g. OpenSim's SceneGraph.UpdatePrimFlags sending "Object physics
    /// cancelled because it exceeds limits for physical prims" when a Physical toggle is denied
    /// (size/linkset physics-capacity limits) instead of an ObjectFlagUpdate ever coming back.</summary>
    private void OnAlertMessage(object? sender, AlertMessageEventArgs e)
    {
        AlertMessageReceived?.Invoke(this, new AlertMessageEvent(e.Message));
    }

    /// <summary>Looks up an already-resolved user/group name from the local cache. Returns
    /// false (with the raw id's string form) if it hasn't been fetched yet -- call
    /// <see cref="RequestAvatarName"/>/<see cref="RequestGroupName"/> and wait for
    /// <see cref="NameResolved"/> in that case.</summary>
    public bool TryGetCachedName(Guid id, out string name)
    {
        if (_nameCache.TryGetValue(id, out var cached))
        {
            name = cached;
            return true;
        }
        name = id.ToString();
        return false;
    }

    public void RequestAvatarName(Guid agentId)
    {
        if (agentId == Guid.Empty || _nameCache.ContainsKey(agentId) || !_client.Network.Connected) return;
        _client.Avatars.RequestAvatarName(new UUID(agentId));
    }

    public void RequestGroupName(Guid groupId)
    {
        if (groupId == Guid.Empty || _nameCache.ContainsKey(groupId) || !_client.Network.Connected) return;
        _client.Groups.RequestGroupName(new UUID(groupId));
    }

    private void OnFriendOnline(object? sender, FriendInfoEventArgs e) =>
        FriendStatusChanged?.Invoke(this, new FriendStatusEvent(e.Friend.UUID.Guid, true));

    private void OnFriendOffline(object? sender, FriendInfoEventArgs e) =>
        FriendStatusChanged?.Invoke(this, new FriendStatusEvent(e.Friend.UUID.Guid, false));

    /// <summary>Snapshot of the logged-in agent's friends list. LibreMetaverse's FriendInfo
    /// usually already carries a resolved Name; when it doesn't, this falls back to the shared
    /// name cache (see <see cref="TryGetCachedName"/>) and kicks off a resolve via
    /// <see cref="RequestAvatarName"/> so a later call (e.g. after <see cref="NameResolved"/>
    /// fires) picks it up -- same pattern as every other UUID-keyed name in this class.</summary>
    public IReadOnlyList<FriendEntry> GetFriends()
    {
        var result = new List<FriendEntry>();
        foreach (var friend in _client.Friends.FriendList.Values)
        {
            var id = friend.UUID.Guid;
            string name = friend.Name;
            if (string.IsNullOrEmpty(name) && !TryGetCachedName(id, out name))
            {
                name = "";
                RequestAvatarName(id);
            }
            result.Add(new FriendEntry(id, name, friend.IsOnline));
        }
        return result;
    }

    // Self.IM carries every instant-message-shaped packet (friendship offers, teleport
    // requests, group notices, ...), not just plain 1:1 chat -- filter to MessageFromAgent so
    // Phase 1c's IM tabs only see actual conversation messages. The others get their own
    // dedicated flows later rather than being half-handled here.
    private void OnInstantMessage(object? sender, InstantMessageEventArgs e)
    {
        if (e.IM.Dialog != InstantMessageDialog.MessageFromAgent || e.IM.GroupIM) return;

        InstantMessageReceived?.Invoke(this, new InstantMessageEvent(
            e.IM.FromAgentID.Guid, e.IM.FromAgentName, e.IM.Message, e.IM.IMSessionID.Guid));
    }

    /// <summary>Sends a 1:1 instant message.</summary>
    public void SendInstantMessage(Guid targetAgentId, string message)
    {
        if (_client.Network.Connected)
            _client.Self.InstantMessage(new UUID(targetAgentId), message);
    }

    // ---- FEAT-UI-13: avatar profile fetch + social actions --------------------------------

    /// <summary>Kicks off the full profile fetch for one avatar. A single AvatarPropertiesRequest
    /// makes the sim send Properties + Interests + Groups; Picks and Classifieds have their own
    /// request/reply pairs. Results arrive asynchronously on <see cref="AvatarPropertiesReceived"/>
    /// and its siblings — a network-thread event, marshal before touching the UI.</summary>
    public void RequestAvatarProfile(Guid agentId)
    {
        if (agentId == Guid.Empty || !_client.Network.Connected) return;
        var id = new UUID(agentId);
        _client.Avatars.RequestAvatarProperties(id);
        _client.Avatars.RequestAvatarPicks(id);
        _client.Avatars.RequestAvatarClassified(id);
    }

    /// <summary>Requests the full detail of one Pick (image, description, location). Result on
    /// <see cref="AvatarPickDetailReceived"/>.</summary>
    public void RequestAvatarPickInfo(Guid agentId, Guid pickId)
    {
        if (agentId == Guid.Empty || pickId == Guid.Empty || !_client.Network.Connected) return;
        _client.Avatars.RequestPickInfo(new UUID(agentId), new UUID(pickId));
    }

    /// <summary>Teleports to a global position by resolving the containing region by name (the
    /// form Picks / the map give us). Region-local coordinates are the global position modulo the
    /// 256 m region grid — correct for standard regions; a varregion pick could land off-centre.</summary>
    public void TeleportToGlobalPosition(string regionName, double globalX, double globalY, double globalZ)
    {
        if (string.IsNullOrEmpty(regionName) || !_client.Network.Connected) return;
        var local = new Vector3(
            (float)(globalX - Math.Floor(globalX / 256.0) * 256.0),
            (float)(globalY - Math.Floor(globalY / 256.0) * 256.0),
            (float)globalZ);
        _ = _client.Self.TeleportAsync(regionName, local);
    }

    /// <summary>Writes the logged-in agent's own "2nd Life" / "1st Life" profile pages
    /// (<c>AvatarPropertiesUpdate</c>, or the AgentProfile CAP where the sim has one). The whole
    /// struct is sent every time, so the caller must pass the CURRENT image ids back unchanged or
    /// they get cleared — picture editing is a separate (upload/pick) feature. On grids without a
    /// profile service this is a silent no-op.</summary>
    public void UpdateOwnProfile(string aboutText, string firstLifeText, string profileUrl,
        Guid profileImageId, Guid firstLifeImageId, bool allowPublish, bool maturePublish)
    {
        if (!_client.Network.Connected) return;
        _client.Self.UpdateProfile(new Avatar.AvatarProperties
        {
            AboutText = aboutText ?? string.Empty,
            FirstLifeText = firstLifeText ?? string.Empty,
            ProfileURL = profileUrl ?? string.Empty,
            ProfileImage = new UUID(profileImageId),
            FirstLifeImage = new UUID(firstLifeImageId),
            AllowPublish = allowPublish,
            MaturePublish = maturePublish,
        });
    }

    /// <summary>Writes the logged-in agent's own profile "Interests" free-text fields
    /// (<c>AvatarInterestsUpdate</c>). The skill / want-to bitmasks (the viewer's checkbox lists)
    /// are sent as 0 — this pass edits the text only.</summary>
    public void UpdateOwnInterests(string languages, string skills, string wantTo)
    {
        if (!_client.Network.Connected) return;
        _client.Self.UpdateInterests(new Avatar.Interests
        {
            LanguagesText = languages ?? string.Empty,
            SkillsText = skills ?? string.Empty,
            WantToText = wantTo ?? string.Empty,
            SkillsMask = 0,
            WantToMask = 0,
        });
    }

    /// <summary>Sends a friendship offer to another avatar.</summary>
    public void OfferFriendship(Guid agentId)
    {
        if (agentId == Guid.Empty || !_client.Network.Connected) return;
        _client.Friends.OfferFriendship(new UUID(agentId));
    }

    /// <summary>Offers the target avatar a teleport to our current location (a "lure").</summary>
    public void OfferTeleport(Guid agentId, string message = "Join me at my location.")
    {
        if (agentId == Guid.Empty || !_client.Network.Connected) return;
        _client.Self.SendTeleportLure(new UUID(agentId), message);
    }

    /// <summary>Pays L$ to another avatar. No-op for a non-positive amount.</summary>
    public void PayAvatar(Guid agentId, int amount)
    {
        if (agentId == Guid.Empty || amount <= 0 || !_client.Network.Connected) return;
        _client.Self.GiveAvatarMoney(new UUID(agentId), amount);
    }

    /// <summary>Asks the sim to (re)send the account mute list, so <see cref="IsAvatarMuted"/>
    /// reflects reality. Cheap; safe to call once after login.</summary>
    public void RequestMuteList()
    {
        if (_client.Network.Connected) _client.Self.RequestMuteList();
    }

    /// <summary>Adds or removes a local mute-list entry for an avatar (the "Block" action). The
    /// mute list is per-account server state that LibreMetaverse round-trips.</summary>
    public void SetAvatarMuted(Guid agentId, string avatarName, bool muted)
    {
        if (agentId == Guid.Empty || !_client.Network.Connected) return;
        var id = new UUID(agentId);
        if (muted)
            _client.Self.UpdateMuteListEntry(MuteType.Resident, id, avatarName ?? string.Empty);
        else
            _client.Self.RemoveMuteListEntry(id, avatarName ?? string.Empty);
    }

    /// <summary>Whether an avatar is currently on the synced mute list. Best-effort: only as
    /// current as the last <see cref="RequestMuteList"/> / mute edit.</summary>
    public bool IsAvatarMuted(Guid agentId)
    {
        var id = new UUID(agentId);
        foreach (var entry in _client.Self.MuteList.Values)
            if (entry.ID == id) return true;
        return false;
    }

    private void OnAvatarPropertiesReply(object? sender, AvatarPropertiesReplyEventArgs e)
    {
        var p = e.Properties;
        AvatarPropertiesReceived?.Invoke(this, new AvatarPropertiesEvent(new AvatarProfileProperties(
            e.AvatarID.Guid,
            p.AboutText ?? string.Empty,
            p.FirstLifeText ?? string.Empty,
            p.ProfileImage.Guid,
            p.FirstLifeImage.Guid,
            p.Partner.Guid,
            p.BornOn ?? string.Empty,
            p.CharterMember ?? string.Empty,
            p.ProfileURL ?? string.Empty,
            p.AllowPublish,
            p.MaturePublish)));
    }

    private void OnAvatarInterestsReply(object? sender, AvatarInterestsReplyEventArgs e)
    {
        var i = e.Interests;
        AvatarInterestsReceived?.Invoke(this, new AvatarInterestsEvent(new AvatarProfileInterests(
            e.AvatarID.Guid,
            i.LanguagesText ?? string.Empty,
            i.SkillsText ?? string.Empty,
            i.WantToText ?? string.Empty)));
    }

    private void OnAvatarGroupsReply(object? sender, AvatarGroupsReplyEventArgs e)
    {
        var groups = new List<AvatarProfileGroup>();
        foreach (var g in e.Groups)
            groups.Add(new AvatarProfileGroup(g.GroupID.Guid, g.GroupName ?? string.Empty, g.GroupInsigniaID.Guid));
        AvatarGroupsReceived?.Invoke(this, new AvatarGroupsEvent(e.AvatarID.Guid, groups));
    }

    private void OnAvatarPicksReply(object? sender, AvatarPicksReplyEventArgs e)
    {
        var picks = new List<AvatarPickInfo>();
        foreach (var kvp in e.Picks)
            picks.Add(new AvatarPickInfo(kvp.Key.Guid, kvp.Value ?? string.Empty));
        AvatarPicksReceived?.Invoke(this, new AvatarPicksEvent(e.AvatarID.Guid, picks));
    }

    private void OnPickInfoReply(object? sender, PickInfoReplyEventArgs e)
    {
        var p = e.Pick;
        AvatarPickDetailReceived?.Invoke(this, new AvatarPickDetailEvent(new AvatarPickDetail(
            e.PickID.Guid,
            p.Name ?? string.Empty,
            p.Desc ?? string.Empty,
            p.SnapshotID.Guid,
            p.SimName ?? string.Empty,
            p.PosGlobal.X,
            p.PosGlobal.Y,
            p.PosGlobal.Z)));
    }

    private void OnAvatarClassifiedReply(object? sender, AvatarClassifiedReplyEventArgs e)
    {
        var ads = new List<AvatarClassifiedInfo>();
        foreach (var kvp in e.Classifieds)
            ads.Add(new AvatarClassifiedInfo(kvp.Key.Guid, kvp.Value ?? string.Empty));
        AvatarClassifiedsReceived?.Invoke(this, new AvatarClassifiedsEvent(e.AvatarID.Guid, ads));
    }

    private void OnScriptDialog(object? sender, ScriptDialogEventArgs e)
    {
        ScriptDialogReceived?.Invoke(this, new ScriptDialogEvent(
            e.ObjectID.Guid, e.ObjectName, e.OwnerID.Guid,
            $"{e.FirstName} {e.LastName}".Trim(),
            e.Message, e.Channel, e.ButtonLabels));
    }

    /// <summary>Answers an llDialog popup by sending the chosen button back over the proper
    /// ScriptDialogReply protocol path (M5-4) -- NOT Self.Chat on the channel, which is a
    /// separate internal helper for negative-channel gesture/debug chat, not dialog replies.</summary>
    public void ReplyToScriptDialog(Guid objectId, int channel, int buttonIndex, string buttonLabel)
    {
        if (_client.Network.Connected)
            _client.Self.ReplyToScriptDialog(channel, buttonIndex, buttonLabel, new UUID(objectId));
    }

    private void OnAvatarAppearance(object? sender, AvatarAppearanceEventArgs e)
    {
        // Report the local agent's parameter set from an INCOMING appearance. This check first
        // hung off AppearanceSet, which only fires when LibreMetaverse runs its own bake -- i.e.
        // exactly the path SendAppearance=false disables -- so it could never fire in the
        // configuration it was written to diagnose. This event arrives regardless.
        if (e.AvatarID == _client.Self.AgentID && !_visualParamsLogged)
        {
            _visualParamsLogged = true;
            LogVisualParamHealth();
        }

        var textures = new Dictionary<int, Guid>();
        if (e.FaceTextures != null)
        {
            for (int i = 0; i < e.FaceTextures.Length; i++)
            {
                var face = e.FaceTextures[i];
                if (face != null && face.TextureID != LibreMetaverse.UUID.Zero)
                {
                    textures[i] = face.TextureID.Guid;
                }
            }
        }

        // AvatarAppearanceEventArgs doesn't expose the packet's AppearanceHover field (see
        // AvatarAppearanceEvent's doc comment for why it matters), but LibreMetaverse's own
        // internal AvatarAppearanceHandler already parsed it into the cached Avatar object's
        // HoverHeight before raising this event -- same ObjectsAvatars cache, same linear-scan-
        // by-AgentID pattern already used for the AgentId-resolution fix (see the ObjectUpdate/
        // TerseObjectUpdate handlers above), just keyed by AvatarID here since that's all this
        // event carries (no LocalID).
        float hoverOffsetZ = 0f;
        foreach (var kv in e.Simulator.ObjectsAvatars)
        {
            if (kv.Value != null && kv.Value.ID == e.AvatarID)
            {
                hoverOffsetZ = kv.Value.HoverHeight.Z;
                break;
            }
        }

        AvatarAppearanceReceived?.Invoke(this, new AvatarAppearanceEvent(
            e.Simulator.Handle,
            e.AvatarID.Guid,
            e.VisualParams?.ToArray() ?? Array.Empty<byte>(),
            textures,
            hoverOffsetZ
        ));
    }

    /// <summary>Fired when LibreMetaverse's appearance workflow (server-side bake POST on SL, or
    /// client-side bake + AgentSetAppearance on OpenSim) completes for the LOCAL agent. The self
    /// avatar's baked textures are produced by OUR viewer, not pushed unprompted by the sim, so
    /// this is the authoritative moment the real bakes exist.
    ///
    /// On server-side-baking regions LibreMetaverse also re-raises Avatars.AvatarAppearance for us
    /// with the composited ids (AppearanceManager.cs line 2323), so <see cref="OnAvatarAppearance"/>
    /// would already cover that case. But the OpenSim client-side path only populates
    /// <c>Appearance.MyTextures</c> and does NOT re-raise AvatarAppearance for self -- whether we
    /// then see a self AvatarAppearance depends on the sim echoing one, which is grid-dependent.
    /// Reading MyTextures here and emitting it through the same neutral event closes that gap so
    /// the renderer picks up the real bakes regardless of grid. Redundant-but-identical on SSB.</summary>
    /// <summary>Reports the visual-parameter set LibreMetaverse holds for the local agent.
    /// Read-only: it sends nothing and changes nothing. See its call site for why it exists.</summary>
    private bool _visualParamsLogged;

    private void LogVisualParamHealth()
    {
        try
        {
            var vp = _client.Appearance.MyVisualParameters;
            if (vp == null || vp.Length == 0)
            {
                Console.Error.WriteLine("[VisualParams] LibreMetaverse holds NO visual parameters — " +
                    "an appearance send would have replaced the stored shape with defaults");
                return;
            }

            int zero = 0, mid = 0;
            foreach (var b in vp)
            {
                if (b == 0) zero++;
                else if (b == 128) mid++;
            }

            // 218 is the modern parameter count; a much shorter array means an incomplete set.
            // All-zero or all-128 is the tell-tale of a never-populated (default) array rather
            // than a real shape.
            Console.Error.WriteLine($"[VisualParams] {vp.Length} params, {zero} zero, {mid} at 128 " +
                $"(mid), first 12: {string.Join(",", vp.Take(12))}" +
                ((zero + mid == vp.Length) ? "  <-- ALL DEFAULT: sending this would flatten the avatar" : ""));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[VisualParams] could not be inspected: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OnAppearanceSet(object? sender, AppearanceSetEventArgs e)
    {
        if (!e.Success) return;

        // Risk-free diagnostic for the avatar-corruption incident (2026-08-02). SendAppearance is
        // OFF, so nothing here is transmitted -- this only records what LibreMetaverse WOULD have
        // sent as AgentSetAppearance, which is what silently overwrote the user's stored shape.
        //
        // The suspicion is the visual parameters: if LMV holds an empty or default-filled array
        // rather than the values the simulator sent us, then every appearance send replaces a real
        // shape with a default one -- which is exactly what "avatar suddenly squat and deformed,
        // in Firestorm too" looks like. A rebake feature must not be built until this reads sane.
        LogVisualParamHealth();

        var te = _client.Appearance.MyTextures;
        var faces = te?.FaceTextures;
        if (faces == null) return;

        // Mirror OnAvatarAppearance: key by face index, drop empty slots. Also drop the generic
        // DEFAULT_AVATAR_TEXTURE that MyTextures carries for never-baked slots, so we only ever
        // emit genuinely-composited bakes and never regress a real bake to the default skin.
        var textures = new Dictionary<int, Guid>();
        for (int i = 0; i < faces.Length; i++)
        {
            var face = faces[i];
            if (face != null
                && face.TextureID != LibreMetaverse.UUID.Zero
                && face.TextureID != AppearanceManager.DEFAULT_AVATAR_TEXTURE)
            {
                textures[i] = face.TextureID.Guid;
            }
        }

        if (textures.Count == 0) return;

        RaiseAvatarAppearance(new AvatarAppearanceEvent(
            _client.Network.CurrentSim?.Handle ?? 0,
            _client.Self.AgentID.Guid,
            _client.Appearance.MyVisualParameters ?? Array.Empty<byte>(),
            textures));
    }

    private void OnAvatarAnimation(object? sender, LibreMetaverse.AvatarAnimationEventArgs e)
    {
        var animIds = new List<Guid>(e.Animations.Count);
        foreach (var anim in e.Animations)
        {
            animIds.Add(anim.AnimationID.Guid);
        }

        AvatarAnimationReceived?.Invoke(this, new AvatarAnimationEvent(
            e.AvatarID.Guid,
            animIds
        ));
    }

    private void OnObjectUpdate(object? sender, PrimEventArgs e) => RaiseObjectUpdate(e.Simulator, e.Prim, isFullUpdate: true);

    /// <summary>
    /// Puts back the particle system LibreMetaverse drops from every <c>ObjectUpdateCompressed</c>
    /// object -- see <see cref="CompressedParticleRepair"/> for what it gets wrong and why the
    /// failure is silent. Runs on a network thread, like every other LibreMetaverse handler.
    /// </summary>
    private void OnObjectUpdateCompressedRaw(object? sender, PacketReceivedEventArgs e)
    {
        if (e.Packet is not ObjectUpdateCompressedPacket packet)
        {
            return;
        }

        foreach (var block in packet.ObjectData)
        {
            byte[]? raw = CompressedParticleRepair.ExtractParticleBlock(block.Data);
            if (raw is null || !CompressedParticleRepair.TryReadLocalId(block.Data, out uint localId))
            {
                continue;
            }

            // The object LibreMetaverse has just finished decoding. If it is not there, this
            // callback beat the library's own handler and there is nothing to correct yet -- the
            // next update carries the same block.
            if (!e.Simulator.ObjectsPrimitives.TryGetValue(localId, out Primitive? prim) || prim is null)
            {
                continue;
            }

            var repaired = new Primitive.ParticleSystem(raw, 0);
            if (prim.ParticleSys.Equals(repaired))
            {
                // Already correct: an object whose particles have not changed sends the same block
                // on every compressed update, and re-raising each one would double the work of
                // every moving emitter in the region.
                continue;
            }

            prim.ParticleSys = repaired;
            RaiseObjectUpdate(e.Simulator, prim, isFullUpdate: true);
        }
    }

    /// <summary>ImprovedTerseObjectUpdate -- the lightweight packet a physically-moving object
    /// (falling, rolling, pushed) streams position/rotation/velocity through while it's actually
    /// in motion. Without this subscription, only full ObjectUpdate packets reach our pipeline,
    /// which the sim sends on state changes (select, flag/property edits) but NOT continuously
    /// while an object is just physically moving -- so a physical object's position only ever
    /// visibly updated here when something else incidentally forced a full resync, never smoothly
    /// while actually falling/rolling.</summary>
    private void OnTerseObjectUpdate(object? sender, TerseObjectUpdateEventArgs e)
    {
        if (e.Update.Avatar || e.Prim is Avatar)
        {
            Guid agentId = e.Prim.ID.Guid;
            string firstName = (e.Prim as Avatar)?.FirstName ?? "";
            string lastName = (e.Prim as Avatar)?.LastName ?? "";

            if (e.Simulator.ObjectsAvatars.TryGetValue(e.Prim.LocalID, out var knownAv) && knownAv != null)
            {
                if (agentId == Guid.Empty) agentId = knownAv.ID.Guid;
                if (string.IsNullOrEmpty(firstName)) firstName = knownAv.FirstName;
                if (string.IsNullOrEmpty(lastName)) lastName = knownAv.LastName;
            }

            bool isLocalAgent = agentId == _client.Self.AgentID.Guid || e.Prim.LocalID == _client.Self.LocalID;
            // Position/Rotation/Velocity come from e.Update (the freshly-decoded
            // ObjectMovementUpdate for THIS packet), never from e.Prim: LibreMetaverse's
            // ImprovedTerseObjectUpdateHandler fires this event via ThreadPool.QueueUserWorkItem
            // BEFORE it writes the decoded values onto the shared, cached e.Prim object ("Fire the
            // pre-emptive notice (before we stomp the object)" -- ObjectManager.PacketHandlers.cs
            // ~line 619). Reading e.Prim here races that later write; under load (many queued
            // avatar updates while walking) the handler can run before or after the stomp, so
            // e.Prim.Velocity is sometimes last packet's value or zero. Since ExtrapolateMovement
            // dead-reckons Position purely from Velocity between packets, a stale/zero read here
            // silently killed the extrapolation for that interval -- the avatar would sit still
            // until the next (correct) packet snapped it forward, reading as juddery/stuttering
            // motion. e.Update is race-free: it's the packet's own decoded struct, not a shared
            // mutable cache.
            //
            // e.Prim.ParentID (MVP2-1 seat lookup) does NOT race that write: ImprovedTerseObjectUpdate
            // never carries ParentID at all (only a full ObjectUpdate changes it), so unlike
            // Position/Rotation/Velocity above, the cached e.Prim's ParentID is always current here.
            ResolveSeatedTransform(e.Simulator, e.Update.Position, e.Update.Rotation, e.Prim.ParentID,
                out var worldPos, out var worldRot);

            // From e.Update, not e.Prim -- the same race the comment above describes. The terse
            // update is where the plane actually moves: it rides every avatar movement packet,
            // which is how the viewer keeps foot placement current while walking.
            var supportPlane = ToSupportPlane(e.Update.CollisionPlane);

            AvatarUpdateReceived?.Invoke(this, new AvatarUpdateEvent(
                e.Simulator.Handle,
                e.Prim.LocalID,
                agentId,
                new System.Numerics.Vector3(worldPos.X, worldPos.Y, worldPos.Z),
                new System.Numerics.Quaternion(worldRot.X, worldRot.Y, worldRot.Z, worldRot.W),
                firstName,
                lastName,
                isLocalAgent,
                e.Prim.Scale.Z,
                new System.Numerics.Vector3(e.Update.Velocity.X, e.Update.Velocity.Y, e.Update.Velocity.Z),
                e.TimeDilation / 65535.0f,
                e.Prim.ParentID,
                supportPlane));
            return;
        }

        // Position/Rotation/Velocity from e.Update, not e.Prim -- same race as the avatar branch
        // above (LibreMetaverse fires this event before stomping the shared cached Primitive).
        RaiseObjectUpdate(
            e.Simulator, e.Prim, isFullUpdate: false,
            positionOverride: new System.Numerics.Vector3(e.Update.Position.X, e.Update.Position.Y, e.Update.Position.Z),
            rotationOverride: new System.Numerics.Quaternion(e.Update.Rotation.X, e.Update.Rotation.Y, e.Update.Rotation.Z, e.Update.Rotation.W),
            velocityOverride: new System.Numerics.Vector3(e.Update.Velocity.X, e.Update.Velocity.Y, e.Update.Velocity.Z),
            timeDilation: e.TimeDilation / 65535.0f);
    }

    /// <summary>Builds and raises an ObjectUpdateEvent from a LibreMetaverse Primitive.
    /// <paramref name="positionOverride"/>, <paramref name="rotationOverride"/> and
    /// <paramref name="velocityOverride"/>, when set, are used instead of the same-named field on
    /// <paramref name="prim"/> -- required for a terse-sourced call (see OnTerseObjectUpdate):
    /// LibreMetaverse's ImprovedTerseObjectUpdateHandler fires its event via
    /// ThreadPool.QueueUserWorkItem BEFORE writing the decoded values onto the shared, cached
    /// Primitive ("fire the pre-emptive notice before we stomp the object" --
    /// ObjectManager.PacketHandlers.cs ~line 619), so reading prim.Position/Rotation/Velocity
    /// directly races that later write (confirmed root cause of the identical avatar-side bug fixed
    /// in OnTerseObjectUpdate's avatar branch). The full ObjectUpdate path has no such race --
    /// ObjectUpdateHandler stomps the Primitive synchronously before queuing its event (same file,
    /// ~line 371-385) -- so OnObjectUpdate's call leaves these null and reads straight off prim.
    /// Every other field (mesh, texture, flags, ...) always reads off prim regardless: terse updates
    /// don't carry them on the wire at all, so prim already holds the last full update's values
    /// either way, override or not.</summary>
    private void RaiseObjectUpdate(
        LibreMetaverse.Simulator simulator, Primitive prim, bool isFullUpdate,
        System.Numerics.Vector3? positionOverride = null,
        System.Numerics.Quaternion? rotationOverride = null,
        System.Numerics.Vector3? velocityOverride = null,
        float timeDilation = 1f)
    {
        var resolvedPosition = positionOverride ?? new System.Numerics.Vector3(prim.Position.X, prim.Position.Y, prim.Position.Z);
        var resolvedRotation = rotationOverride ?? new System.Numerics.Quaternion(prim.Rotation.X, prim.Rotation.Y, prim.Rotation.Z, prim.Rotation.W);
        var resolvedVelocity = velocityOverride ?? new System.Numerics.Vector3(prim.Velocity.X, prim.Velocity.Y, prim.Velocity.Z);

        bool isMesh = false;
        Guid meshId = Guid.Empty;
        bool isSculpt = false;
        Guid sculptId = Guid.Empty;
        byte sculptType = 0;

        if (prim.Sculpt != null && prim.Sculpt.SculptTexture != LibreMetaverse.UUID.Zero)
        {
            if (prim.Sculpt.Type == LibreMetaverse.SculptType.Mesh)
            {
                isMesh = true;
                meshId = prim.Sculpt.SculptTexture.Guid;
            }
            else
            {
                // ANY sculpt block with a map is a sculpt, INCLUDING stitching type None (0).
                //
                // This used to require `Type != None`, which silently demoted such a prim to its
                // underlying profile/path curve -- and since sculpties keep whatever base shape
                // they were built from, that came out as a smooth torus or sphere sitting where
                // the real object should be. Measured on OSGrid, The Dangazi Forest 2026-08-23: a
                // reef rock at <163.76, 197.57, 18.41> rendered here as a featureless 26x7x71
                // ellipse, while Firestorm's own build floater reported it as "Geformt"
                // (sculpted), stitching "Plane/None", Invert set -- i.e. a type byte of 0x40,
                // whose low three bits are zero.
                //
                // The viewer decides this on PRESENCE OF THE BLOCK, not on the stitching value:
                //     bool LLVOVolume::isSculpted() const
                //     { if (getSculptParams()) return true; return false; }   (llvovolume.cpp:3633)
                // and that predicate is what gates the sculpt texture fetch and the sculpted
                // rendering path. (LLVolumeParams::isSculpt(), which DOES test
                // `(mSculptType & MASK) != NONE`, is a different predicate used elsewhere -- it
                // was the one that made this look correct when the condition was written.)
                //
                // Stitching 0 then behaves exactly like PLANE when the map is wrapped:
                // sculptGenerateMapVertices special-cases only SPHERE (pole pinch), TORUS (T wrap)
                // and CYLINDER (S wrap), so anything else clamps to the map's edges
                // (llvolume.cpp:3072-3113). PrimMeshService.GenerateSculpt already matches that --
                // its `_ => plane` default covers 0 -- so passing the byte through is all that is
                // needed here.
                isSculpt = true;
                sculptId = prim.Sculpt.SculptTexture.Guid;
                // The SL sculpt-type byte packs the base type (low 3 bits) with two render flags:
                // Invert (0x40, render inside-out) and Mirror (0x80, mirror on X). LibreMetaverse's
                // prim.Sculpt.Type PROPERTY masks those flags off (& 7), so reading it alone silently
                // dropped them — a sculpt authored inverted/mirrored (very common for organic sculpts
                // like trees) was then built with the wrong winding/handedness: internally clean
                // geometry (no NaN, no spikes) but wrapped wrong, so it rendered "disintegrated".
                // Re-pack the flags so the whole byte reaches the mesher (SculptData.Type's setter
                // stores it verbatim, and its Invert/Mirror getters read the flag bits back).
                sculptType = (byte)((byte)prim.Sculpt.Type
                    | (prim.Sculpt.Invert ? (byte)LibreMetaverse.SculptType.Invert : 0)
                    | (prim.Sculpt.Mirror ? (byte)LibreMetaverse.SculptType.Mirror : 0));
            }
        }

        Guid textureId = Guid.Empty;
        Guid renderMaterialId = Guid.Empty;
        Guid legacyMaterialId = Guid.Empty;
        System.Numerics.Vector4 colorTint = new System.Numerics.Vector4(1, 1, 1, 1);

        var defaultFace = prim.Textures?.DefaultTexture;
        if (defaultFace != null)
        {
            textureId = defaultFace.TextureID.Guid;
            renderMaterialId = defaultFace.RenderMaterialID.Guid;
            legacyMaterialId = defaultFace.MaterialID.Guid;
            colorTint = new System.Numerics.Vector4(defaultFace.RGBA.R, defaultFace.RGBA.G, defaultFace.RGBA.B, defaultFace.RGBA.A);
        }

        // Per-face textures: each prim face can have its own texture/colour. Resolve each face
        // (its own entry, or the default) to a neutral FaceTexture indexed by face number.
        FaceTexture[]? faces = null;
        var faceArr = prim.Textures?.FaceTextures;
        if (faceArr != null && faceArr.Length > 0 && defaultFace != null)
        {
            // Each face carries TWO material ids: RenderMaterialID (glTF PBR) and MaterialID
            // (legacy Blinn-Phong -- normal + specular map). They are separate systems and a face
            // can have either, both or neither. Only the glTF one was read until FEAT-RENDER-04,
            // so a face whose detail lives in its normal/specular maps rendered as nothing but its
            // bare diffuse texture.
            faces = new FaceTexture[faceArr.Length];
            for (int i = 0; i < faceArr.Length; i++)
            {
                var f = faceArr[i] ?? defaultFace;
                faces[i] = new FaceTexture(
                    f.TextureID.Guid,
                    f.RenderMaterialID.Guid,
                    f.MaterialID.Guid,
                    new System.Numerics.Vector4(f.RGBA.R, f.RGBA.G, f.RGBA.B, f.RGBA.A),
                    f.RepeatU,
                    f.RepeatV,
                    f.OffsetU,
                    f.OffsetV,
                    f.Rotation,
                    (byte)f.TexMapType,
                    f.Fullbright);
            }
        }

        // Convert the prim's construction data to a neutral PrimShape so the asset layer can
        // regenerate real geometry without seeing a LibreMetaverse type.
        //
        // IMPORTANT: pd.profileCurve (raw field, lowercase) is a single packed byte carrying BOTH
        // the profile curve type (Circle/Square/Triangle/... in the low nibble, 0x00-0x05) AND the
        // hollow-cut's own shape (HoleType Same/Circle/Square/Triangle, pre-shifted into the high
        // nibble as 0x00/0x10/0x20/0x30 — see LibreMetaverse.Types.EnumsPrimitive). pd.ProfileCurve
        // (the PROPERTY, capital P) masks that byte down to just the low nibble
        // (`profileCurve & PROFILE_MASK`), silently discarding the hole-shape bits. Using the
        // property here (as this line previously did) meant every hollow prim's hole shape got
        // zeroed out end-to-end -- reconstructed as HoleType.Same regardless of what the creator
        // actually chose, which is only coincidentally correct when "Same" was already picked.
        // Passing the raw packed byte through lets PrimMeshService.Generate() assign it straight
        // back onto ConstructionData.profileCurve (also the raw field) and get BOTH nibbles right.
        var pd = prim.PrimData;
        var shape = new PrimShape(
            pd.profileCurve,
            (byte)pd.PathCurve,
            pd.PathBegin, pd.PathEnd,
            pd.PathScaleX, pd.PathScaleY,
            pd.PathShearX, pd.PathShearY,
            pd.PathTaperX, pd.PathTaperY,
            pd.PathTwist, pd.PathTwistBegin,
            pd.PathRadiusOffset, pd.PathSkew, pd.PathRevolutions,
            pd.ProfileBegin, pd.ProfileEnd, pd.ProfileHollow,
            (byte)pd.PCode);

        // Primitive.Light never resets itself when a light is disabled (see
        // _lightPresentByLocalId) -- if our own raw-packet scan positively saw this update's
        // ExtraParams WITHOUT a Light block, trust that over the stale Primitive.Light, and
        // correct the shared Primitive object too so any other code reading prim.Light directly
        // (not just this event) also sees the fix from here on.
        // llSetTextureAnim. LibreMetaverse copies the four wire bytes verbatim into
        // Primitive.TextureAnim without the viewer's unpack rules (signed face byte, non-smooth
        // size clamp), so the raw values go through TextureAnimation.FromWire rather than being
        // read off the struct field by field. Unlike Primitive.Light this one does NOT latch:
        // ObjectUpdateHandler reassigns prim.TextureAnim unconditionally on every full update, so
        // an animation switched off really does come back as ANIM_OFF here.
        SLNG.Core.TextureAnimation? textureAnim = null;
        if ((prim.TextureAnim.Flags & Primitive.TextureAnimMode.ANIM_ON) != 0)
        {
            textureAnim = SLNG.Core.TextureAnimation.FromWire(
                (byte)prim.TextureAnim.Flags,
                (byte)prim.TextureAnim.Face,
                (byte)prim.TextureAnim.SizeX,
                (byte)prim.TextureAnim.SizeY,
                prim.TextureAnim.Start,
                prim.TextureAnim.Length,
                prim.TextureAnim.Rate);
        }

        bool lightEnabled = prim.Light.Intensity > 0f;
        if (_lightPresentByLocalId.TryGetValue(prim.LocalID, out bool lightBlockPresent) && !lightBlockPresent && lightEnabled)
        {
            prim.Light = new Primitive.LightData();
            lightEnabled = false;
        }
        ObjectUpdateReceived?.Invoke(this, new ObjectUpdateEvent(
            simulator.Handle,
            prim.LocalID,
            resolvedPosition,
            resolvedRotation,
            new System.Numerics.Vector3(prim.Scale.X, prim.Scale.Y, prim.Scale.Z),
            (byte)prim.PrimData.ProfileCurve,
            isMesh,
            meshId,
            textureId,
            renderMaterialId,
            colorTint,
            defaultFace?.RepeatU ?? 1.0f,
            defaultFace?.RepeatV ?? 1.0f,
            defaultFace?.OffsetU ?? 0.0f,
            defaultFace?.OffsetV ?? 0.0f,
            defaultFace?.Rotation ?? 0.0f,
            prim.ParentID,
            (byte)prim.PrimData.AttachmentPoint,
            shape,
            isSculpt, sculptId, sculptType,
            faces,
            prim.ID.Guid,
            prim.Flags.HasFlag(PrimFlags.Physics),
            prim.Flags.HasFlag(PrimFlags.Temporary),
            prim.Flags.HasFlag(PrimFlags.Phantom),
            prim.Flags.HasFlag(PrimFlags.CastShadows),
            // Light is an ExtraParams block, not a PrimFlags bit -- LibreMetaverse's own
            // convention (mirrored in SetObjectLight below) is Intensity>0 means "the block is
            // active"; Primitive.Light is never null (ObjectManager always constructs a default).
            // lightEnabled (computed above) is prim.Light.Intensity>0f corrected for the
            // never-resets-on-disable bug -- prim.Light itself is also corrected by then, so the
            // fields below are consistent with it either way.
            lightEnabled,
            new System.Numerics.Vector3(prim.Light.Color.R, prim.Light.Color.G, prim.Light.Color.B),
            prim.Light.Intensity,
            prim.Light.Radius,
            prim.Light.Falloff,
            // OpenMetaverse.Material and SLNG.Core.PrimMaterial share the same 0-6 numeric values
            // by design (see PrimMaterial's doc comment) -- the enum's rare/vestigial value 7
            // ("Light", unrelated to the point-light feature, not user-selectable in the real SL
            // viewer either) has no matching PrimMaterial member; clamp it to Wood rather than
            // let an unnamed enum value reach the UI.
            (byte)prim.PrimData.Material <= 6 ? (SLNG.Core.PrimMaterial)(byte)prim.PrimData.Material : SLNG.Core.PrimMaterial.Wood,
            (byte)prim.ClickAction,
            isFullUpdate,
            resolvedVelocity,
            timeDilation,
            // The DEFAULT face's texgen. LibreMetaverse's MappingType is already the raw SL
            // value (Default=0, Planar=2, ...), and FaceTexture.TexGen stores it unconverted,
            // so this is a straight cast -- see FaceTexture.TexGen on why it is NOT 1.
            defaultFace != null ? (byte)defaultFace.TexMapType : FaceTexture.TexGenDefault,
            textureAnim,
            legacyMaterialId,
            ParticleSystemConverter.FromWire(prim.ParticleSys),
            // The DEFAULT face's fullbright flag -- see FaceTexture.Fullbright. Per-face entries
            // in `faces` carry their own; this is for prims that send no per-face entries.
            defaultFace?.Fullbright ?? false));
    }

    private void OnKillObject(object? sender, KillObjectEventArgs e)
    {
        ObjectRemovedReceived?.Invoke(this, new ObjectRemovedEvent(e.Simulator.Handle, e.ObjectLocalID));
    }

    private void OnKillObjects(object? sender, KillObjectsEventArgs e)
    {
        foreach (var localId in e.ObjectLocalIDs)
        {
            ObjectRemovedReceived?.Invoke(this, new ObjectRemovedEvent(e.Simulator.Handle, localId));
        }
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

    private void OnSimDisconnected(object? sender, SimDisconnectedEventArgs e)
    {
        RegionDisconnectedReceived?.Invoke(this, new RegionDisconnectedEvent(e.Simulator.Handle));
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

    /// <summary>
    /// Attempts to log in to the grid described by <paramref name="credentials"/>.
    /// Uses LibreMetaverse's async login API; failures (including unreachable grids)
    /// are returned as a failed <see cref="LoginResult"/> rather than thrown.
    /// </summary>
    public async Task<LoginResult> LoginAsync(LoginCredentials credentials, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);

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
    /// <summary>Logs out if connected. Safe to call when already disconnected.</summary>
    public void Logout()
    {
        if (_client.Network.Connected)
        {
            _client.Network.Logout();
        }
    }

    /// <summary>Sends a local chat message.</summary>
    public void SendChat(string message, int channel = 0, ChatType type = ChatType.Normal)
    {
        if (_client.Network.Connected)
        {
            _client.Self.Chat(message, channel, type);
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

        string lastMessage = string.Empty;
        void OnProgress(object? sender, TeleportEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.Message))
                lastMessage = e.Message;
        }

        _client.Self.TeleportProgress += OnProgress;
        try
        {
            // Attempt 1: Direct landmark teleport request
            bool success = await _client.Self
                .TeleportAsync(new UUID(landmarkAssetId), ct)
                .ConfigureAwait(false);

            string msg = !string.IsNullOrWhiteSpace(lastMessage) ? lastMessage : _client.Self.TeleportMessage;
            if (success)
            {
                SyncLocalAgentPositionAfterTeleport();
                return new TeleportResult(true, msg);
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

                        string fallbackMsg = !string.IsNullOrWhiteSpace(lastMessage) ? lastMessage : _client.Self.TeleportMessage;
                        return new TeleportResult(fallbackSuccess, fallbackSuccess ? string.Empty : fallbackMsg);
                    }
                }
            }

            return new TeleportResult(false, msg);
        }
        catch (Exception ex)
        {
            return new TeleportResult(false, ex.Message);
        }
        finally
        {
            _client.Self.TeleportProgress -= OnProgress;
        }
    }

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
            SittingOnLocalId: _client.Self.SittingOn));
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

    /// <summary>Touches (clicks) an object — the SL grab/de-grab pair
    /// <see cref="ObjectManager.ClickObjectAsync"/> sends 50ms apart, which is what fires
    /// touch_start/touch_end on any touch script the object carries. <paramref name="localId"/>
    /// is the SL scene-local id (<c>Entity.LocalId</c>), not the persistent asset/object UUID.
    /// Surface hit details are optional (all-zero if omitted, like LibreMetaverse's own
    /// no-detail overload) — a HUD button script rarely inspects them, but pass real ones (face
    /// index, hit position/normal) when available for scripts that do.</summary>
    public async System.Threading.Tasks.Task ClickObjectAsync(
        uint localId,
        int faceIndex = 0,
        System.Numerics.Vector3 position = default,
        System.Numerics.Vector3 normal = default,
        System.Numerics.Vector3 uvCoord = default,
        System.Numerics.Vector3 stCoord = default,
        System.Numerics.Vector3 binormal = default)
    {
        var sim = _client.Network.CurrentSim;
        if (sim == null) return;

        await _client.Objects.ClickObjectAsync(
            sim, localId,
            ToOmv(uvCoord), ToOmv(stCoord), faceIndex,
            ToOmv(position), ToOmv(normal), ToOmv(binormal));
    }

    private static LibreMetaverse.Vector3 ToOmv(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>MVP2-1: requests to sit on the object identified by its scene-local id. Sends
    /// the same AgentRequestSit + AgentSit pair the real viewer sends (LMV's own examples send
    /// both back-to-back with no wait) -- against OpenSim the second call is server-side
    /// redundant (SendSitResponse already seats the avatar), but real SL requires the client's
    /// own AgentSit to actually complete the sit. Fire-and-forget like SelectObject: LMV's
    /// RequestSit/Sit are synchronous, and any failure (target out of SitActiveRange, wrong
    /// distance -- see ScenePresence.SendSitResponse) is silent on the wire, so there is nothing
    /// meaningful to await or return here. A no-op if the local id doesn't resolve to a
    /// currently-known primitive.</summary>
    public void RequestSit(uint localId)
    {
        var sim = _client.Network.CurrentSim;
        if (sim == null || !_client.Network.Connected) return;
        if (!sim.ObjectsPrimitives.TryGetValue(localId, out var prim) || prim == null) return;

        _client.Self.RequestSit(prim.ID, LibreMetaverse.Vector3.Zero);
        _client.Self.Sit();
    }

    /// <summary>MVP2-1: sits on the ground at the avatar's current position (no target object) --
    /// the SL "Sit on Ground" action. Sets AGENT_CONTROL_SIT_ON_GROUND, which — unlike a prim
    /// sit — the sim never reports back as a ParentID change (OpenSim tracks it in a separate
    /// SitGround field), so <see cref="AvatarUpdateEvent.SittingOnLocalId"/> stays 0 for a ground
    /// sit; the animation is the only client-visible signal.</summary>
    public void SitOnGround() => _client.Self.SitOnGround();

    /// <summary>MVP2-1: stands up from a prim sit or a ground sit alike. Returns false (and logs
    /// a warning inside LibreMetaverse) only if agent updates are disabled entirely, which SLNG
    /// never does -- included for completeness rather than swallowed, matching LMV's own
    /// signature.</summary>
    public bool Stand() => _client.Self.Stand();
    public void StopAnimation(Guid animId) => _client.Self.AnimationStop(new UUID(animId), true);

    /// <summary>Root folder id of the agent's own inventory, or null until login has completed
    /// (LibreMetaverse builds the store — folders only, no items — from the login response's
    /// inventory skeleton; there is no way to opt out and nothing extra to request).</summary>
    public Guid? InventoryRootId => _client.Inventory.Store?.RootFolder?.UUID.Guid;

    /// <summary>Root folder id of the grid-provided Library tree, or null until login (or if the
    /// grid has no library).</summary>
    public Guid? LibraryRootId => _client.Inventory.Store?.LibraryFolder?.UUID.Guid;

    /// <summary>Folder id of the Trash folder, or null until login.</summary>
    public Guid? TrashFolderId => _client.Inventory.FindFolderForType(FolderType.Trash).Guid;

    /// <summary>Folder id of the Landmarks system folder, or null until login. Falls back to
    /// the inventory root (LibreMetaverse's own FindFolderForType behavior) if the grid never
    /// sent one — same fallback shape as <see cref="TrashFolderId"/>.</summary>
    public Guid? LandmarksFolderId => _client.Inventory.FindFolderForType(FolderType.Landmark).Guid;

    /// <summary>Folder id of the Current Outfit system folder (COF), or null until login. Its
    /// children are LINK items pointing at whatever's actually worn/attached right now — the
    /// same folder Firestorm's "Worn Items" tab reads, and the fastest way to identify a worn
    /// attachment by name without a dedicated UI (browse to it in the existing inventory tree).</summary>
    public Guid? CurrentOutfitFolderId => _client.Inventory.FindFolderForType(FolderType.CurrentOutfit).Guid;

    /// <summary>Checks if a folder is the Landmarks system folder or any descendant subfolder of it.</summary>
    public bool IsInLandmarksSubtree(Guid folderId)
    {
        var store = _client.Inventory.Store;
        if (store == null) return false;
        var landmarkFolderUuid = _client.Inventory.FindFolderForType(FolderType.Landmark);
        if (landmarkFolderUuid == UUID.Zero) return false;

        var folderUuid = new LibreMetaverse.UUID(folderId);
        if (folderUuid == landmarkFolderUuid) return true;

        for (var n = store.GetNodeOrDefault(folderUuid); n != null; n = n.Parent)
        {
            if (n.Data?.UUID == landmarkFolderUuid) return true;
        }
        return false;
    }

    /// <summary>
    /// Fetches one folder's direct children (subfolders + items) — the lazy per-folder expansion
    /// unit for an inventory UI. One CAPS request (FetchInventoryDescendents2 — supported by
    /// modern OpenSim; AIS3 is SL-only and mutation-only in LibreMetaverse anyway) per call, no
    /// recursion: recursing the whole tree hammers the grid and is never needed for a browser.
    /// Returns an empty list before login or when the fetch fails (LibreMetaverse's
    /// FolderContentsAsync falls back to its own cache on failure rather than surfacing an
    /// error — acceptable for a UI tree, where the user just re-expands).
    /// </summary>
    public async Task<IReadOnlyList<InventoryEntry>> FetchInventoryChildrenAsync(
        Guid folderId, CancellationToken ct = default)
    {
        var store = _client.Inventory.Store;
        if (store == null) return Array.Empty<InventoryEntry>();

        var folderUuid = new LibreMetaverse.UUID(folderId);
        // Library folders are owned by the library owner, not the agent — but do NOT trust the
        // stored node's own OwnerID for this: LibreMetaverse's descendents-reply parser creates
        // folders with OwnerID unset (UUID.Zero) — only login-SKELETON folders carry an owner.
        // Passing Zero as the owner makes OpenSim return nothing, which rendered every folder
        // below the root as "(empty)" on the first live test. Membership in the Library subtree
        // (walk the store's parent chain) is the reliable signal.
        var owner = _client.Self.AgentID;
        var libraryRoot = store.LibraryFolder;
        if (libraryRoot != null)
        {
            for (var n = store.GetNodeOrDefault(folderUuid); n != null; n = n.Parent)
            {
                if (n.Data?.UUID != libraryRoot.UUID) continue;
                owner = libraryRoot.OwnerID;
                break;
            }
        }

        var contents = await _client.Inventory.FolderContentsAsync(
            folderUuid, owner, fetchFolders: true, fetchItems: true,
            LibreMetaverse.InventorySortOrder.ByName, ct).ConfigureAwait(false);
        if (contents == null) return Array.Empty<InventoryEntry>();

        bool isLandmarksFolder = IsInLandmarksSubtree(folderId);

        var result = new List<InventoryEntry>(contents.Count);
        foreach (var entry in contents)
        {
            switch (entry)
            {
                case LibreMetaverse.InventoryFolder f:
                    result.Add(new InventoryEntry(
                        f.UUID.Guid, f.ParentUUID.Guid, f.OwnerID.Guid, f.Name,
                        IsFolder: true, (int)f.PreferredType,
                        AssetId: Guid.Empty, AssetType: -1, InventoryType: -1,
                        IsLink: false, LinkTargetId: Guid.Empty,
                        CanCopy: true, CanModify: true, CanTransfer: true));
                    break;
                case LibreMetaverse.InventoryItem i:
                    var owned = i.Permissions.OwnerMask;
                    int assetType = (int)i.AssetType;
                    if (assetType <= 0 && (i.InventoryType == LibreMetaverse.InventoryType.Landmark || i is LibreMetaverse.InventoryLandmark || isLandmarksFolder))
                    {
                        assetType = (int)LibreMetaverse.AssetType.Landmark;
                    }
                    result.Add(new InventoryEntry(
                        i.UUID.Guid, i.ParentUUID.Guid, i.OwnerID.Guid, i.Name,
                        IsFolder: false, PreferredFolderType: -1,
                        // For links the "asset" id actually points at the linked inventory item —
                        // report it as the link target and leave AssetId empty (resolving the
                        // target's real asset takes a second fetch the UI doesn't need yet).
                        AssetId: i.ResolvedAssetID.Guid,
                        assetType, (int)i.InventoryType,
                        i.IsLink(), i.IsLink() ? i.ResolvedItemID.Guid : Guid.Empty,
                        owned.HasFlag(LibreMetaverse.PermissionMask.Copy),
                        owned.HasFlag(LibreMetaverse.PermissionMask.Modify),
                        owned.HasFlag(LibreMetaverse.PermissionMask.Transfer)));
                    break;
            }
        }

        // Deduplicate entries under Current Outfit (COF) if multiple links/items point to the same target
        if (folderUuid == _client.Inventory.FindFolderForType(LibreMetaverse.FolderType.CurrentOutfit))
        {
            var seenTargets = new HashSet<Guid>();
            var deduped = new List<InventoryEntry>(result.Count);
            foreach (var item in result)
            {
                var target = item.IsLink ? item.LinkTargetId : item.Id;
                if (target != Guid.Empty && !seenTargets.Add(target))
                {
                    continue; // Skip duplicate link/item pointing to same target
                }
                deduped.Add(item);
            }
            result = deduped;
        }

        return result;
    }

    /// <summary>
    /// Moves an inventory item (or folder) to the Trash folder.
    /// </summary>
    public Task MoveToTrashAsync(Guid itemId, bool isFolder)
    {
        if (TrashFolderId is not { } trashId) return Task.CompletedTask;

        if (isFolder)
            _client.Inventory.MoveFolder(new LibreMetaverse.UUID(itemId), new LibreMetaverse.UUID(trashId));
        else
            _client.Inventory.MoveItem(new LibreMetaverse.UUID(itemId), new LibreMetaverse.UUID(trashId));

        return Task.CompletedTask;
    }

    /// <summary>
    /// Gives an inventory item to another agent (IM inventory offer).
    /// </summary>
    public Task GiveItemAsync(Guid itemId, string itemName, int assetType, Guid recipientAgentId)
    {
        _client.Inventory.GiveItem(
            new LibreMetaverse.UUID(itemId),
            itemName,
            (LibreMetaverse.AssetType)assetType,
            new LibreMetaverse.UUID(recipientAgentId),
            doEffect: true);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gives an inventory folder to another agent (IM inventory offer).
    /// </summary>
    public Task GiveFolderAsync(Guid folderId, string folderName, Guid recipientAgentId)
    {
        _client.Inventory.GiveItem(
            new LibreMetaverse.UUID(folderId),
            folderName,
            LibreMetaverse.AssetType.Folder,
            new LibreMetaverse.UUID(recipientAgentId),
            doEffect: true);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Copies an inventory item to a new parent folder.
    /// </summary>
    public async Task CopyItemAsync(Guid itemId, Guid newParentId, string newName)
    {
        var itemUuid = new LibreMetaverse.UUID(itemId);
        var parentUuid = new LibreMetaverse.UUID(newParentId);

        await _client.Inventory.RequestCopyItemAsync(itemUuid, parentUuid, newName, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Attaches an inventory item (Object/HUD/Attachment) to the agent.
    /// Handles both real inventory item IDs and link IDs.
    /// </summary>
    public Task AttachItemAsync(Guid itemId, byte attachPoint = 0, bool replace = false)
    {
        var itemUuid = new LibreMetaverse.UUID(itemId);
        var store = _client.Inventory.Store;
        var itemNode = store?.GetNodeOrDefault(itemUuid);

        if (itemNode?.Data is LibreMetaverse.InventoryItem item && item.IsLink())
        {
            itemUuid = item.ResolvedItemID;
            itemNode = store?.GetNodeOrDefault(itemUuid);
        }

        if (itemNode?.Data is LibreMetaverse.InventoryItem realItem)
        {
            _client.Appearance.Attach(realItem, (LibreMetaverse.AttachmentPoint)attachPoint, replace);
        }
        else
        {
            _client.Appearance.Attach(
                itemUuid,
                _client.Self.AgentID,
                "Attachment",
                "",
                new LibreMetaverse.Permissions { OwnerMask = LibreMetaverse.PermissionMask.All },
                0,
                (LibreMetaverse.AttachmentPoint)attachPoint,
                replace);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Detaches an inventory item / attachment from the agent.
    /// Handles both real inventory item IDs and link IDs inside Current Outfit.
    /// </summary>
    public Task<DetachResult> DetachItemAsync(Guid itemId)
    {
        var itemUuid = new LibreMetaverse.UUID(itemId);
        var store = _client.Inventory.Store;
        var node = store?.GetNodeOrDefault(itemUuid);

        var uuidsToDetach = new HashSet<LibreMetaverse.UUID>();
        if (itemUuid != LibreMetaverse.UUID.Zero) uuidsToDetach.Add(itemUuid);

        if (node?.Data is LibreMetaverse.InventoryItem item && item.IsLink())
        {
            if (item.ResolvedItemID != LibreMetaverse.UUID.Zero) uuidsToDetach.Add(item.ResolvedItemID);
            if (item.AssetUUID != LibreMetaverse.UUID.Zero) uuidsToDetach.Add(item.AssetUUID);
        }

        // Search Current Outfit folder to find matching links or items
        var cofUuid = _client.Inventory.FindFolderForType(LibreMetaverse.FolderType.CurrentOutfit);
        if (cofUuid != LibreMetaverse.UUID.Zero)
        {
            var cofNode = store?.GetNodeOrDefault(cofUuid);
            if (cofNode != null)
            {
                foreach (var childNode in cofNode.Nodes.Values)
                {
                    if (childNode.Data is LibreMetaverse.InventoryItem cofItem)
                    {
                        var linkId = cofItem.UUID;
                        var targetId = cofItem.IsLink() ? (cofItem.ResolvedItemID != LibreMetaverse.UUID.Zero ? cofItem.ResolvedItemID : cofItem.AssetUUID) : cofItem.UUID;

                        if (uuidsToDetach.Contains(linkId) || uuidsToDetach.Contains(targetId) || linkId == itemUuid || targetId == itemUuid)
                        {
                            if (linkId != LibreMetaverse.UUID.Zero) uuidsToDetach.Add(linkId);
                            if (targetId != LibreMetaverse.UUID.Zero) uuidsToDetach.Add(targetId);
                        }
                    }
                }
            }
        }

        // Query active attachments from AppearanceManager ONLY for matching candidate UUIDs.
        // wasAttached is the load-bearing bit: DetachAttachmentIntoInv is matched server-side
        // against LIVE attachments, so when nothing here is actually attached every packet below
        // is a silent no-op -- see the stale-link cleanup at the end of this method.
        bool wasAttached = false;
        try
        {
            var activeAtts = _client.Appearance.GetAttachmentsByItemId();
            foreach (var kvp in activeAtts)
            {
                if (uuidsToDetach.Contains(kvp.Key))
                {
                    uuidsToDetach.Add(kvp.Key);
                    wasAttached = true;
                }
            }
        }
        catch { }

        // Send DetachAttachmentIntoInv packet for every candidate UUID
        foreach (var u in uuidsToDetach)
        {
            if (u != LibreMetaverse.UUID.Zero)
            {
                _client.Appearance.Detach(u);
            }
        }

        // Clean up stale link nodes from local Store under COF
        int staleLinksRemoved = 0;
        try
        {
            if (cofUuid != LibreMetaverse.UUID.Zero)
            {
                var cofNode = store?.GetNodeOrDefault(cofUuid);
                if (cofNode != null)
                {
                    var staleKeys = new List<LibreMetaverse.UUID>();
                    foreach (var childNode in cofNode.Nodes.Values)
                    {
                        if (childNode.Data is LibreMetaverse.InventoryItem cofItem)
                        {
                            var target = cofItem.IsLink() ? (cofItem.ResolvedItemID != LibreMetaverse.UUID.Zero ? cofItem.ResolvedItemID : cofItem.AssetUUID) : cofItem.UUID;
                            if (uuidsToDetach.Contains(cofItem.UUID) || uuidsToDetach.Contains(target))
                            {
                                staleKeys.Add(cofItem.UUID);
                            }
                        }
                    }

                    // Server-side half. Dropping the node from the local Store alone (below) only
                    // hides the row until the next fetch re-reads the folder from the sim, which
                    // is exactly the "Detach does nothing" report: the item was never attached, so
                    // the DetachAttachmentIntoInv packets above matched nothing, and the COF link
                    // that made it LOOK worn survived every refresh.
                    //
                    // Only when nothing was actually attached: for a real attachment the sim
                    // removes the link itself as part of the detach, and racing it from here could
                    // strip the outfit entry of an item whose detach then failed.
                    //
                    // Moved to Trash rather than purged. The link is not the item -- the real
                    // object stays where it lives in inventory -- but an outfit is still user data
                    // and Trash keeps a mistake recoverable, unlike RemoveItemsAsync.
                    if (!wasAttached && staleKeys.Count > 0 && TrashFolderId is { } trashId)
                    {
                        var trashUuid = new LibreMetaverse.UUID(trashId);
                        foreach (var k in staleKeys)
                        {
                            if (k == LibreMetaverse.UUID.Zero) continue;
                            try
                            {
                                _client.Inventory.MoveItem(k, trashUuid);
                                staleLinksRemoved++;
                            }
                            catch { }
                        }
                    }

                    foreach (var k in staleKeys)
                    {
                        cofNode.Nodes.Remove(k);
                    }
                }
            }
        }
        catch { }

        return Task.FromResult(new DetachResult(wasAttached, staleLinksRemoved));
    }

    /// <summary>Detaches whatever attachment is the given scene-local object id, via ObjectDetach
    /// (by localId). Unlike <see cref="DetachItemAsync"/> (DetachAttachmentIntoInv, which the sim
    /// matches on the attachment's AttachItemID name-value) this works even when that name-value
    /// is missing or stale — the case where an inventory "Detach" silently does nothing.</summary>
    public void DetachByLocalId(uint localId)
    {
        var sim = _client.Network.CurrentSim;
        if (sim == null || localId == 0) return;
        _client.Objects.DetachObjects(sim, new List<uint> { localId });
    }

    /// <summary>Escape hatch for a stuck attachment that can't be pinned down in inventory:
    /// ObjectDetach every worn attachment (optionally only the HUD-point ones) by localId.
    /// Returns how many were sent.</summary>
    public int DetachAllAttachments(bool hudOnly = false)
    {
        var sim = _client.Network.CurrentSim;
        if (sim == null) return 0;

        var ids = new List<uint>();
        var report = new List<(uint LocalId, LibreMetaverse.AttachmentPoint Point, string Name)>();
        foreach (var p in sim.ObjectsPrimitives.Values)
        {
            // Only root prims of an attachment carry ParentID == our avatar; child prims hang off
            // the attachment root and ObjectDetach on the root takes the whole linkset.
            if (p == null || p.ParentID != _client.Self.LocalID) continue;
            var ap = p.PrimData.AttachmentPoint;
            if (ap == LibreMetaverse.AttachmentPoint.Default) continue;
            bool isHud = (int)ap >= 31 && (int)ap <= 38; // HUDCenter2 .. HUDBottomRight
            if (hudOnly && !isHud) continue;
            ids.Add(p.LocalID);
            report.Add((p.LocalID, ap, p.Properties?.Name ?? ""));
        }

        // Named, not just counted: "0 detached" and "3 detached but one is still on screen" are
        // the two outcomes this escape hatch has to be able to tell apart afterwards.
        foreach (var (localId, point, name) in report)
            Console.Error.WriteLine($"[Detach] localId={localId} point={point} \"{name}\"");

        if (ids.Count > 0) _client.Objects.DetachObjects(sim, ids);
        return ids.Count;
    }

    /// <summary>
    /// Formats an SL AttachmentPoint enum value to a human-readable display string.
    /// </summary>
    public static string FormatAttachmentPoint(LibreMetaverse.AttachmentPoint point)
    {
        return point switch
        {
            LibreMetaverse.AttachmentPoint.Chest => "Brust",
            LibreMetaverse.AttachmentPoint.Skull => "Kopf",
            LibreMetaverse.AttachmentPoint.LeftShoulder => "Linke Schulter",
            LibreMetaverse.AttachmentPoint.RightShoulder => "Rechte Schulter",
            LibreMetaverse.AttachmentPoint.LeftHand => "Linke Hand",
            LibreMetaverse.AttachmentPoint.RightHand => "Rechte Hand",
            LibreMetaverse.AttachmentPoint.LeftFoot => "Linker Fuß",
            LibreMetaverse.AttachmentPoint.RightFoot => "Rechter Fuß",
            LibreMetaverse.AttachmentPoint.Spine => "Rücken",
            LibreMetaverse.AttachmentPoint.Pelvis => "Becken",
            LibreMetaverse.AttachmentPoint.Mouth => "Mund",
            LibreMetaverse.AttachmentPoint.Chin => "Kinn",
            LibreMetaverse.AttachmentPoint.LeftEar => "Linkes Ohr",
            LibreMetaverse.AttachmentPoint.RightEar => "Rechtes Ohr",
            LibreMetaverse.AttachmentPoint.LeftEyeball => "Linkes Auge",
            LibreMetaverse.AttachmentPoint.RightEyeball => "Rechtes Auge",
            LibreMetaverse.AttachmentPoint.Nose => "Nase",
            LibreMetaverse.AttachmentPoint.RightUpperArm => "Rechter Oberarm",
            LibreMetaverse.AttachmentPoint.RightForearm => "Rechter Unterarm",
            LibreMetaverse.AttachmentPoint.LeftUpperArm => "Linker Oberarm",
            LibreMetaverse.AttachmentPoint.LeftForearm => "Linker Unterarm",
            LibreMetaverse.AttachmentPoint.RightHip => "Rechte Hüfte",
            LibreMetaverse.AttachmentPoint.RightUpperLeg => "Rechtes Oberschenkel",
            LibreMetaverse.AttachmentPoint.RightLowerLeg => "Rechtes Unterschenkel",
            LibreMetaverse.AttachmentPoint.LeftHip => "Linke Hüfte",
            LibreMetaverse.AttachmentPoint.LeftUpperLeg => "Linkes Oberschenkel",
            LibreMetaverse.AttachmentPoint.LeftLowerLeg => "Linkes Unterschenkel",
            LibreMetaverse.AttachmentPoint.Stomach => "Bauch",
            LibreMetaverse.AttachmentPoint.LeftPec => "Linke Brust",
            LibreMetaverse.AttachmentPoint.RightPec => "Rechte Brust",
            LibreMetaverse.AttachmentPoint.HUDCenter2 => "Mitte 2",
            LibreMetaverse.AttachmentPoint.HUDTopRight => "Oben rechts",
            LibreMetaverse.AttachmentPoint.HUDTop => "Oben",
            LibreMetaverse.AttachmentPoint.HUDTopLeft => "Oben links",
            LibreMetaverse.AttachmentPoint.HUDCenter => "Mitte",
            LibreMetaverse.AttachmentPoint.HUDBottomLeft => "Unten links",
            LibreMetaverse.AttachmentPoint.HUDBottom => "Unten",
            LibreMetaverse.AttachmentPoint.HUDBottomRight => "Unten rechts",
            LibreMetaverse.AttachmentPoint.Neck => "Hals",
            LibreMetaverse.AttachmentPoint.Root => "Stamm",
            LibreMetaverse.AttachmentPoint.LeftWing => "Linker Flügel",
            LibreMetaverse.AttachmentPoint.RightWing => "Rechter Flügel",
            _ => point.ToString()
        };
    }

    /// <summary>
    /// Returns a dictionary mapping currently worn inventory item IDs (or link target IDs)
    /// to their attachment point display string (e.g. "Linker Flügel", "Oben links") or "getragen".
    /// </summary>
    public Dictionary<Guid, string> GetWornItemsMap()
    {
        var result = new Dictionary<Guid, string>();

        try
        {
            var atts = _client.Appearance.GetAttachmentsByItemId();
            foreach (var kvp in atts)
            {
                result[kvp.Key.Guid] = FormatAttachmentPoint(kvp.Value);
            }
        }
        catch { }

        try
        {
            var store = _client.Inventory.Store;
            var cofUuid = _client.Inventory.FindFolderForType(LibreMetaverse.FolderType.CurrentOutfit);
            if (cofUuid != LibreMetaverse.UUID.Zero)
            {
                var cofNode = store?.GetNodeOrDefault(cofUuid);
                if (cofNode != null)
                {
                    foreach (var childNode in cofNode.Nodes.Values)
                    {
                        if (childNode.Data is LibreMetaverse.InventoryItem item)
                        {
                            var targetId = item.IsLink() ? item.ResolvedItemID.Guid : item.UUID.Guid;
                            if (targetId != Guid.Empty && !result.ContainsKey(targetId))
                            {
                                result[targetId] = "getragen";
                            }
                        }
                    }
                }
            }
        }
        catch { }

        return result;
    }

    /// <summary>Creates a new inventory subfolder — used for the Create Landmark dialog's
    /// "new folder" affordance, but generic. Note: the 3-arg <c>CreateFolder</c> overload that
    /// takes a <c>FolderType</c> de-dupes on preferred type and would hand back the *existing*
    /// system folder of that type instead of creating a new one, so this always uses the
    /// 2-arg (plain, <c>FolderType.None</c>) overload.</summary>
    public Guid CreateInventoryFolder(Guid parentId, string name) =>
        _client.Inventory.CreateFolder(new LibreMetaverse.UUID(parentId), name).Guid;

    /// <summary>Creates a landmark asset for the agent's current region + position and uploads
    /// it as a new inventory item in <paramref name="folderId"/> (typically <see
    /// cref="LandmarksFolderId"/> or a subfolder of it). Requires the grid's
    /// <c>NewFileAgentInventory</c> CAP — present on modern OpenSim/SL, but reported as a
    /// neutral failure rather than thrown if missing, same as <see cref="LoginAsync"/>.</summary>
    public async Task<LandmarkCreateResult> CreateLandmarkHereAsync(
        string name, string description, Guid folderId, CancellationToken ct = default)
    {
        var sim = _client.Network.CurrentSim;
        if (sim == null) return new LandmarkCreateResult(false, null, null, "Not connected.");

        try
        {
            // The official viewer sends a CreateInventoryItem UDP request (wrapped here by CreateItemAsync)
            // for landmarks. It does NOT upload an AssetLandmark via CAPS. When the grid receives
            // a CreateInventoryItem request for AssetType.Landmark, it automatically generates the
            // landmark asset using the agent's current region and position, then creates the item.
            var item = await _client.Inventory.CreateItemAsync(
                new LibreMetaverse.UUID(folderId),
                name,
                description,
                AssetType.Landmark,
                LibreMetaverse.UUID.Zero, // No asset upload transaction
                InventoryType.Landmark,
                PermissionMask.Copy | PermissionMask.Transfer,
                ct).ConfigureAwait(false);

            if (item != null)
            {
                // The item created via UDP is immediately valid and carries the correct AssetType/InventoryType
                // on the server, avoiding CAPS asset-type corruption.
                return new LandmarkCreateResult(true, item.UUID.Guid, item.AssetUUID.Guid, "Success");
            }
            return new LandmarkCreateResult(false, null, null, "Failed to create landmark (no response).");
        }
        catch (Exception ex)
        {
            return new LandmarkCreateResult(false, null, null, ex.Message);
        }
    }

    /// <summary>
    /// Gets the detailed properties of an inventory item from the local store cache.
    /// Returns null if not found or if the item is a folder.
    /// </summary>
    public InventoryItemProperties? GetItemProperties(Guid itemId)
    {
        var node = _client.Inventory.Store?.GetNodeFor(new LibreMetaverse.UUID(itemId));
        if (node?.Data is LibreMetaverse.InventoryItem item)
        {
            var next = item.Permissions.NextOwnerMask;
            return new InventoryItemProperties(
                itemId,
                item.Name,
                item.Description,
                next.HasFlag(LibreMetaverse.PermissionMask.Copy),
                next.HasFlag(LibreMetaverse.PermissionMask.Modify),
                next.HasFlag(LibreMetaverse.PermissionMask.Transfer)
            );
        }
        return null;
    }

    /// <summary>
    /// Updates an inventory item's name, description, and next owner permissions.
    /// </summary>
    public void UpdateItemProperties(Guid itemId, string newName, string newDescription, bool nextCopy, bool nextModify, bool nextTransfer)
    {
        if (!_client.Network.Connected) return;

        var node = _client.Inventory.Store?.GetNodeFor(new LibreMetaverse.UUID(itemId));
        if (node?.Data is LibreMetaverse.InventoryItem item)
        {
            item.Name = newName;
            item.Description = newDescription;

            // Build new next-owner mask
            uint nextOwnerMask = 0;
            if (nextCopy) nextOwnerMask |= (uint)LibreMetaverse.PermissionMask.Copy;
            if (nextModify) nextOwnerMask |= (uint)LibreMetaverse.PermissionMask.Modify;
            if (nextTransfer) nextOwnerMask |= (uint)LibreMetaverse.PermissionMask.Transfer;

            // Only update next-owner permissions; others shouldn't be touched by UI directly yet
            var perms = item.Permissions;
            perms.NextOwnerMask = (LibreMetaverse.PermissionMask)nextOwnerMask;
            item.Permissions = perms;

            _client.Inventory.RequestUpdateItem(item);
        }
    }

    public void SelectObject(uint localId)
    {
        if (!_client.Network.Connected || _client.Network.CurrentSim == null) return;
        _client.Objects.SelectObject(_client.Network.CurrentSim, localId);
    }

    public void DeselectObject(uint localId)
    {
        if (!_client.Network.Connected || _client.Network.CurrentSim == null) return;
        _client.Objects.DeselectObject(_client.Network.CurrentSim, localId);
    }

    public void RequestObjectProperties(Guid objectId)
    {
        if (!_client.Network.Connected || _client.Network.CurrentSim == null) return;
        _client.Objects.RequestObjectPropertiesFamily(_client.Network.CurrentSim, new LibreMetaverse.UUID(objectId));
    }

    public void UpdateObjectTransform(uint localId, System.Numerics.Vector3 position, System.Numerics.Quaternion rotation, System.Numerics.Vector3 scale)
    {
        if (!_client.Network.Connected || _client.Network.CurrentSim == null) return;

        var slPos = new LibreMetaverse.Vector3(position.X, position.Y, position.Z);
        var slRot = new LibreMetaverse.Quaternion(rotation.X, rotation.Y, rotation.Z, rotation.W);
        var slScale = new LibreMetaverse.Vector3(scale.X, scale.Y, scale.Z);

        _client.Objects.SetPosition(_client.Network.CurrentSim, localId, slPos);
        _client.Objects.SetRotation(_client.Network.CurrentSim, localId, slRot);
        _client.Objects.SetScale(_client.Network.CurrentSim, localId, slScale, true, false);
    }

    /// <summary>Sets Physical/Temporary/Phantom/CastShadows together in one ObjectFlagUpdate --
    /// the wire message replaces all four at once, so callers must pass the object's full
    /// current state, not just the one flag being toggled.
    ///
    /// Must send OpenSim's PhysShapeType.invalid (255) as the extra-physics shape type, NOT
    /// LibreMetaverse's default PhysicsShapeType.Prim (0, a real value on the wire). OpenSim's
    /// SceneGraph.UpdatePrimFlags branches on this byte: anything other than invalid is treated
    /// as "also apply extra physics data" (density/friction/shape), and THAT branch never calls
    /// group.UpdateFlags(...) at all -- so a Prim-shape request silently never touches
    /// Physical/Temporary/Phantom no matter how good the object's permissions are. LMV's
    /// simplified 6-arg SetFlags() overload hardcodes Prim, which is why every flag toggle from
    /// this client was being swallowed (confirmed: a full-perm object the same agent already
    /// edits fine in Firestorm still silently rejected our ObjectFlagUpdate).</summary>
    /// <summary>Flags AND physics-shape/material data share one wire message
    /// (ObjectFlagUpdate) -- every call resends both, so the caller must pass the object's
    /// current known physics values (not just the flag being changed), the same way
    /// ObjectEditWindow.SendObjectFlags already threads through its other unchanged flags.
    /// physicsShapeType=(byte)255/invalid is the sentinel meaning "leave extra-physics data
    /// alone" (see opensim-objectflagupdate-physshapetype memory) -- pass a real
    /// PrimPhysicsShapeType value only when the caller actually knows/wants to set one.</summary>
    public void SetObjectFlags(uint localId, bool physical, bool temporary, bool phantom, bool castsShadows,
        PrimPhysicsShapeType physicsShapeType, float density, float friction, float restitution, float gravityMultiplier)
    {
        if (!_client.Network.Connected || _client.Network.CurrentSim == null) return;
        _client.Objects.SetFlags(_client.Network.CurrentSim, localId, physical, temporary, phantom, castsShadows,
            (PhysicsShapeType)(byte)physicsShapeType, density, friction, restitution, gravityMultiplier);
    }

    /// <summary>SL's "Locked" build-floater checkbox isn't a wire flag -- it's expressed by
    /// removing/restoring the owner's Move permission.</summary>
    public void SetObjectLocked(uint localId, bool locked)
    {
        if (!_client.Network.Connected || _client.Network.CurrentSim == null) return;
        _client.Objects.SetPermissions(_client.Network.CurrentSim, new List<uint> { localId }, PermissionWho.Owner, PermissionMask.Move, !locked);
    }

    /// <summary>SL's point-light ("Light") prim property -- an ExtraParams block, not a
    /// PrimFlags bit (see ObjectManager.SetLight). Disabling sends the block with Intensity 0
    /// rather than omitting it, matching LibreMetaverse's own enabled/disabled convention.</summary>
    public void SetObjectLight(uint localId, bool enabled, System.Numerics.Vector3 color, float intensity, float radius, float falloff)
    {
        if (!_client.Network.Connected || _client.Network.CurrentSim == null) return;
        var light = new Primitive.LightData
        {
            Color = new Color4(color.X, color.Y, color.Z, 1f),
            Intensity = enabled ? intensity : 0f,
            Radius = radius,
            Falloff = falloff,
            Cutoff = 0f
        };
        _client.Objects.SetLight(_client.Network.CurrentSim, localId, light);
    }

    /// <summary>Classic material (Stone/Metal/.../Rubber) -- collision sound/friction. Sends a
    /// dedicated ObjectMaterial packet, unrelated to the flags/light messages above.</summary>
    public void SetObjectMaterial(uint localId, SLNG.Core.PrimMaterial material)
    {
        if (!_client.Network.Connected || _client.Network.CurrentSim == null) return;
        _client.Objects.SetMaterial(_client.Network.CurrentSim, localId, (Material)(byte)material);
    }

    /// <summary>Rezzes a new basic-shape prim at the given region-local position. The sim only
    /// treats this as an approximate placement (see ObjectManager.AddPrim's remarks) -- the
    /// object streams back in shortly after via the normal ObjectUpdate path, same as any other
    /// object.</summary>
    public void CreatePrim(SLNG.Core.BasicPrimType type, System.Numerics.Vector3 position)
    {
        if (!_client.Network.Connected || _client.Network.CurrentSim == null) return;

        var lmvType = type switch
        {
            SLNG.Core.BasicPrimType.Box => PrimType.Box,
            SLNG.Core.BasicPrimType.Cylinder => PrimType.Cylinder,
            SLNG.Core.BasicPrimType.Prism => PrimType.Prism,
            SLNG.Core.BasicPrimType.Sphere => PrimType.Sphere,
            SLNG.Core.BasicPrimType.Torus => PrimType.Torus,
            SLNG.Core.BasicPrimType.Tube => PrimType.Tube,
            SLNG.Core.BasicPrimType.Ring => PrimType.Ring,
            _ => PrimType.Box
        };

        var constructionData = ObjectManager.BuildBasicShape(lmvType);
        var slPos = new LibreMetaverse.Vector3(position.X, position.Y, position.Z);
        _client.Objects.AddPrim(_client.Network.CurrentSim, constructionData, UUID.Zero, slPos,
            new LibreMetaverse.Vector3(0.5f, 0.5f, 0.5f), LibreMetaverse.Quaternion.Identity);
    }

    /// <summary>Sends an AgentUpdate to move the avatar.</summary>
    /// <param name="cameraPosition">The RENDER camera's region-local position (System.Numerics,
    /// SL Z-up axes), or null to keep anchoring the interest camera on the avatar's facing. The
    /// sim centres its interest list on <c>CameraCenter</c>, so without this it streams objects
    /// around LibreMetaverse's default region-centre camera (128,128,20), not where the user is
    /// looking -- BUG-NET-01.</param>
    /// <param name="cameraForward">The render camera's forward direction (region-local, SL axes);
    /// only used when <paramref name="cameraPosition"/> is supplied.</param>
    /// <param name="cameraFar">Interest / draw distance in metres; ignored when &lt;= 0.</param>
    public void SetMovement(bool forward, bool backward, bool left, bool right, bool up, bool down,
        System.Numerics.Quaternion cameraRotation, bool fly = false,
        System.Numerics.Vector3? cameraPosition = null,
        System.Numerics.Vector3? cameraForward = null,
        float cameraFar = 0f)
    {
        if (!_client.Network.Connected) return;

        // Map Godot/SLNG axes to LibreMetaverse (which uses OpenSim/SL axes: X forward, Y left, Z up)
        // For LibreMetaverse, we just pass the rotation directly.
        var slQuat = new LibreMetaverse.Quaternion(cameraRotation.X, cameraRotation.Y, cameraRotation.Z, cameraRotation.W);

        // Interest camera. With a real render-camera pose, anchor CameraCenter there (BUG-NET-01);
        // otherwise fall back to the pre-existing "look along body facing from wherever the camera
        // already is" behaviour.
        if (cameraPosition is { } camPos)
        {
            var slPos = new LibreMetaverse.Vector3(camPos.X, camPos.Y, camPos.Z);
            var slFwd = cameraForward is { } f
                ? new LibreMetaverse.Vector3(f.X, f.Y, f.Z)
                : LibreMetaverse.Vector3.UnitX * slQuat;
            _client.Self.Movement.Camera.LookAt(slPos, slPos + slFwd);
        }
        else
        {
            _client.Self.Movement.Camera.LookDirection(LibreMetaverse.Vector3.UnitX * slQuat);
        }

        if (cameraFar > 0f)
            _client.Self.Movement.Camera.Far = cameraFar;

        _client.Self.Movement.HeadRotation = slQuat;
        _client.Self.Movement.BodyRotation = slQuat;

        _client.Self.Movement.AtPos = forward;
        _client.Self.Movement.AtNeg = backward;
        _client.Self.Movement.LeftPos = left;
        _client.Self.Movement.LeftNeg = right;
        _client.Self.Movement.UpPos = up;
        _client.Self.Movement.UpNeg = down;
        _client.Self.Movement.Fly = fly;

        // Send the update to the server
        _client.Self.Movement.SendUpdate(false);
    }

    /// <summary>
    /// Fetches the raw bytes of a mesh asset from the simulator. Returns a neutral
    /// payload — no LibreMetaverse type crosses this boundary; decoding lives in
    /// <c>SLNG.Assets</c>.
    /// </summary>
    /// <summary>Fetches legacy Blinn-Phong materials by id from the region's
    /// <c>RenderMaterials</c> capability, as neutral <see cref="LegacyMaterialData"/>.
    ///
    /// <para>Materials are NOT assets: they live in a per-region capability whose request and
    /// response bodies are zlib-compressed LLSD, and a request carries at most 50 ids
    /// (MATERIALS_GET_MAX_ENTRIES, llmaterialmgr.cpp:58). LibreMetaverse implements all of that,
    /// so this method's job is batching to that limit and converting at the boundary.</para>
    ///
    /// <para>Returns only what the sim actually returned -- an id it does not know is simply
    /// absent from the result, never a default-valued entry, so the caller can tell "resolved to
    /// a material with no maps" from "never resolved".</para></summary>
    public async Task<IReadOnlyList<LegacyMaterialData>> FetchLegacyMaterialsAsync(
        IReadOnlyCollection<Guid> materialIds, CancellationToken cancellationToken = default)
    {
        var sim = _client.Network.CurrentSim;
        if (sim == null || materialIds.Count == 0) return Array.Empty<LegacyMaterialData>();

        var result = new List<LegacyMaterialData>(materialIds.Count);
        var batch = new List<LibreMetaverse.UUID>(MaterialsPerRequest);

        foreach (var id in materialIds)
        {
            if (id == Guid.Empty) continue;
            batch.Add(new LibreMetaverse.UUID(id));
            if (batch.Count < MaterialsPerRequest) continue;
            await FetchOneBatchAsync(sim, batch, result, cancellationToken).ConfigureAwait(false);
            batch.Clear();
        }
        if (batch.Count > 0)
            await FetchOneBatchAsync(sim, batch, result, cancellationToken).ConfigureAwait(false);

        return result;
    }

    /// <summary>MATERIALS_GET_MAX_ENTRIES (llmaterialmgr.cpp:58). The sim rejects more.</summary>
    private const int MaterialsPerRequest = 50;

    private async Task FetchOneBatchAsync(
        LibreMetaverse.Simulator sim, List<LibreMetaverse.UUID> ids,
        List<LegacyMaterialData> into, CancellationToken cancellationToken)
    {
        try
        {
            var materials = await _client.Objects.RequestMaterialsAsync(sim, ids, cancellationToken)
                .ConfigureAwait(false);
            foreach (var m in materials) into.Add(ToLegacyMaterialData(m));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Materials] request for {ids.Count} legacy materials failed: {ex.Message}");
        }
    }

    /// <summary>Converts LibreMetaverse's <c>LegacyMaterial</c> to the neutral value. Public and
    /// static so the conversion is testable on its own -- it is the only place the two type
    /// systems meet, and it is where a units mistake (the wire scales every float by 10000, and
    /// the specular tint arrives 0-255) would otherwise be invisible until something rendered
    /// wrong.</summary>
    public static LegacyMaterialData ToLegacyMaterialData(LibreMetaverse.Materials.LegacyMaterial m)
    {
        // SpecularColor is already 0-1 on LibreMetaverse's side; the wire's 0-255 form is decoded
        // by its OSD reader. It becomes a Vector4 here so nothing downstream needs an LMV type.
        return new LegacyMaterialData(
            m.ID.Guid,
            m.NormalMap.Guid,
            new System.Numerics.Vector2((float)m.NormalMapOffsetX, (float)m.NormalMapOffsetY),
            new System.Numerics.Vector2((float)m.NormalMapRepeatX, (float)m.NormalMapRepeatY),
            (float)m.NormalMapRotation,
            m.SpecularMap.Guid,
            new System.Numerics.Vector2((float)m.SpecularMapOffsetX, (float)m.SpecularMapOffsetY),
            new System.Numerics.Vector2((float)m.SpecularMapRepeatX, (float)m.SpecularMapRepeatY),
            (float)m.SpecularMapRotation,
            new System.Numerics.Vector4(m.SpecularColor.R, m.SpecularColor.G, m.SpecularColor.B, m.SpecularColor.A),
            m.SpecularExponent,
            m.EnvironmentIntensity,
            m.AlphaMaskCutoff,
            (LegacyDiffuseAlphaMode)(byte)m.DiffuseAlphaMode);
    }

    public async Task<byte[]?> FetchMeshDataAsync(Guid meshId)
    {
        var asset = await _client.Assets
            .RequestMeshAsync(new UUID(meshId), CancellationToken.None)
            .ConfigureAwait(false);
        return asset?.AssetData;
    }

    private static readonly HttpClient _textureHttpClient = new();

    /// <summary>
    /// Fetches the raw bytes of a texture asset (JPEG2000) from the simulator. Returns null
    /// if the fetch times out or fails.
    /// </summary>
    /// <param name="desiredDiscard">SL/OpenSim J2K discard level to request: 0 = full resolution
    /// up to <see cref="J2kByteSizeEstimator.MaxDiscardLevel"/> = coarsest. FEAT-PERF-02 Phase 2:
    /// a higher discard level makes the SIMULATOR send fewer bytes (verified against OpenSim's
    /// GetTextureHandler/J2KImage source, see docs/specs/FEAT-PERF-02-texture-loading-speed.md's
    /// Phase 2.1 write-up), not just a client-side decode/display hint.</param>
    /// <param name="skipHttp">Forces the UDP path. Set by the caller when a previous attempt's
    /// HTTP body arrived intact-looking but would not decode: retrying HTTP just re-fetches the
    /// identical bytes, so without this the UDP fallback is unreachable for exactly the assets
    /// that need it most (see AssetService's retry loop).</param>
    public struct TextureFetchResult
    {
        public byte[]? Data;
        public bool IsReliable;
    }

    public async Task<TextureFetchResult> FetchTextureDataAsync(Guid textureId, int desiredDiscard = 0, bool skipHttp = false)
    {
        // FEAT-PERF-02 Phase 2: prefer our own HTTP GetTexture Range fetch over the UDP path
        // below. Two wins over the pre-existing code: (1) HTTP is the faster transport (no UDP
        // packet/ACK overhead or agent-throttle pacing) even for a full (discard 0) fetch --
        // LibreMetaverse's own built-in HTTP texture path was configured as preferred
        // (UseHttpTextures=true, see the TexturePipeline setup above) but was never actually
        // reached, because the reflection call below always targets the UDP TexturePipeline
        // regardless of that setting; (2) for discard > 0, a Range request makes the SIMULATOR
        // send fewer bytes -- LibreMetaverse's built-in HTTP fetch (AssetManager.
        // HttpRequestTexture) ignores discardLevel/priority entirely and always downloads the
        // whole asset, so it can't do this at all, which is why this method builds the HTTP
        // request itself instead of calling into LibreMetaverse's HTTP path.
        var capUri = skipHttp ? null : _client.Network.CurrentSim?.Caps?.GetTextureCapURI();
        if (capUri != null)
        {
            var httpResult = await FetchTextureViaHttpRangeAsync(textureId, desiredDiscard, capUri).ConfigureAwait(false);
            if (httpResult != null) return new TextureFetchResult { Data = httpResult, IsReliable = true };
            // Falls through to the UDP path below on any HTTP failure (network error,
            // non-success status) -- never a hard failure just because HTTP didn't pan out.
        }

        var tcs = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var pipeline = typeof(AssetManager).GetField("Texture", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(_client.Assets);
            if (pipeline == null)
                return new TextureFetchResult { Data = UdpFailed(textureId, "AssetManager.Texture field not found (reflection)"), IsReliable = false };
            {
                var reqMethod = pipeline.GetType().GetMethod("RequestTexture", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (reqMethod == null)
                    return new TextureFetchResult { Data = UdpFailed(textureId, "TexturePipeline.RequestTexture not found (reflection)"), IsReliable = false };
                {
                    var callbackType = reqMethod.GetParameters()[5].ParameterType;
                    Action<TextureRequestState, LibreMetaverse.Assets.AssetTexture> action = (state, assetTexture) =>
                    {
                        if (state == TextureRequestState.Finished)
                        {
                            var data = assetTexture?.AssetData;
                            tcs.TrySetResult(data is { Length: > 0 } ? data : null);
                        }
                        else if (state == TextureRequestState.NotFound || state == TextureRequestState.Aborted || state == TextureRequestState.Timeout)
                        {
                            UdpFailed(textureId, $"pipeline reported {state}");
                            tcs.TrySetResult(null);
                        }
                    };
                    var delegateObj = Delegate.CreateDelegate(callbackType, action.Target, action.Method);

                    // RequestTexture(UUID textureID, ImageType imageType, float priority, int discardLevel,
                    //                uint packetStart, TextureDownloadCallback callback, bool progressive).
                    // discardLevel now passed through instead of hardcoded 0 -- this UDP path DOES
                    // honor it server-side (unlike LibreMetaverse's HTTP path), so even this
                    // fallback benefits from a non-zero desiredDiscard when HTTP isn't reachable.
                    reqMethod.Invoke(pipeline, new object[] { new UUID(textureId), ImageType.Normal, 100000.0f, desiredDiscard, 0u, delegateObj, false });

                    // The pipeline can simply never call back -- e.g. if it was never started
                    // because the client is configured to prefer HTTP textures. Awaiting the bare
                    // TaskCompletionSource would then hang until AssetService's own 60 s timeout,
                    // three times per texture, with nothing in the log to say why. Bound it here
                    // and name it instead.
                    var udpTimeout = Task.Delay(TimeSpan.FromSeconds(20));
                    if (await Task.WhenAny(tcs.Task, udpTimeout).ConfigureAwait(false) != tcs.Task)
                        return new TextureFetchResult { Data = UdpFailed(textureId, "TexturePipeline never called back within 20s"), IsReliable = false };

                    var udpBytes = await tcs.Task.ConfigureAwait(false);
                    if (udpBytes is { Length: > 0 })
                        // Console.Error.WriteLine($"[TextureFetch] {textureId}: UDP delivered {udpBytes.Length} bytes");
                        return new TextureFetchResult { Data = udpBytes, IsReliable = false };
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GridSession] TexturePipeline reflection failed: {ex.Message}");
        }

        // Fallback if reflection fails
        var fallbackTcs = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = _client.Assets.RequestImageAsync(new UUID(textureId), ImageType.Normal, CancellationToken.None)
            .ContinueWith(t =>
            {
                var data = t.Result?.AssetData;
                fallbackTcs.TrySetResult(data is { Length: > 0 } ? data : null);
            });

        return new TextureFetchResult { Data = await fallbackTcs.Task, IsReliable = false };
    }

    /// <summary>
    /// Issues our own HTTP GET against the region's GetTexture capability, with a Range header
    /// when <paramref name="desiredDiscard"/> is above 0 -- see <see cref="FetchTextureDataAsync"/>'s
    /// doc comment for why LibreMetaverse's own HTTP path can't do this. A partial (206) response
    /// is treated exactly like a full (200) one -- the caller (AssetService) knows this data may
    /// be truncated-on-purpose and routes it to the tolerant decoder, same as any other incomplete
    /// J2C stream. Returns null on ANY failure (non-success status, network error) so the caller
    /// falls back to the UDP path -- never throws.
    /// </summary>
    // Why a texture fetch gave up, once per texture id. The HTTP path has five separate silent
    // "return null" exits and the UDP fallback a sixth, all of which surfaced to the renderer as
    // the same "fetch/decode returned null" line -- which is how 14 textures could fail
    // consistently in one view with no way to tell a missing asset from a rejected codestream.
    // Deduped because a failing texture is retried and re-requested by every face that uses it.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _fetchFailureLogged = new();

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _udpFailureLogged = new();

    private static byte[]? UdpFailed(Guid textureId, string reason)
    {
        if (_udpFailureLogged.TryAdd(textureId, 0))
            Console.Error.WriteLine($"[TextureFetch] {textureId}: UDP fallback failed: {reason}");
        return null;
    }

    private static byte[]? FetchFailed(Guid textureId, string reason)
    {
        if (_fetchFailureLogged.TryAdd(textureId, 0))
            Console.Error.WriteLine($"[TextureFetch] {textureId} HTTP fetch gave up: {reason} — falling back to UDP");
        return null;
    }

    private async Task<byte[]?> FetchTextureViaHttpRangeAsync(Guid textureId, int desiredDiscard, Uri capUri)
    {
        try
        {
            var url = new Uri($"{capUri}?texture_id={textureId}");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (desiredDiscard > 0)
            {
                int byteLimit = J2kByteSizeEstimator.CalcDataSizeJ2C(0, 0, desiredDiscard);
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, byteLimit - 1);
            }

            using var response = await _textureHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return FetchFailed(textureId, $"HTTP {(int)response.StatusCode}");

            long? declaredLength = response.Content.Headers.ContentLength;
            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (bytes.Length == 0) return FetchFailed(textureId, "empty body");

            // The body must actually BE a JPEG2000 codestream. Measured on OSGrid 2026-08-02: of
            // 14 textures that rendered white, four came back as 1-3 byte bodies -- three of them
            // the literal bytes 9E E9 65, one a single 00 -- with a success status and a matching
            // Content-Length. Those were handed on as image data, failed both decoders, and the
            // surface stayed blank.
            //
            // The damage was not the failed decode, it was that HTTP "succeeded": the caller only
            // falls through to the UDP path when this method returns null, so a garbage body meant
            // UDP was never tried at all and three retries just re-fetched the same rubbish. A raw
            // J2C starts with SOC (FF 4F); a JP2-wrapped one starts with the 12-byte JP2 signature
            // box. Anything else is not a texture, whatever the status line claimed.
            bool isJ2c = bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0x4F;
            bool isJp2 = bytes.Length >= 12 && bytes[4] == 0x6A && bytes[5] == 0x50
                         && bytes[6] == 0x20 && bytes[7] == 0x20;
            if (!isJ2c && !isJp2)
                return FetchFailed(textureId, $"not a JPEG2000 stream ({bytes.Length} bytes, " +
                    $"head {string.Join("", bytes.Take(Math.Min(4, bytes.Length)).Select(b => b.ToString("X2")))})");

            // OpenSim's embedded HTTP server has been observed (empirically, right after a
            // teleport/region-crossing burst of many simultaneous texture GETs) to close the
            // connection early and return fewer bytes than its own declared Content-Length --
            // with a 200/206 success status and no exception from HttpClient, since an early
            // clean connection close is indistinguishable from "body complete" once the socket
            // just stops sending. Handing that short body to the J2K decoder is exactly the
            // "Codestream truncated" case this method exists to avoid (see the doc comment
            // above) even though we never sent a Range header ourselves. Treat a short read as a
            // failed fetch so the caller falls back to the UDP path in the SAME attempt, instead
            // of silently decoding (and, for Magick.NET, likely failing on) partial data.
            if (declaredLength.HasValue && bytes.Length < declaredLength.Value)
                return FetchFailed(textureId, $"short read {bytes.Length}/{declaredLength.Value}");

            // The transport-level checks above can only catch a truncation the TRANSPORT knows
            // about. A body that is short but whose Content-Length agrees with it -- the sim
            // serving a partial asset and honestly declaring the partial size -- passes both, and
            // then decodes "degraded": gap-filled pixels. For an ordinary texture that is a
            // cosmetic problem; for a SCULPT MAP the pixels ARE the vertex positions, so the
            // renderer (correctly) refuses the result and substitutes a placeholder solid, which
            // is how a rock ends up on screen as a smooth flat disc.
            //
            // Content-Length only catches truncation when the server actually sends that
            // header -- OpenSim's embedded HTTP server can respond chunked (no Content-Length)
            // for texture bodies, which would let a short chunked read straight through the
            // check above. A complete J2C codestream (SOC marker 0xFF4F at the start, verified
            // by AssetService.DecodeTexture) always ends with an EOC marker (0xFFD9) -- that's
            // true regardless of transport, so it catches the chunked-encoding gap. Only applies
            // to a full (desiredDiscard 0) fetch: an intentional Range request never contains
            // the EOC by design.
            if (desiredDiscard == 0 && bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0x4F
                && (bytes[^2] != 0xFF || bytes[^1] != 0xD9))
            {
                // A missing EOC used to REJECT the response outright. That threw away perfectly
                // usable data: JPEG2000 is progressive and SL assets are routinely stored without
                // a terminating EOC, so the real viewer decodes such streams on purpose. Measured
                // 2026-08-02 on OSGrid: 14 textures in a single view failed here, every one of
                // them having already passed the Content-Length check -- i.e. the body was
                // complete, just not EOC-terminated -- and the objects using them rendered white
                // while Firestorm drew them fine.
                //
                // Kept as a WARNING, not a failure: the check was added to catch chunked-encoding
                // truncation that Content-Length cannot see, and that concern is real. It is just
                // not decidable from the EOC alone. Hand the bytes to the tolerant decoder
                // instead; AssetService already detects a degraded decode and retries, which
                // distinguishes "genuinely truncated" from "simply not EOC-terminated" by the one
                // thing that actually settles it -- whether it decodes.
                // if (_fetchFailureLogged.TryAdd(textureId, 0))
                //     Console.Error.WriteLine($"[TextureFetch] {textureId}: no EOC marker " +
                //         $"({bytes.Length} bytes, tail {bytes[^2]:X2}{bytes[^1]:X2}) — decoding anyway");
            }

            return bytes;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GridSession] HTTP range texture fetch failed for {textureId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Fetches a GLTF PBR Material asset from the simulator. Returns null if the fetch fails.
    /// </summary>
    public async Task<LibreMetaverse.Assets.AssetMaterial?> FetchMaterialDataAsync(Guid materialId)
    {
        var asset = await _client.Assets
            .RequestAssetAsync(new UUID(materialId), AssetType.Material, true, CancellationToken.None)
            .ConfigureAwait(false);
        return asset as LibreMetaverse.Assets.AssetMaterial;
    }

    /// <summary>
    /// Fetches the raw bytes of an animation asset from the simulator.
    /// </summary>
    public async Task<byte[]?> FetchAnimationDataAsync(Guid animId)
    {
        var asset = await _client.Assets
            .RequestAssetAsync(new UUID(animId), AssetType.Animation, true, CancellationToken.None)
            .ConfigureAwait(false);
        return asset?.AssetData;
    }

    public void Dispose()
    {
        _client.Self.ChatFromSimulator -= OnChatFromSimulator;
        _client.Objects.ObjectUpdate -= OnObjectUpdate;
        _client.Objects.TerseObjectUpdate -= OnTerseObjectUpdate;
        _client.Objects.ObjectPropertiesFamily -= OnObjectPropertiesFamily;
        _client.Objects.ObjectProperties -= OnObjectPropertiesFull;
        _client.Objects.PhysicsProperties -= OnPhysicsProperties;
        _client.Avatars.UUIDNameReply -= OnUUIDNameReply;
        _client.Avatars.DisplayNameUpdate -= OnDisplayNameUpdate;
        _client.Groups.GroupNamesReply -= OnGroupNamesReply;
        _client.Self.AlertMessage -= OnAlertMessage;
        _client.Objects.KillObject -= OnKillObject;
        _client.Objects.KillObjects -= OnKillObjects;
        _client.Terrain.LandPatchReceived -= OnLandPatchReceived;
        _client.Network.SimConnected -= OnSimConnected;
        _client.Network.SimDisconnected -= OnSimDisconnected;
        _client.Appearance.AppearanceSet -= OnAppearanceSet;
        _client.Friends.FriendOnline -= OnFriendOnline;
        _client.Friends.FriendOffline -= OnFriendOffline;
        _client.Self.IM -= OnInstantMessage;
        _client.Self.ScriptDialog -= OnScriptDialog;
        _client.Avatars.AvatarPropertiesReply -= OnAvatarPropertiesReply;
        _client.Avatars.AvatarInterestsReply -= OnAvatarInterestsReply;
        _client.Avatars.AvatarGroupsReply -= OnAvatarGroupsReply;
        _client.Avatars.AvatarPicksReply -= OnAvatarPicksReply;
        _client.Avatars.PickInfoReply -= OnPickInfoReply;
        _client.Avatars.AvatarClassifiedReply -= OnAvatarClassifiedReply;
        _client.Network.UnregisterCallback(PacketType.ObjectUpdate, OnRawObjectUpdatePacket);
        Logout();
    }
}
