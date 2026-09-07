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
public sealed class GridSession : IDisposable, IWorldEventSource
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

    /// <summary>FEAT-UI-16: the set of items the local avatar is wearing may have changed (a bake
    /// completed, an appearance relay arrived). Raised on a LibreMetaverse network thread — marshal
    /// before touching a scene node. The inventory "Worn" tab also polls while visible, so a missed
    /// raise (e.g. an attachment attach/detach, which has no clean LMV event) self-heals.</summary>
    public event EventHandler? WornItemsChanged;

    public event EventHandler<FriendStatusEvent>? FriendStatusChanged;
    public event EventHandler<InstantMessageEvent>? InstantMessageReceived;
    public event EventHandler<ScriptDialogEvent>? ScriptDialogReceived;

    /// <summary>M5-3 Phase 2: the agent's group memberships, after <see cref="RequestGroups"/>.
    /// Raised on a LibreMetaverse network thread — marshal before touching a scene node.</summary>
    public event EventHandler<GroupsUpdatedEvent>? GroupsUpdated;
    /// <inheritdoc cref="GroupsUpdated"/>
    public event EventHandler<GroupChatMessageEvent>? GroupChatMessageReceived;
    /// <inheritdoc cref="GroupsUpdated"/>
    public event EventHandler<GroupChatJoinedEvent>? GroupChatJoined;
    /// <inheritdoc cref="GroupsUpdated"/>
    public event EventHandler<GroupInvitationEvent>? GroupInvitationReceived;

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
    internal void RaiseObjectRemoved(ObjectRemovedEvent e) => ObjectRemovedReceived?.Invoke(this, e);
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
            LibreMetaverse.Logger.SetLoggerFactory(
                Microsoft.Extensions.Logging.LoggerFactory.Create(b => b
                    .SetMinimumLevel(lmvLevel)
                    .AddSimpleConsole(o => o.SingleLine = true)),
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
        _client.Groups.CurrentGroups += OnCurrentGroups;
        _client.Self.GroupChatJoined += OnGroupChatJoined;
        _client.Self.AlertMessage += OnAlertMessage;
        _client.Objects.KillObject += OnKillObject;
        _client.Objects.KillObjects += OnKillObjects;
        _client.Terrain.LandPatchReceived += OnLandPatchReceived;
        _client.Network.SimConnected += OnSimConnected;
        _client.Network.SimDisconnected += OnSimDisconnected;
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

        // MVP2-3: region radar (minimap) and grid-map tile resolution.
        _client.Grid.CoarseLocationUpdate += OnCoarseLocationUpdate;
        _client.Grid.GridRegion += OnGridRegion;

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

        // BUG-NET-09: a PARCEL-only environment edit (someone changes just the parcel you're
        // standing on, not the whole region) has no reachable push signal at all -- see this
        // field's own doc comment for why -- so the only way to notice one is to ask again
        // periodically. Started here, for the session's lifetime; stopped in Dispose.
        _ = Task.Run(() => ParcelEnvironmentPollLoopAsync(_parcelEnvironmentPollCts.Token));
    }

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
            _appearanceReadinessLogged = false; // FEAT-AVATAR-01: re-check the wearable-edit path for the new region
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
    /// now -- remove it from the world immediately instead of waiting for the server. A same-grid
    /// walking crossing (dx/dy always ≤ 1) is left entirely alone: that is BUG-NET-03's existing,
    /// working path, and this must not race or duplicate it.</summary>
    private void OnSimChanged(object? sender, LibreMetaverse.SimChangedEventArgs e)
    {
        var oldSim = e.PreviousSimulator;
        var newSim = _client.Network.CurrentSim;
        if (oldSim == null || newSim == null || oldSim.Handle == newSim.Handle) return;

        var (dx, dy) = RegionGridOffset(oldSim.Handle, newSim.Handle);
        if (Math.Abs(dx) <= 1 && Math.Abs(dy) <= 1) return; // still a plausible neighbor -- leave to DisableSimulator

        Console.WriteLine($"[Teleport] left {oldSim.Name} ({oldSim.Handle}) {dx:+0;-0;0},{dy:+0;-0;0} region-steps away -- removing eagerly, not waiting for DisableSimulator");
        RegionDisconnectedReceived?.Invoke(this, new RegionDisconnectedEvent(oldSim.Handle));
    }

    /// <summary>Guards <see cref="OnEventQueueRunning"/>'s environment capture against firing more
    /// than once per <c>Simulator</c> instance -- see that method's doc comment for why this exists
    /// (BUG-NET-08).</summary>
    private Simulator? _environmentCapturedForSim;

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

    /// <summary>Gates the one-time diagnostic log of the resolved legacy EnvironmentSettings
    /// capability URI -- see its use in <see cref="FetchRegionEnvironmentAsync"/>.</summary>
    private bool _legacyEnvironmentCapLogged;

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
        // BUG-NET-03: since MultipleSims connects neighbor circuits, avatar updates now also arrive
        // from neighbor sims -- including our OWN avatar as a child agent there, with a different
        // LocalId. Feeding those to WorldSimulation flipped the local agent entity's RegionHandle/
        // LocalId back and forth, tearing down and rebuilding the Bento skeleton on every packet
        // and leaving stale BoneAttachment3D nodes behind (ObjectDisposedException spam from
        // AvatarRenderer.UpdateAttachment). We don't render neighbor-region avatars yet, so the
        // current sim stays the sole authority for avatars, exactly as before MultipleSims.
        if (e.Simulator != _client.Network.CurrentSim) return;

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
        // A group invitation is its own dialog (3) and would otherwise fall through both branches
        // below and vanish -- which is exactly what "die Gruppeneinladung kam nicht an" was.
        //
        // Deliberately NOT via LibreMetaverse's own GroupManager.GroupInvitation event: that one
        // fires synchronously and then immediately sends accept-or-decline based on
        // GroupInvitationEventArgs.Accept, which defaults to FALSE (GroupManager.cs:1099-1125).
        // Subscribing to it while asking the user first would auto-DECLINE every invitation --
        // worse than not handling it at all. Leaving it unsubscribed makes that handler a no-op
        // (it early-returns when nothing is listening), so we answer on our own schedule instead.
        if (e.IM.Dialog == InstantMessageDialog.GroupInvitation)
        {
            GroupInvitationReceived?.Invoke(this, new GroupInvitationEvent(
                // llimprocessing.cpp:864 -- the group id travels in FromAgentID for an invite sent
                // by the group itself, and the reply is addressed to it (send_improved_im(group_id,
                // ..., transaction_id), llviewermessage.cpp:681). See the DTO for the aux-id gap.
                e.IM.FromAgentID.Guid,
                e.IM.IMSessionID.Guid,
                e.IM.FromAgentName ?? string.Empty,
                e.IM.Message ?? string.Empty,
                ParseGroupInvitationFee(e.IM.BinaryBucket)));
            return;
        }

        // Group chat first, and NOT by inspecting the dialog byte: it arrives as
        // InstantMessageDialog.SessionSend, not MessageFromAgent, and its GroupIM flag is only set
        // on the first message of a session -- a later one carries just the session id. Both the
        // old `Dialog != MessageFromAgent` test and the old `|| e.IM.GroupIM` bail therefore
        // dropped group chat, twice over. LibreMetaverse's own AgentManager.IsGroupMessage is the
        // authoritative test (GroupIM || the session is a known group chat session), so use it
        // rather than re-deriving the rule here.
        if (_client.Self.IsGroupMessage(e.IM))
        {
            if (string.IsNullOrEmpty(e.IM.Message)) return; // typing/keep-alive, same as local chat
            // For group chat the session id IS the group id.
            GroupChatMessageReceived?.Invoke(this, new GroupChatMessageEvent(
                e.IM.IMSessionID.Guid, e.IM.FromAgentID.Guid, e.IM.FromAgentName, e.IM.Message));
            return;
        }

        if (e.IM.Dialog != InstantMessageDialog.MessageFromAgent) return;

        InstantMessageReceived?.Invoke(this, new InstantMessageEvent(
            e.IM.FromAgentID.Guid, e.IM.FromAgentName, e.IM.Message, e.IM.IMSessionID.Guid));
    }

    /// <summary>Sends a 1:1 instant message.</summary>
    public void SendInstantMessage(Guid targetAgentId, string message)
    {
        if (_client.Network.Connected)
            _client.Self.InstantMessage(new UUID(targetAgentId), message);
    }

    // ---- M5-3 Phase 2: groups + group chat -------------------------------------------------

    /// <summary>Asks the sim for the agent's group memberships. The answer arrives asynchronously
    /// on <see cref="GroupsUpdated"/> (a network-thread event — marshal before touching the UI);
    /// <see cref="GetGroups"/> then returns it without another round trip.</summary>
    public void RequestGroups()
    {
        if (_client.Network.Connected) _client.Groups.RequestCurrentGroups();
    }

    /// <summary>Snapshot of the agent's group memberships, or empty until the first
    /// <see cref="GroupsUpdated"/> has landed. Sorted by name so the UI needs no opinion.</summary>
    public IReadOnlyList<GroupEntry> GetGroups()
    {
        var snapshot = _groups;
        return snapshot ?? (IReadOnlyList<GroupEntry>)Array.Empty<GroupEntry>();
    }

    /// <summary>Last group list received, replaced wholesale by <see cref="OnCurrentGroups"/>.
    /// Read from the Godot main thread and written from a network thread, so it is swapped as a
    /// single reference rather than mutated in place — the same buffer-and-publish discipline
    /// AGENTS.md requires for world state.</summary>
    private volatile IReadOnlyList<GroupEntry>? _groups;

    private void OnCurrentGroups(object? sender, CurrentGroupsEventArgs e)
    {
        var list = new List<GroupEntry>(e.Groups.Count);
        foreach (var g in e.Groups.Values)
        {
            // Cache the name too: group chat lines and object owners resolve through the same
            // shared name cache, and a membership reply is a free source for it.
            _nameCache[g.ID.Guid] = g.Name ?? string.Empty;
            list.Add(new GroupEntry(
                g.ID.Guid, g.Name ?? string.Empty, g.MemberTitle ?? string.Empty,
                g.InsigniaID.Guid, g.AcceptNotices));
        }
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        _groups = list;
        GroupsUpdated?.Invoke(this, new GroupsUpdatedEvent(list));
    }

    /// <summary>Joins a group's chat session. Required before <see cref="SendGroupMessage"/> can
    /// deliver anything — LibreMetaverse refuses to send into a session it has not joined. Result
    /// arrives on <see cref="GroupChatJoined"/>.</summary>
    public void JoinGroupChat(Guid groupId)
    {
        if (groupId == Guid.Empty || !_client.Network.Connected) return;
        _client.Self.RequestJoinGroupChat(new UUID(groupId));
    }

    /// <summary>Leaves a group's chat session (closing its tab), so the sim stops delivering it.</summary>
    public void LeaveGroupChat(Guid groupId)
    {
        if (groupId == Guid.Empty || !_client.Network.Connected) return;
        _client.Self.RequestLeaveGroupChat(new UUID(groupId));
    }

    /// <summary>Sends a message to a group chat session. No-op unless the session was joined
    /// first (see <see cref="JoinGroupChat"/>) — LibreMetaverse logs an error and drops it.</summary>
    public void SendGroupMessage(Guid groupId, string message)
    {
        if (groupId == Guid.Empty || string.IsNullOrEmpty(message) || !_client.Network.Connected) return;
        _client.Self.InstantMessageGroup(new UUID(groupId), message);
    }

    /// <summary>Membership fee out of a group invitation's binary bucket. The viewer reads that
    /// bucket as <c>{ S32 membership_fee; LLUUID role_id; }</c> and rejects an invitation whose
    /// bucket is not exactly that size (llimprocessing.cpp:846-857); the S32 is network byte
    /// order. A wrong size here means an unparseable bucket, not a free group, but the invitation
    /// itself is still worth showing — so this reports 0 rather than dropping it, and the fee is
    /// only ever displayed.</summary>
    private static int ParseGroupInvitationFee(byte[]? bucket)
    {
        const int ExpectedSize = 4 + 16; // S32 membership_fee + UUID role_id
        if (bucket == null || bucket.Length != ExpectedSize) return 0;
        return (bucket[0] << 24) | (bucket[1] << 16) | (bucket[2] << 8) | bucket[3];
    }

    /// <summary>Accepts or declines a pending group invitation. Both answers are sent — declining
    /// silently is not the same thing to the server as never answering.</summary>
    public void RespondToGroupInvitation(Guid groupId, Guid sessionId, bool accept)
    {
        if (groupId == Guid.Empty || !_client.Network.Connected) return;
        _client.Self.GroupInviteRespond(new UUID(groupId), new UUID(sessionId), accept);
        // Membership only changes server-side after the accept lands; re-ask so the Groups tab
        // catches up without needing a relog.
        if (accept) _client.Groups.RequestCurrentGroups();
    }

    private void OnGroupChatJoined(object? sender, GroupChatJoinedEventArgs e)
    {
        GroupChatJoined?.Invoke(this, new GroupChatJoinedEvent(
            e.SessionID.Guid, e.SessionName ?? string.Empty, e.Success));
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
        if (e.AvatarID == _client.Self.AgentID)
        {
            if (!_visualParamsLogged)
            {
                _visualParamsLogged = true;
                LogVisualParamHealth();
            }

            // FEAT-AVATAR-01: remember the simulator's own relay of our shape. This array is in
            // Group0ParamIds (wire/decoder) order, which is what AvatarShapeService reads it as --
            // see _lastSelfRelayVisualParams. It is the ONLY trustworthy shape source for the self
            // avatar; LMV's MyVisualParameters is built in a DIFFERENT order (see OnAppearanceSet).
            if (VisualParamsHealthy(e.VisualParams?.ToArray()))
                _lastSelfRelayVisualParams = e.VisualParams!.ToArray();

            // The appearance workflow is off, so LMV's MyVisualParameters stays empty -- seed it
            // so a diagnostic read shows something truthful.
            TrySeedVisualParams(e.VisualParams);
        }

        // FEAT-UI-16: a self appearance relay can change the worn wearable set.
        if (e.AvatarID == _client.Self.AgentID)
            WornItemsChanged?.Invoke(this, EventArgs.Empty);

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

        // FEAT-AVATAR-01: the simulator's own view of our baked textures -- the last set known to
        // actually work, since some other viewer produced them. Kept as the reference the bake
        // diagnostic compares LibreMetaverse's (possibly empty) Textures[] against, and the
        // fallback any future send must use rather than writing empty bake ids.
        if (e.AvatarID == _client.Self.AgentID && textures.Count > 0)
        {
            _lastSelfRelayBakes = new Dictionary<int, Guid>(textures);
            _selfAppearanceWithBakesSeen = true;
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

        // Remembered for the local appearance refresh after our own bake, which has no incoming
        // event to read a hover height from and must not silently reset it to zero.
        if (e.AvatarID == _client.Self.AgentID) _lastSelfHoverOffsetZ = hoverOffsetZ;

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

    /// <summary>The modern SL VisualParams set is 218 bytes; a materially shorter relay is
    /// incomplete. FEAT-AVATAR-01.</summary>
    internal const int MinHealthyVisualParams = 200;

    private bool _visualParamsSeeded;

    /// <summary>True if <paramref name="vp"/> reads as a genuine shape: long enough, and not the
    /// all-zero / all-128 array of a never-populated default set.</summary>
    internal static bool VisualParamsHealthy(byte[]? vp)
    {
        if (vp == null || vp.Length < MinHealthyVisualParams) return false;
        int defaulted = 0;
        foreach (var b in vp)
            if (b == 0 || b == 128) defaulted++;
        return defaulted < vp.Length;
    }

    /// <summary>Copies the simulator's self-appearance VisualParams into
    /// <c>AppearanceManager.MyVisualParameters</c> once, when LibreMetaverse holds nothing better,
    /// so <c>LogVisualParamHealth()</c> reports a real value while LMV's own workflow is disabled.
    ///
    /// NOTE: this does NOT make a wearable-edit rebake safe. <c>AppearanceManager.MakeAppearancePacket</c>
    /// rebuilds every param from the decoded <c>wearable.Asset</c> (falling back to
    /// <c>DefaultValue</c> when the asset was never downloaded) and then OVERWRITES
    /// <c>MyVisualParameters</c> — it never reads the seeded value. FEAT-AVATAR-01 Phase 1's
    /// wearable send is reverted for exactly this reason.</summary>
    private void TrySeedVisualParams(List<byte>? incoming)
    {
        if (_visualParamsSeeded) return;
        var arr = incoming?.ToArray();
        if (!VisualParamsHealthy(arr)) return;
        if (VisualParamsHealthy(_client.Appearance.MyVisualParameters)
            && _client.Appearance.MyVisualParameters.Length >= arr!.Length)
        {
            _visualParamsSeeded = true; // LMV already has an equal-or-better set
            return;
        }

        _client.Appearance.MyVisualParameters = arr!;
        _visualParamsSeeded = true;
        Console.Error.WriteLine($"[VisualParams] seeded {arr!.Length} params from self AvatarAppearance relay (diagnostic only)");
    }

    /// <summary>Whether a "Wear" / "Detach" on an inventory item targets a system wearable
    /// (Clothing/Bodypart layer — Alpha, Skin, Shape, Tattoo, Universal, …) rather than an
    /// attachment. FEAT-AVATAR-01.</summary>
    internal enum WearableKind { Attachment, Wearable }

    /// <summary>Classifies a resolved inventory item. Pure function of the two facts that decide it,
    /// so it unit-tests without a live client: LibreMetaverse types it as an
    /// <c>InventoryWearable</c>, or its asset type is Clothing (5) or Bodypart (13).</summary>
    internal static WearableKind ClassifyItem(bool isInventoryWearable, int assetType)
        => isInventoryWearable
           || assetType == (int)LibreMetaverse.AssetType.Clothing
           || assetType == (int)LibreMetaverse.AssetType.Bodypart
            ? WearableKind.Wearable
            : WearableKind.Attachment;

    /// <summary>FEAT-AVATAR-01: true only when the current region does the full SL server-side-baking
    /// handshake — advertises the <c>AgentAppearanceService</c> protocol AND registers the
    /// <c>UpdateAvatarAppearance</c> capability. Here a wearable edit needs no local preparation:
    /// <c>RemoveFromOutfit</c>/<c>AddToOutfit</c> reach <c>RequestSetAppearanceAsync</c>'s SSB branch,
    /// whose only outgoing request is a cap POST of <c>{ cof_version }</c> — no visual params, no
    /// shape — and the server composites. This is an SL path; OpenSim's own server-side appearance
    /// (XBakes) does NOT set these (<c>RequestSetAppearanceAsync</c> ~3264 and the comment at
    /// <c>MakeAppearancePacket</c> ~2952 both say "always false on OpenSim"), so on OpenSim this is
    /// false and the wearable edit takes the client-side path — safe only after
    /// <see cref="EnsureWornWearablesDecodedAsync"/>.</summary>
    public bool RegionHasServerSideBaking()
        => _client.Network.Connected
           && _client.Appearance.ServerBakingRegion()
           && _client.Network.CurrentSim?.Caps?.CapabilityURI("UpdateAvatarAppearance") != null;

    private bool _appearanceReadinessLogged;

    /// <summary>Logs, once per region, that system-wearable edits are disabled and why — so "the
    /// alpha layer won't come off" has an answer in the log. FEAT-AVATAR-01.</summary>
    private void LogAppearanceEditReadiness()
    {
        if (_appearanceReadinessLogged) return;
        _appearanceReadinessLogged = true;
        string region = _client.Network.CurrentSim?.Name ?? "?";
        Console.Error.WriteLine($"[Appearance] {region}: system-wearable edits enabled — every bake is " +
            "followed by a corrected AgentSetAppearance: LibreMetaverse puts 195 of 218 visual " +
            "params in the wrong slot AND truncates the wire array from 253 to 218 (FEAT-AVATAR-01)");
    }

    /// <summary>FEAT-AVATAR-01: raised when the corrected <c>AgentSetAppearance</c> failed its
    /// pre-send verification and was therefore NOT sent — meaning LibreMetaverse's scrambled packet
    /// is what the grid now holds and the avatar needs repairing in another viewer. Payload is the
    /// verification failure, for a user-facing notice. Should never fire; if it does, that is the
    /// signal to stop editing wearables.</summary>
    public event EventHandler<string>? WearableEditUnavailable;

    /// <summary>Raised when a wardrobe edit is refused because Second Life does not allow it — a
    /// body part being taken off, say. Separate from <see cref="WearableEditUnavailable"/>, which
    /// means "this should work and did not": this one carries a finished explanation for the user,
    /// not a failure.</summary>
    public event EventHandler<string>? WearableEditRefused;

    // FEAT-AVATAR-01 — system-wearable remove/add. Blocked on an upstream bug, MEASURED not guessed.
    //
    // AppearanceManager.MakeAppearancePacket builds the outgoing AgentSetAppearance by iterating
    // VisualParams.Params (ALL 672 params) and taking the first 218, but the wire order is
    // VisualParams.Group0ParamIds (the 253 TRANSMITTED ids). They agree for 23 slots and diverge
    // from index 23 on -- 195 of 218 values land on the WRONG parameter, and the sim persists that.
    // Pinned by SLNG.Assets.Tests.VisualParamOrderTests.
    //
    // Every route into AppearanceManager hits it: AddToOutfit / RemoveFromOutfit /
    // ReplaceOutfitAsync / RequestSetAppearance all reach MakeAppearancePacket. That is the single
    // explanation for all three live incidents (2026-08-02 deformed, 2026-08-29 flat, 2026-08-31
    // torn rigged head), and no amount of preparing the wearables first can fix it -- the three
    // attempts that tried are in the spec.
    //
    // The way out, implemented here, needs no fork: let LibreMetaverse do the half it gets right
    // (compositing and uploading the bake textures), then immediately send a CORRECTED
    // AgentSetAppearance over the top -- MakeAppearancePacket() is public, so its fresh TextureEntry
    // is reusable while VisualParam[] and AgentData.Size are rebuilt by AgentAppearanceParams.
    // OpenSim's LLClientView.HandlerAgentSetAppearance never reads AgentData.SerialNum and never
    // rejects a repeat, so last write wins and the corrected packet is what gets stored. See
    // SendCorrectedAppearance below and the spec for the trade-off this accepts.

    /// <summary>Sends the legacy <c>AgentWearablesRequest</c> and waits for the simulator's
    /// <c>AgentWearablesUpdate</c>, which is what populates <c>AppearanceManager.Wearables</c>.
    ///
    /// <para>LibreMetaverse would send this itself at login, but only under
    /// <c>Settings.Agent.SendAppearance</c> — which stays off (see the ctor) — so with the flag off
    /// nobody ever asks and the worn set is empty for the whole session. That empty set is why the
    /// "Angezogen" tab showed every Clothing/Bodypart layer as "(nicht aktiv)": <c>GetWornItems</c>
    /// marks an item live from <c>GetWearables()</c>, and could only ever see the COF link instead.
    /// It is also what a wearable edit needs to build an appearance from.</para>
    ///
    /// <para>Mirrors LibreMetaverse's own private <c>GatherAgentWearablesViaLLUDPAsync</c>. Prefer
    /// this over the COF route (<c>RequestAgentWornAsync</c>): measured 2026-08-31, that one returns
    /// empty on OSGrid, while OpenSim answers this packet reliably. Best-effort — returns on the
    /// reply or after a 10 s timeout, and never throws into the caller.</para></summary>
    private async Task RequestWornWearablesViaLludpAsync(CancellationToken ct)
    {
        if (!_client.Network.Connected) return;

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnReply(object? s, LibreMetaverse.AgentWearablesReplyEventArgs e) => tcs.TrySetResult(true);

        _client.Appearance.AgentWearablesReply += OnReply;
        try
        {
            var request = new LibreMetaverse.Packets.AgentWearablesRequestPacket
            {
                AgentData = { AgentID = _client.Self.AgentID, SessionID = _client.Self.SessionID }
            };
            _client.Network.SendPacket(request);

            await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10), ct)).ConfigureAwait(false);

            int count = _client.Appearance.GetWearables().Count();
            Console.Error.WriteLine(count > 0
                ? $"[Appearance] worn wearables resolved: {count}"
                : "[Appearance] no worn wearables returned — the Worn tab will show layers as inactive");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Appearance] AgentWearablesRequest failed: {ex.Message}");
        }
        finally
        {
            _client.Appearance.AgentWearablesReply -= OnReply;
        }
    }

    /// <summary>Sends an <c>AgentSetAppearance</c> whose visual parameters are in the order the
    /// simulator actually reads, replacing the scrambled one LibreMetaverse just sent.
    ///
    /// <para>Called from <see cref="OnAppearanceSet"/>, i.e. after LibreMetaverse has finished a
    /// bake: at that point it has composited and uploaded the baked textures (its baker is correct)
    /// and has already sent its own packet with <b>195 of 218 params on the wrong parameter</b>.
    /// <c>MakeAppearancePacket()</c> is public, so the good half — the freshly baked
    /// <c>TextureEntry</c> and the wearable cache blocks — can be reused verbatim while
    /// <c>VisualParam[]</c> and <c>AgentData.Size</c> are rebuilt correctly by
    /// <see cref="AgentAppearanceParams"/>.</para>
    ///
    /// <para>This works because OpenSim's <c>LLClientView.HandlerAgentSetAppearance</c> never looks
    /// at <c>AgentData.SerialNum</c> and never rejects a repeat — last write wins. There is
    /// therefore a brief window (until this lands) in which the scrambled appearance is the stored
    /// one; that is the accepted trade-off of not forking LibreMetaverse.</para>
    ///
    /// <para>Refuses to send if the built array does not verify
    /// (<see cref="AgentAppearanceParams.VerifyRoundTrip"/>) — leaving LibreMetaverse's bad packet
    /// standing is worse than nothing, but sending a second unverified one is worse still.</para></summary>
    private void SendCorrectedAppearance()
    {
        if (!_client.Network.Connected) return;

        // Belt and braces. With SendAppearance off and no direct RequestSetAppearance call left,
        // LibreMetaverse never bakes, so OnAppearanceSet never fires and this is unreachable --
        // but "unreachable" is exactly what was assumed about the login send that broke the avatar.
        // The one thing this method must never do is transmit while writing is disabled.
        if (!_client.Settings.Agent.SendAppearance)
        {
            Console.Error.WriteLine("[Appearance] correction suppressed: appearance writing is disabled");
            return;
        }

        try
        {
            // The bake decoded these; Asset.Params is the wearable's own paramId -> weight map.
            var wearableParams = _client.Appearance.GetWearables()
                .Where(w => w.Asset != null)
                .Select(w => (IReadOnlyDictionary<int, float>)w.Asset!.Params)
                .ToList();

            var packet = _client.Appearance.MakeAppearancePacket();
            // Deliberately NOT packet.VisualParam.Length: LibreMetaverse allocates 218 (251 with a
            // Physics layer) while the wire actually carries 253 -- the simulator's own relay was
            // measured at 253, matching Group0ParamIds. Following LMV's length would truncate 35
            // parameters on top of the ordering bug. OpenSim reads the length dynamically.
            int length = AgentAppearanceParams.DefaultLength;

            byte[] wire;
            if (wearableParams.Count > 0)
            {
                wire = AgentAppearanceParams.BuildWireArray(wearableParams, length);
                if (!AgentAppearanceParams.VerifyRoundTrip(wire, wearableParams, out var failure))
                {
                    // Fall through to the relay below rather than returning: LibreMetaverse has
                    // ALREADY sent its scrambled packet by the time we get here, so staying silent
                    // leaves that as the account's stored shape. Overwriting with the simulator's
                    // own last relay is always at least as good as what it already had.
                    Console.Error.WriteLine($"[Appearance] built params rejected ({failure}) -- falling back to the simulator's relay");
                    wire = Array.Empty<byte>();
                }
            }
            else
            {
                wire = Array.Empty<byte>();
            }

            if (wire.Length == 0)
            {
                // No usable wearable-derived shape -- happens on the LOGIN bake, where
                // LibreMetaverse can complete without any wearable asset decoded. Send back the
                // shape the simulator itself last told us, which is already in wire order and is by
                // definition what the account holds. Measured 2026-08-31: an early return here left
                // LibreMetaverse's scrambled login packet standing and broke the avatar, which is
                // why this path must still SEND rather than skip.
                if (_lastSelfRelayVisualParams.Length == 0)
                {
                    Console.Error.WriteLine("[Appearance] correction NOT sent: no decoded wearables AND no simulator relay yet " +
                        "-- LibreMetaverse's packet stands, appearance may be wrong until a rebake");
                    WearableEditUnavailable?.Invoke(this, "no shape available to correct with");
                    return;
                }

                wire = _lastSelfRelayVisualParams.Length > length
                    ? _lastSelfRelayVisualParams.Take(length).ToArray()
                    : _lastSelfRelayVisualParams;
                Console.Error.WriteLine($"[Appearance] no decoded wearables -- restoring the simulator's own last shape ({wire.Length} params)");
            }

            // Bake textures. LibreMetaverse only composites when SendAppearance is on, and it is
            // not -- so its Textures[] can be all-zero, and a packet built from it says "I have no
            // baked textures". The simulator persists that and the avatar renders untextured for
            // everyone until something re-bakes it: measured live 2026-08-31, the head lost its
            // texture on the grid and only came back after a Firestorm login. Fill any empty slot
            // from the simulator's own last relay, and refuse outright if that still leaves a hole.
            var currentBakes = new Dictionary<int, Guid>();
            var teBytes = packet.ObjectData.TextureEntry;
            var entry = teBytes is { Length: > 1 }
                ? new Primitive.TextureEntry(teBytes, 0, teBytes.Length) : null;
            foreach (int slot in AgentAppearanceParams.EssentialBakeSlots)
            {
                var face = entry?.FaceTextures is { } fs && slot < fs.Length ? fs[slot] : null;
                var id = face?.TextureID ?? LibreMetaverse.UUID.Zero;
                currentBakes[slot] = id == AppearanceManager.DEFAULT_AVATAR_TEXTURE ? Guid.Empty : id.Guid;
            }

            var merged = AgentAppearanceParams.MergeBakeSlots(currentBakes, _lastSelfRelayBakes, out bool bakesComplete);
            if (!bakesComplete)
            {
                Console.Error.WriteLine("[Appearance] correction NOT sent -- incomplete bake set " +
                    $"({string.Join(" ", merged.OrderBy(k => k.Key).Select(k => $"{k.Key}={(k.Value == Guid.Empty ? "EMPTY" : "ok")}"))}); " +
                    "sending it would strip the avatar's textures");
                WearableEditUnavailable?.Invoke(this, "incomplete bake set");
                return;
            }

            if (entry != null)
            {
                foreach (var kv in merged) entry.CreateFace((uint)kv.Key).TextureID = new LibreMetaverse.UUID(kv.Value);
                packet.ObjectData.TextureEntry = entry.GetBytes();
            }

            var blocks = new LibreMetaverse.Packets.AgentSetAppearancePacket.VisualParamBlock[wire.Length];
            for (int i = 0; i < wire.Length; i++)
                blocks[i] = new LibreMetaverse.Packets.AgentSetAppearancePacket.VisualParamBlock { ParamValue = wire[i] };
            packet.VisualParam = blocks;

            // Height is derived from params 33/198/503/682/692/842, which LibreMetaverse reads off
            // whatever its mis-ordered loop landed on -- so its Size is wrong for the same reason.
            packet.AgentData.Size = new Vector3(0.45f, 0.6f,
                AgentAppearanceParams.ComputeAgentHeight(wearableParams));

            _client.Network.SendPacket(packet);

            // Keep LibreMetaverse's own copy consistent with what the grid now holds, so anything
            // else reading it is not looking at the scrambled array.
            _client.Appearance.MyVisualParameters = wire;

            int nonDefault = wire.Count(b => b != 0);
            int preserved = merged.Count(kv => currentBakes[kv.Key] == Guid.Empty);
            Console.Error.WriteLine($"[Appearance] corrected AgentSetAppearance sent " +
                $"({wire.Length} params, {nonDefault} non-zero, height {packet.AgentData.Size.Z:F2} m, " +
                $"{preserved}/{merged.Count} bake slots preserved from the previous appearance)");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Appearance] correction failed: {ex.Message}");
        }
    }

    /// <summary>FEAT-AVATAR-01: recomposites the baked textures from the currently worn set and
    /// re-sends the appearance — the manual rebake every viewer offers (Firestorm: Ctrl+Alt+R).
    /// Same corrected path as a wearable edit: LibreMetaverse bakes, then
    /// <see cref="SendCorrectedAppearance"/> replaces its scrambled packet. Use it when a wearable
    /// change did not visibly take.</summary>
    /// <summary>FEAT-AVATAR-01: runs the real bake locally and reports what it produced, WITHOUT
    /// uploading and WITHOUT sending anything.
    ///
    /// <para>This is possible because LibreMetaverse's pieces are all public — <c>Baker</c>,
    /// <c>AppearanceManager.DecodeWearableParams</c>, <c>BakeTypeToTextures</c> and the
    /// <c>TextureProvider</c> — so the compositing step can be driven on its own instead of
    /// through <c>RequestSetAppearance</c>, which bakes, uploads AND sends in one call. Every
    /// previous attempt to learn anything about the bake had to send to find out; this does not.</para>
    ///
    /// <para>Mirrors <c>CreateBakeAsync</c>: gather the worn wearables, decode their assets, let
    /// <c>DecodeWearableParams</c> fill the per-index <c>TextureData</c>, fetch those textures,
    /// then feed each bake channel's indices to a <c>Baker</c> and report the result size.</para></summary>
    private IBakeTextureEncoder? _bakeEncoder;

    /// <summary>Supplies the JPEG2000 encoder used for avatar bakes. Set by the composition root
    /// (<c>app</c>), because the codec lives in <c>SLNG.Assets</c> and the layering forbids
    /// <c>SLNG.Net</c> from referencing it — see <see cref="IBakeTextureEncoder"/>.</summary>
    public void UseBakeEncoder(IBakeTextureEncoder encoder) => _bakeEncoder = encoder;

    /// <summary>Builds a <see cref="ManagedImage"/> from tightly packed 8-bit BGRA — the inverse of
    /// <see cref="ToBgra"/>, so a generated pattern can be handed to the baker as if it had been
    /// downloaded and decoded like any other texture.</summary>
    private static ManagedImage ToManagedImage(byte[] bgra, int size)
    {
        var image = new ManagedImage(size, size,
            ManagedImage.ImageChannels.Color | ManagedImage.ImageChannels.Alpha);

        for (int i = 0; i < size * size; i++)
        {
            image.Blue[i] = bgra[i * 4 + 0];
            image.Green[i] = bgra[i * 4 + 1];
            image.Red[i] = bgra[i * 4 + 2];
            image.Alpha[i] = bgra[i * 4 + 3];
        }

        return image;
    }

    /// <summary>Converts a composited bake into the tightly packed 8-bit BGRA the encoder expects.
    /// Mirrors <c>ManagedImage.ExportBitmap</c> so the two cannot drift, but stays in plain bytes so
    /// no SkiaSharp type has to cross into this assembly.</summary>
    private static byte[] ToBgra(ManagedImage img)
    {
        int n = img.Width * img.Height;
        var raw = new byte[n * 4];
        // Test lengths, not nulls: ManagedImage initialises every channel to Array.Empty<byte>(),
        // so an absent channel is an empty array and a null check passes straight into an
        // out-of-range read. Measured on the Color-only EyesIris texture.
        bool color = img.Red.Length >= n && img.Green.Length >= n && img.Blue.Length >= n;
        bool alpha = img.Alpha.Length >= n;

        for (int i = 0; i < n; i++)
        {
            if (color)
            {
                raw[i * 4 + 0] = img.Blue![i];
                raw[i * 4 + 1] = img.Green![i];
                raw[i * 4 + 2] = img.Red![i];
            }
            else if (alpha)
            {
                // Alpha-only layer: replicate to RGB the way ExportBitmap does.
                raw[i * 4 + 0] = raw[i * 4 + 1] = raw[i * 4 + 2] = img.Alpha![i];
            }
            raw[i * 4 + 3] = color && alpha ? img.Alpha![i] : byte.MaxValue;
        }

        return raw;
    }

    /// <summary>
    /// FEAT-AVATAR-01: tells the grid what the avatar now looks like — the last step of the bake.
    ///
    /// <para>Everything here was built from measurement, because this exact packet corrupted a real
    /// avatar three times. The visual parameters come from <see cref="AgentAppearanceParams"/>,
    /// which orders them the way the wire does; LibreMetaverse's own encoder mis-orders 195 of 218
    /// and truncates 35 more. The baked-texture ids come from bakes this client composited, encoded
    /// and uploaded itself, verified against the reference viewer's own bakes for the same avatar at
    /// a mean channel difference of 2.6/255.</para>
    ///
    /// <para>Three refusals stand in front of the send, in order of how badly each failed before:
    /// the parameter array must survive a round trip, every essential bake slot must hold a real id,
    /// and an incomplete set falls back to the ids the simulator already had rather than sending
    /// empties — an empty slot is persisted and renders the avatar untextured for everyone.</para>
    /// </summary>
    /// <param name="bakes">Bake slot (<c>AvatarTextureIndex</c>) → uploaded asset id.</param>
    /// <param name="wearableParams">The worn wearables' decoded paramId → weight maps, in layer order.</param>
    /// <returns>True when the packet was sent.</returns>
    private bool SendAppearanceFromOwnBake(
        IReadOnlyDictionary<int, LibreMetaverse.UUID> bakes,
        IReadOnlyList<IReadOnlyDictionary<int, float>> wearableParams)
    {
        if (!_client.Network.Connected)
        {
            Console.Error.WriteLine("[Appearance] not sent: not connected");
            return false;
        }

        // 1. Shape. Never LibreMetaverse's array -- see AgentAppearanceParams for why.
        byte[] wire;
        if (wearableParams.Count > 0)
        {
            wire = AgentAppearanceParams.BuildWireArray(wearableParams);
            if (!AgentAppearanceParams.VerifyRoundTrip(wire, wearableParams, out var failure))
            {
                Console.Error.WriteLine($"[Appearance] NOT sent: built params failed verification ({failure})");
                return false;
            }
        }
        else if (_lastSelfRelayVisualParams.Length > 0)
        {
            // No decoded wearables: send back the shape the simulator itself last reported, which is
            // already in wire order and is by definition what the account holds.
            wire = _lastSelfRelayVisualParams;
            Console.Error.WriteLine($"[Appearance] no decoded wearables -- keeping the simulator's own shape ({wire.Length} params)");
        }
        else
        {
            Console.Error.WriteLine("[Appearance] NOT sent: no shape to send (no wearables decoded, no relay seen)");
            return false;
        }

        // 2. Bake slots. Anything we did not upload falls back to what the grid already had; a hole
        //    after that means refusing, because sending an empty slot strips the avatar for everyone.
        var current = bakes.ToDictionary(kv => kv.Key, kv => kv.Value.Guid);
        var merged = AgentAppearanceParams.MergeBakeSlots(current, _lastSelfRelayBakes, out bool complete);
        if (!complete)
        {
            Console.Error.WriteLine("[Appearance] NOT sent: incomplete bake set (" +
                string.Join(" ", merged.OrderBy(k => k.Key).Select(k => $"{k.Key}={(k.Value == Guid.Empty ? "EMPTY" : "ok")}")) +
                ") -- sending it would strip the avatar's textures");
            return false;
        }

        // 3. Texture entry: the bake ids in their slots, the default avatar texture everywhere else,
        //    which is what a viewer sends.
        var entry = new Primitive.TextureEntry(AppearanceManager.DEFAULT_AVATAR_TEXTURE);
        foreach (var kv in merged)
            entry.CreateFace((uint)kv.Key).TextureID = new LibreMetaverse.UUID(kv.Value);

        var packet = new LibreMetaverse.Packets.AgentSetAppearancePacket
        {
            AgentData =
            {
                AgentID = _client.Self.AgentID,
                SessionID = _client.Self.SessionID,
                SerialNum = _appearanceSerial++,
                // Height comes from params 33/198/503/682/692/842, resolved by id rather than by
                // loop position -- LibreMetaverse reads them off whatever its mis-ordered loop
                // landed on, so its Size is wrong for the same reason its array is.
                Size = new Vector3(0.45f, 0.6f, AgentAppearanceParams.ComputeAgentHeight(wearableParams)),
            },
            ObjectData = { TextureEntry = entry.GetBytes() },
            VisualParam = wire
                .Select(b => new LibreMetaverse.Packets.AgentSetAppearancePacket.VisualParamBlock { ParamValue = b })
                .ToArray(),
            WearableData = Array.Empty<LibreMetaverse.Packets.AgentSetAppearancePacket.WearableDataBlock>(),
        };

        _client.Network.SendPacket(packet);

        // Keep LibreMetaverse's own copy consistent with what the grid now holds, so anything else
        // reading it is not looking at the scrambled array.
        _client.Appearance.MyVisualParameters = wire;
        foreach (var kv in merged) _lastSelfRelayBakes[kv.Key] = kv.Value;

        int fromUs = merged.Count(kv => current.TryGetValue(kv.Key, out var c) && c == kv.Value && c != Guid.Empty);
        Console.Error.WriteLine($"[Appearance] SENT: {wire.Length} params, height {packet.AgentData.Size.Z:F2} m, " +
            $"{fromUs}/{merged.Count} bake slots from this bake -- " +
            string.Join(" ", merged.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value.ToString()[..8]}")));

        // Tell our own renderer what we just baked. Nothing else will: the simulator stores an
        // AgentSetAppearance but does not echo an AvatarAppearance back to the sender, so the only
        // path that carries new bake ids to the scene never fires for the local avatar. Measured
        // 2026-09-01 -- the avatar stayed untextured after a correct bake and correct upload, and
        // came up right on the next login, which is the same ids arriving through the login path
        // instead. A viewer knows its own bake; this is the local half of that.
        AvatarAppearanceReceived?.Invoke(this, new AvatarAppearanceEvent(
            _client.Network.CurrentSim?.Handle ?? 0,
            _client.Self.AgentID.Guid,
            wire,
            merged.ToDictionary(kv => kv.Key, kv => kv.Value),
            _lastSelfHoverOffsetZ));

        return true;
    }

    private uint _appearanceSerial = 1;

    /// <summary>Builds the worn set from the Current Outfit Folder, which is what actually defines
    /// what an avatar wears — and, unlike the legacy <c>AgentWearablesReply</c>, can hold several
    /// layers of one type. Also reports every link it finds, since "which of my layers does the
    /// client see" turned out to be the question behind a wrong face.</summary>
    private async Task<List<AppearanceManager.WearableData>> CollectWornWearablesForBakeAsync(bool verbose, CancellationToken ct)
    {
        var worn = new List<AppearanceManager.WearableData>();
        var cof = _client.Inventory.FindFolderForType(LibreMetaverse.FolderType.CurrentOutfit);
        if (cof == LibreMetaverse.UUID.Zero) return worn;

        // Ask for the folder rather than reading the store: the store only holds what has already
        // been fetched, and at bake time nothing has opened the inventory yet. Reading it straight
        // reported an empty COF and silently fell back to the region's stale list.
        var links = await _client.Inventory.FolderContentsAsync(
            cof, _client.Self.AgentID, fetchFolders: false, fetchItems: true,
            LibreMetaverse.InventorySortOrder.ByName, ct).ConfigureAwait(false);
        if (links == null) return worn;

        // A COF entry is a link; the wearable it points at is a separate item that also has to be
        // present before its type and asset can be read.
        var targets = new Dictionary<LibreMetaverse.UUID, LibreMetaverse.UUID>();
        foreach (var entry in links)
        {
            if (entry is not LibreMetaverse.InventoryItem link) continue;
            if (link.AssetType == LibreMetaverse.AssetType.LinkFolder) continue;
            var target = link.IsLink() ? link.ResolvedItemID : link.UUID;
            if (target != LibreMetaverse.UUID.Zero) targets[target] = _client.Self.AgentID;
        }

        var store = _client.Inventory.Store;
        if (targets.Count > 0 && targets.Keys.Any(id => store?.GetNodeOrDefault(id)?.Data is not LibreMetaverse.InventoryWearable))
        {
            try
            {
                _client.Inventory.RequestFetchInventory(targets);
                // No completion event covers a bulk fetch, so give the replies a moment to land.
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* best effort -- unresolved targets are reported below */ }
        }

        // One link per wearable. A COF can hold several links to the same item -- this one holds 20
        // for 10 wearables, each item linked twice, once with an ordering token and once without.
        // Wearing a layer twice draws it twice, and the untokened copy sinks to the bottom of the
        // stack, so a duplicate of an opaque skin quietly reappears underneath everything. Keep the
        // link that carries a valid token; it is the one the viewer's own ordering is built on.
        int unresolved = 0, duplicates = 0;
        var chosen = new Dictionary<LibreMetaverse.UUID, (LibreMetaverse.InventoryWearable Wearable, string? Description)>();
        var order = new List<LibreMetaverse.UUID>();

        foreach (var entry in links)
        {
            if (entry is not LibreMetaverse.InventoryItem link) continue;
            if (link.AssetType == LibreMetaverse.AssetType.LinkFolder) continue;

            var target = link.IsLink() ? link.ResolvedItemID : link.UUID;
            if (target == LibreMetaverse.UUID.Zero) continue;

            if (store?.GetNodeOrDefault(target)?.Data is not LibreMetaverse.InventoryWearable w)
            {
                // Attachments live in the COF too, so only count links that stayed unresolved.
                if (link.AssetType is LibreMetaverse.AssetType.Clothing or LibreMetaverse.AssetType.Bodypart)
                    unresolved++;
                continue;
            }

            bool tokened = WearableLayerOrder.IsValidOrderString(link.Description, (int)w.WearableType);

            if (chosen.TryGetValue(target, out var existing))
            {
                duplicates++;
                bool existingTokened = WearableLayerOrder.IsValidOrderString(existing.Description, (int)w.WearableType);
                if (tokened && !existingTokened) chosen[target] = (w, link.Description);
                continue;
            }

            chosen[target] = (w, link.Description);
            order.Add(target);
        }

        foreach (var target in order)
        {
            var (w, description) = chosen[target];
            worn.Add(new AppearanceManager.WearableData
            {
                ItemID = target,
                AssetID = w.AssetUUID,
                AssetType = w.AssetType,
                WearableType = w.WearableType,
            });
            _cofLinkDescriptions[target] = description ?? string.Empty;

            if (verbose)
                Console.Error.WriteLine($"[Bake]   COF {w.WearableType,-10} \"{w.Name}\"" +
                    (string.IsNullOrEmpty(description) ? "" : $"  desc=\"{description}\""));
        }

        if (duplicates > 0 && verbose)
            Console.Error.WriteLine($"[Bake]   COF: dropped {duplicates} duplicate link(s) -- the outfit folder links some items more than once");
        if (unresolved > 0)
            Console.Error.WriteLine($"[Bake]   COF: {unresolved} wearable link(s) did not resolve -- baking would miss them");

        return worn;
    }

    /// <summary>Layer-ordering tokens read off the COF links while collecting the worn set, kept so
    /// the ordering step uses the same link the wearable was chosen from rather than looking the
    /// folder up again — with duplicate links present, a second lookup can pick the other one.</summary>
    private readonly Dictionary<LibreMetaverse.UUID, string> _cofLinkDescriptions = new();

    /// <summary>Puts the worn wearables into the layer order Second Life actually stacks them in:
    /// grouped by type, and within a type sorted by the ordering token the viewer stores in the
    /// Current Outfit Folder link's description. Bottom layer first — see
    /// <see cref="WearableLayerOrder"/> for the rule and its source.</summary>
    private List<AppearanceManager.WearableData> OrderWearablesAsTheViewerDoes(List<AppearanceManager.WearableData> worn, bool verbose)
    {
        // The tokens recorded while the worn set was collected. Deliberately not a fresh folder
        // lookup: with duplicate links present, looking up again can land on the other link -- the
        // untokened one -- and lose the ordering that was just resolved.
        var descriptions = _cofLinkDescriptions;

        var result = new List<AppearanceManager.WearableData>();
        foreach (var group in worn.GroupBy(w => w.WearableType))
        {
            result.AddRange(WearableLayerOrder.Sort(
                group,
                (int)group.Key,
                w => descriptions.TryGetValue(w.ItemID, out var d) ? d : null,
                w => w.ItemID.ToString()));
        }

        int tokened = worn.Count(w => descriptions.TryGetValue(w.ItemID, out var d)
                                      && WearableLayerOrder.IsValidOrderString(d, (int)w.WearableType));
        if (verbose)
            Console.Error.WriteLine($"[Bake] layer order: {tokened}/{worn.Count} wearables carry a COF ordering token");

        return result;
    }

    /// <summary>Writes a bake input or result out as a PNG so it can be looked at. Everything about
    /// this task that was decided from numbers alone turned out to be decidable only from the
    /// picture.
    ///
    /// Refuses on a Linden grid (TPV Policy §2.b). The "in_" / "ref_" dumps are DECODED wearable
    /// textures worn by whoever is currently baking -- other creators' skins, tattoos, clothing
    /// layers -- written to disk as plain PNGs. That is an export SL's own viewer has no
    /// equivalent of, and the policy requires verifying the SL creator name matches the viewer
    /// user's own before any such export, "including content that may be set to 'full
    /// permissions.'" No such check exists here, so the safe answer on SL is not to write the
    /// file at all; OpenSim carries no such restriction, and this is the only environment where
    /// SLNG_BAKE_VERBOSE has ever been used to chase a bake defect.</summary>
    private void DumpPreview(string name, ManagedImage? image)
    {
        if (image?.Red == null || _bakeEncoder == null) return;
        if (_isLindenGrid)
        {
            Console.Error.WriteLine($"[Bake]   preview '{name}' skipped -- disabled on a Linden grid (TPV Policy §2.b)");
            return;
        }

        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "slng_bake");
            Directory.CreateDirectory(dir);
            var png = _bakeEncoder.EncodePreviewPng(ToBgra(image), image.Width, image.Height);
            if (png.Length == 0) return;

            var path = Path.Combine(dir, $"{name}.png");
            File.WriteAllBytes(path, png);
            Console.Error.WriteLine($"[Bake]   preview -> {path}");
        }
        catch (Exception ex) { Console.Error.WriteLine($"[Bake]   preview '{name}' failed: {ex.Message}"); }
    }

    /// <summary>
    /// FEAT-AVATAR-01: creates a known-answer test skin in the inventory — three generated textures
    /// plus a Skin bodypart that references them. See <see cref="TestSkinTextures"/> for why a
    /// synthetic skin answers questions a real one cannot.
    ///
    /// <para>Creates inventory items and uploads assets, so it is a deliberate action rather than
    /// part of any automatic path. It does not wear anything: the new skin appears in Body Parts and
    /// is put on like any other, which keeps the thing being tested (wearing a skin and rebaking)
    /// the thing the tester actually does.</para>
    ///
    /// <para>Refused on a Linden grid: three texture uploads and one inventory-item creation are
    /// real L$ upload fees on Agni, spent on a diagnostic tool this task built specifically for
    /// the OpenSim (XBakes) bake path -- see <see cref="BakeAvatarAsync"/>'s own SSB guard, which
    /// this mirrors. Nothing here is needed to test SL: a real skin already answers the same
    /// question there.</para>
    /// </summary>
    /// <returns>A short status line for the chat.</returns>
    public async Task<string> CreateTestSkinAsync(CancellationToken ct = default)
    {
        if (!_client.Network.Connected) return "nicht verbunden";
        if (_bakeEncoder == null) return "kein Bake-Encoder verfügbar";
        if (_isLindenGrid)
        {
            Console.Error.WriteLine("[TestSkin] skipped: refused on a Linden grid -- costs real upload fees for a diagnostic OpenSim-only tool");
            return "Auf Second Life gesperrt — das Testmuster ist ein OpenSim-Diagnosewerkzeug und würde echte Upload-Gebühren kosten.";
        }

        try
        {
            const uint all = (uint)LibreMetaverse.PermissionMask.All;
            var perms = new LibreMetaverse.Permissions(all, all, all, all, all);

            var textureFolder = _client.Inventory.FindFolderForType(LibreMetaverse.FolderType.Texture);
            var bodypartFolder = _client.Inventory.FindFolderForType(LibreMetaverse.FolderType.BodyPart);
            // Plain ASCII, no colon and no dash: the legacy create packet mangled a name
            // containing "07:46:24 — Kopf" into a hex dump truncated at the colon.
            string stamp = DateTime.Now.ToString("HHmmss");

            Console.Error.WriteLine($"[TestSkin] folders: textures={textureFolder} bodyparts={bodypartFolder}");
            if (bodypartFolder == LibreMetaverse.UUID.Zero || textureFolder == LibreMetaverse.UUID.Zero)
                return "Inventarordner (Texturen / Körperteile) noch nicht geladen — Inventar einmal öffnen und erneut versuchen";

            var slots = new[]
            {
                (Slot: AvatarTextureIndex.HeadBodypaint, Label: "Kopf", Ascii: "Head"),
                (Slot: AvatarTextureIndex.UpperBodypaint, Label: "Oberkörper", Ascii: "Upper"),
                (Slot: AvatarTextureIndex.LowerBodypaint, Label: "Unterkörper", Ascii: "Lower"),
            };

            var textures = new Dictionary<AvatarTextureIndex, LibreMetaverse.UUID>();
            foreach (var (slot, label, ascii) in slots)
            {
                var bgra = TestSkinTextures.Build(slot);
                var encoder = _bakeEncoder;
                var j2k = await Task.Run(
                    () => encoder.EncodeBake(bgra, TestSkinTextures.Size, TestSkinTextures.Size), ct)
                    .ConfigureAwait(false);
                if (j2k.Length == 0) return $"Testtextur ({label}) konnte nicht kodiert werden";

                var (ok, texItem, assetId, how) = await CreateInventoryItemVerifiedAsync(
                    j2k, $"SLNG Testhaut {stamp} {ascii}", "Generierte Testtextur (FEAT-AVATAR-01)",
                    LibreMetaverse.AssetType.Texture, LibreMetaverse.InventoryType.Texture,
                    wearableType: null, textureFolder, perms, ct).ConfigureAwait(false);

                if (!ok || assetId == LibreMetaverse.UUID.Zero)
                    return $"Upload der {label}-Textur fehlgeschlagen ({how}) — Details im Log";

                textures[slot] = assetId;
                Console.Error.WriteLine($"[TestSkin] {label}: item={texItem} asset={assetId} " +
                    $"({j2k.Length} bytes, via {how})");
            }

            // The wearable itself. No visual params on purpose: a skin's colour params tint every
            // layer it contributes, and the whole point here is that what comes out is exactly what
            // went in.
            var skin = new LibreMetaverse.Assets.AssetBodypart
            {
                Name = $"SLNG Testhaut {stamp}",
                Description = "Bekannte Farben pro Kanal (Kopf grün, Oberkörper blau, Unterkörper rot)",
                WearableType = LibreMetaverse.WearableType.Skin,
                Creator = _client.Self.AgentID,
                Owner = _client.Self.AgentID,
                LastOwner = _client.Self.AgentID,
                Permissions = perms,
            };
            foreach (var kv in textures) skin.Textures[kv.Key] = kv.Value;

            // A skin with no visual params at all is not something a viewer ever writes, and an
            // asset that no viewer would produce is a poor thing to test a grid with. These are the
            // three the skin wearable is defined by -- rainbow, ruddiness, pigment -- at neutral
            // values, so the test colours come through exactly as generated.
            skin.Params[108] = 0f;  // rainbow colour
            skin.Params[110] = 0f;  // red skin (ruddiness)
            skin.Params[111] = 0.5f; // pigment
            skin.Encode();

            var (skinOk, itemId, _, skinHow) = await CreateInventoryItemVerifiedAsync(
                skin.AssetData, skin.Name, skin.Description,
                LibreMetaverse.AssetType.Bodypart, LibreMetaverse.InventoryType.Wearable,
                LibreMetaverse.WearableType.Skin, bodypartFolder, perms, ct).ConfigureAwait(false);

            if (!skinOk || itemId == LibreMetaverse.UUID.Zero)
                return $"Anlegen der Testhaut fehlgeschlagen ({skinHow}) — Details im Log";

            Console.Error.WriteLine($"[TestSkin] created \"{skin.Name}\" ({itemId}) via {skinHow}");

            // Verify rather than trust the response. Measured 2026-09-01: the grid returned real
            // item and asset ids for all four creates, and after a relog not one of them was in the
            // inventory. A create call that reports success and leaves nothing behind is worse than
            // one that fails, because it sends the user looking for something that is not there.
            return $"\"{skin.Name}\" liegt in Körperteile — anziehen, dann neu backen. " +
                   "Kopf grün, Oberkörper blau, Unterkörper rot.";
        }
        catch (OperationCanceledException) { return "abgebrochen"; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TestSkin] failed: {ex}");
            return $"Testhaut fehlgeschlagen: {ex.Message}";
        }
    }

    /// <summary>
    /// Creates one inventory item from asset data, and confirms it exists afterwards.
    ///
    /// <para>Two paths, because the modern one is not dependable. Measured on OSGrid 2026-09-01:
    /// <c>NewFileAgentInventory</c> answered every create with a real <c>new_inventory_item</c> and
    /// <c>new_asset</c> — which is all LibreMetaverse checks before reporting success — and stored
    /// nothing. Four items, none of them in their folder afterwards, none of them there after a
    /// relog. So the capability is tried, the result is verified against the folder, and on failure
    /// the legacy transaction path is used instead: upload the asset, then create the item
    /// referencing the same transaction id.</para>
    ///
    /// <para>The verification is the point. A create that reports success and leaves nothing behind
    /// is worse than one that fails, because it sends the user looking for something that is not
    /// there — which is exactly what happened.</para>
    /// </summary>
    private async Task<(bool Ok, LibreMetaverse.UUID ItemId, LibreMetaverse.UUID AssetId, string How)>
        CreateInventoryItemVerifiedAsync(
            byte[] data, string name, string description,
            LibreMetaverse.AssetType assetType, LibreMetaverse.InventoryType invType,
            LibreMetaverse.WearableType? wearableType, LibreMetaverse.UUID folder,
            LibreMetaverse.Permissions perms, CancellationToken ct)
    {
        // 1. The capability.
        try
        {
            var (ok, status, itemId, assetId) = await _client.Inventory.RequestCreateItemFromAssetAsync(
                data, name, description, assetType, invType, folder, perms, ct).ConfigureAwait(false);

            if (ok && itemId != LibreMetaverse.UUID.Zero
                   && await FolderHoldsAsync(folder, itemId, "verify(cap)", ct).ConfigureAwait(false))
            {
                return (true, itemId, assetId, "capability");
            }

            Console.Error.WriteLine($"[Inventory] NewFileAgentInventory did not store \"{name}\" " +
                $"(ok={ok}, status='{status}') -- falling back to the legacy transaction path");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Inventory] NewFileAgentInventory failed for \"{name}\": {ex.Message}");
        }

        // 2. The legacy path: upload the asset under a transaction id, then create the item that
        //    references it. Older, and on this grid the one that is actually wired up.
        try
        {
            var transaction = LibreMetaverse.UUID.Random();
            var assetId = await _client.Assets
                .RequestUploadAsync(assetType, data, storeLocal: false, transaction, ct).ConfigureAwait(false);
            if (assetId == LibreMetaverse.UUID.Zero)
            {
                Console.Error.WriteLine($"[Inventory] legacy upload of \"{name}\" returned no asset id");
                return (false, LibreMetaverse.UUID.Zero, LibreMetaverse.UUID.Zero, "legacy-upload-failed");
            }

            var item = wearableType.HasValue
                ? await _client.Inventory.CreateItemAsync(folder, name, description, assetType, transaction,
                    invType, wearableType.Value, LibreMetaverse.PermissionMask.All, ct).ConfigureAwait(false)
                : await _client.Inventory.CreateItemAsync(folder, name, description, assetType, transaction,
                    invType, LibreMetaverse.PermissionMask.All, ct).ConfigureAwait(false);

            var itemId = item?.UUID ?? LibreMetaverse.UUID.Zero;
            Console.Error.WriteLine($"[Inventory] legacy create \"{name}\": asset={assetId} item=" +
                (item == null ? "NULL (no CreateInventoryItem reply)" : itemId.ToString()));

            if (itemId == LibreMetaverse.UUID.Zero)
                return (false, LibreMetaverse.UUID.Zero, assetId, "legacy-no-item");

            // The sim indexes a new item a moment after acknowledging it, so a single immediate
            // listing can miss one that is really there. Give it one retry before calling it lost --
            // reporting a working create as a failure is its own kind of wrong answer.
            bool present = await FolderHoldsAsync(folder, itemId, "verify(legacy)", ct).ConfigureAwait(false);
            if (!present)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                present = await FolderHoldsAsync(folder, itemId, "verify(legacy, retry)", ct).ConfigureAwait(false);
            }

            return (present, itemId, assetId, present ? "legacy" : "legacy-not-stored");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Inventory] legacy create of \"{name}\" failed: {ex.Message}");
            return (false, LibreMetaverse.UUID.Zero, LibreMetaverse.UUID.Zero, "legacy-threw");
        }
    }

    /// <summary>Re-reads a folder from the grid and reports what is in it — the check that separates
    /// "the create call answered with an id" from "the item exists". Pass <c>UUID.Zero</c> to just
    /// list the folder.</summary>
    private async Task<bool> FolderHoldsAsync(
        LibreMetaverse.UUID folder, LibreMetaverse.UUID item, string label, CancellationToken ct)
    {
        try
        {
            var listing = await _client.Inventory.FolderContentsAsync(
                folder, _client.Self.AgentID, fetchFolders: false, fetchItems: true,
                LibreMetaverse.InventorySortOrder.ByName, ct).ConfigureAwait(false);

            bool present = item != LibreMetaverse.UUID.Zero && listing?.Any(e => e.UUID == item) == true;
            var names = listing?.Take(6).Select(e => e.Name) ?? Enumerable.Empty<string>();

            Console.Error.WriteLine($"[TestSkin] {label}: {listing?.Count ?? -1} item(s)" +
                (item != LibreMetaverse.UUID.Zero ? $", the new one is {(present ? "PRESENT" : "MISSING")}" : "") +
                (names.Any() ? "  [" + string.Join(" | ", names) + "]" : ""));
            return present;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TestSkin] {label}: listing failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>FEAT-AVATAR-01's manual bake path: "Avatar neu backen" and "Testmuster backen".
    /// Composites locally, uploads the result as textures, and sends a bake-carrying
    /// <c>AgentSetAppearance</c> -- the OpenSim (XBakes) path, needed only because that server does
    /// not composite for the client.
    ///
    /// <b>Refuses on a region with real Second Life server-side baking</b> (see
    /// <see cref="RegionHasServerSideBaking"/>). SL composites bakes itself from an
    /// <c>UpdateAvatarAppearance</c> cap POST of <c>{ cof_version }</c> -- no visual params, no
    /// shape, no uploaded textures. Running this path there anyway would upload textures the
    /// server already has no use for and hand-send a raw <c>AgentSetAppearance</c> alongside SL's
    /// own pipeline: an unrequested protocol departure (TPV Policy §1.a) and the most likely way to
    /// leave the avatar looking wrong to everyone else in the room. The wearable-edit path already
    /// makes this distinction correctly (<c>WearWearableAsync</c>'s SSB branch); this is the same
    /// rule applied to the two menu commands that skip that path entirely.</summary>
    public async Task<string> BakeAvatarAsync(bool testPattern = false, CancellationToken ct = default)
    {
        if (!_client.Network.Connected)
        {
            Console.Error.WriteLine("[Bake] skipped: not connected");
            return "nicht verbunden";
        }

        if (RegionHasServerSideBaking())
        {
            Console.Error.WriteLine("[Bake] skipped: this region bakes server-side (Second Life) -- " +
                "the client-side composite path is for OpenSim (XBakes) only");
            return "Dieses Grid backt serverseitig — der lokale Bake ist nicht nötig und wird übersprungen.";
        }

        // The bake pipeline is proven, so its running commentary is noise on every wardrobe
        // change. Everything that diagnosed it stays one env var away; what remains by default is
        // the outcome plus anything that went wrong.
        bool verbose = Environment.GetEnvironmentVariable("SLNG_BAKE_VERBOSE") == "1";
        testPattern |= Environment.GetEnvironmentVariable("SLNG_BAKE_TESTPATTERN") == "1";

        try
        {
            // 1. Worn wearables. LLUDP first -- the COF route measured empty on OSGrid.
            await RequestWornWearablesViaLludpAsync(ct).ConfigureAwait(false);
            var legacy = _client.Appearance.GetWearables().ToList();

            // The Current Outfit Folder is what actually defines the worn set. The legacy
            // AgentWearablesReply is whatever the region last had written to it, and that is not the
            // same thing: measured 2026-08-31, the same code resolved 9 wearables before a Firestorm
            // login and 5 after it, because Firestorm rewrote the region's list on login. The four
            // that vanished were tattoo layers -- including the skin the avatar is actually wearing.
            // Baking from the region's copy would have replaced the user's face with a different one.
            var worn = await CollectWornWearablesForBakeAsync(verbose, ct).ConfigureAwait(false);
            if (verbose)
                Console.Error.WriteLine($"[Bake] worn wearables: COF {worn.Count}, region's legacy list {legacy.Count}");

            if (worn.Count == 0)
            {
                Console.Error.WriteLine("[Bake] COF empty -- falling back to the region's list");
                worn = legacy;
            }
            else if (worn.Count < legacy.Count)
            {
                // The COF is authoritative, but fewer entries than the region knows about usually
                // means links are still unresolved rather than genuinely unworn -- and baking from a
                // short set drops layers off the avatar. Say so instead of quietly proceeding.
                Console.Error.WriteLine("[Bake] WARNING: the COF resolved fewer wearables than the region lists; " +
                    "some links may not have loaded yet");
            }
            if (worn.Count == 0) { Console.Error.WriteLine("[Bake] nothing to bake from"); return "nichts zum Backen gefunden"; }

            // 2. Decode each wearable's asset -- DecodeWearableParams reads wearable.Asset.
            int decoded = 0;
            foreach (var w in worn)
            {
                if (w.Asset != null) { decoded++; continue; }
                try
                {
                    var asset = await _client.Assets
                        .RequestAssetAsync(w.AssetID, w.AssetType, priority: true, ct).ConfigureAwait(false);
                    if (asset is LibreMetaverse.Assets.AssetWearable aw && aw.Decode()) { w.Asset = aw; decoded++; }
                    else Console.Error.WriteLine($"[Bake]   {w.WearableType}: asset {w.AssetID} did not fetch/decode");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Console.Error.WriteLine($"[Bake]   {w.WearableType}: {ex.Message}"); }
            }
            if (decoded < worn.Count)
                Console.Error.WriteLine($"[Bake] WARNING: only {decoded}/{worn.Count} wearable assets decoded -- layers will be missing");

            // 2b. What each worn wearable actually declares. The Head bake came back with no skin
            //     texture at all -- its only input was Hair:32x32 -- so it composited the built-in
            //     Linden head instead of the avatar's own face. DecodeWearableParams is a straight
            //     copy of wearable.Asset.Textures with one exception: an entry pointing at
            //     DEFAULT_AVATAR_TEXTURE is mapped to Zero and disappears. This says which of the
            //     two it is -- a skin that declares no head texture, or one whose head texture is
            //     the default and is being dropped on purpose.
            foreach (var w in verbose ? worn.Where(w => w.Asset != null) : Enumerable.Empty<AppearanceManager.WearableData>())
            {
                var declared = w.Asset!.Textures;
                Console.Error.WriteLine($"[Bake]   worn {w.WearableType,-10} " +
                    (declared.Count == 0
                        ? "declares NO textures"
                        : string.Join("  ", declared.Select(e =>
                            $"{e.Key}=" + (e.Value == AppearanceManager.DEFAULT_AVATAR_TEXTURE ? "DEFAULT"
                                : e.Value == LibreMetaverse.UUID.Zero ? "ZERO"
                                : e.Value.ToString()[..8])))));
            }

            // 3. One layer per WEARABLE per texture slot -- not one per slot.
            //
            //    AppearanceManager keeps a single TextureData[] indexed by AvatarTextureIndex and
            //    calls DecodeWearableParams once per wearable into it, so each wearable overwrites
            //    the previous one's slot. Measured 2026-08-31 with five worn Tattoo layers: four
            //    distinct HeadTattoo textures and two UpperTattoo textures were silently discarded,
            //    and because the last tattoo declares HeadTattoo=DEFAULT (which maps to Zero) the
            //    head slot ended up empty. That is why the Head bake had no skin at all and
            //    composited the built-in Linden head.
            //
            //    The real viewer keeps a local texture per (slot, wearable) -- LLLocalTextureObject
            //    -- and Baker.Bake is already built for it: AddTexture appends to a flat list, the
            //    layer loop draws each in turn, and tattooTextures is a List. It simply never
            //    receives more than one. So give each wearable its own scratch array, which keeps
            //    LibreMetaverse's own colour and alpha-mask logic, and collect the results in wear
            //    order (bottom layer first, as SL stacks them).
            //    Order matters as much as membership: where two layers of one type overlap, the
            //    topmost wins, and two of the worn tattoos are fully opaque head skins. See
            //    WearableLayerOrder -- the position lives in the COF link's description, not in the
            //    order LibreMetaverse returns.
            var ordered = OrderWearablesAsTheViewerDoes(worn.Where(w => w.Asset != null).ToList(), verbose);

            var layers = new List<AppearanceManager.TextureData>();
            foreach (var w in ordered)
            {
                var scratch = new AppearanceManager.TextureData[(int)AvatarTextureIndex.NumberOfEntries];
                for (int i = 0; i < scratch.Length; i++) scratch[i] = new AppearanceManager.TextureData();
                AppearanceManager.DecodeWearableParams(w, ref scratch);

                for (int i = 0; i < scratch.Length; i++)
                {
                    if (scratch[i].TextureID == LibreMetaverse.UUID.Zero) continue;
                    scratch[i].TextureIndex = (AvatarTextureIndex)i;
                    layers.Add(scratch[i]);
                }
            }
            if (verbose)
                Console.Error.WriteLine($"[Bake] layers from {worn.Count(w => w.Asset != null)} wearables: {layers.Count}");

            // 4. Fetch every referenced texture.
            var wanted = layers.Select(t => t.TextureID).Distinct().ToList();
            if (verbose)
                Console.Error.WriteLine($"[Bake] textures referenced by the worn set: {wanted.Count}");
            int got = 0, decodedTex = 0;
            foreach (var id in wanted)
            {
                try
                {
                    var tex = await _client.Appearance.TextureProvider.RequestTextureAsync(id, ct).ConfigureAwait(false);
                    if (tex == null) { Console.Error.WriteLine($"[Bake]   texture {id} -> null"); continue; }
                    got++;

                    // Report the decode instead of swallowing it. AssetTexture.Decode runs
                    // J2kImage.DecodeToImage<SKBitmap>, which throws outright when CoreJ2K's Skia
                    // image creator is not registered -- and a bake composited from undecoded
                    // textures comes out blank, which looks like "the baker did nothing".
                    bool ok;
                    try { ok = tex.Decode(); }
                    catch (Exception dex) { ok = false; Console.Error.WriteLine($"[Bake]   texture {id} decode threw: {dex.Message}"); }

                    if (ok && tex.Image != null)
                    {
                        decodedTex++;
                        if (verbose)
                            Console.Error.WriteLine($"[Bake]   texture {id.ToString()[..8]} -> {tex.Image.Width}x{tex.Image.Height} " +
                                $"channels={tex.Image.Channels} ({tex.AssetData?.Length ?? 0} bytes asset)");
                    }
                    else
                    {
                        Console.Error.WriteLine($"[Bake]   texture {id.ToString()[..8]} -> decode FAILED " +
                            $"(ok={ok}, image={(tex.Image == null ? "null" : "set")}) -- this channel will bake blank");
                    }

                    // Every layer referencing this id -- several wearables can share one texture.
                    foreach (var t in layers) if (t.TextureID == id) t.Texture = tex;

                    // Write the input out too. The Head composite came back holding what looks like
                    // two faces, one of them inverted, while UpperBody composited cleanly -- so the
                    // question is whether a single input already looks like that or whether the
                    // layering produces it, and only the inputs themselves answer it.
                    if (verbose)
                    {
                        var slot = layers.FirstOrDefault(t => t.TextureID == id)?.TextureIndex;
                        DumpPreview($"in_{slot}_{id.ToString()[..8]}", tex.Image);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Console.Error.WriteLine($"[Bake]   texture {id} -> {ex.Message}"); }
            }
            if (verbose)
                Console.Error.WriteLine($"[Bake] textures downloaded: {got}/{wanted.Count}, decoded: {decodedTex}/{got}");
            else if (decodedTex < wanted.Count)
                Console.Error.WriteLine($"[Bake] WARNING: only {decodedTex}/{wanted.Count} textures decoded -- the bake will be incomplete");

            // 4b. Test pattern. The generated skin proved impossible to see: as a Skin it is the
            //     bottom of the stack and two worn tattoo layers are fully opaque, and getting an
            //     item to persist and stay worn on this grid cost two rounds without ever answering
            //     the question it was created for. Feeding the same known-answer textures straight
            //     into the bake as the topmost layer of each channel answers it directly -- no
            //     inventory item, no COF link, no layer ordering, nothing that can quietly cover it.
            if (testPattern)
            {
                foreach (var slot in new[]
                {
                    AvatarTextureIndex.HeadBodypaint,
                    AvatarTextureIndex.UpperBodypaint,
                    AvatarTextureIndex.LowerBodypaint,
                })
                {
                    layers.Add(new AppearanceManager.TextureData
                    {
                        TextureIndex = slot,
                        TextureID = LibreMetaverse.UUID.Random(),
                        Texture = new LibreMetaverse.Assets.AssetTexture(
                            ToManagedImage(TestSkinTextures.Build(slot), TestSkinTextures.Size)),
                    });
                }
                Console.Error.WriteLine("[Bake] TEST PATTERN on top of every channel " +
                    "(head green, upper body blue, lower body red)");
            }

            // 5. Bake each channel, and -- when asked -- upload it. Uploading is deliberately
            //    separated from sending: RequestUploadBakedTextureAsync goes through the
            //    UploadBakedTexture capability, which stores an asset and returns its id. It costs
            //    nothing, creates no inventory item and changes nothing about the avatar. What
            //    changes an avatar is the AgentSetAppearance that carries the new ids, and that
            //    still does not happen here. This is the step that proves the grid accepts a bake
            //    of this size before anything irreversible is built on top of it.
            // Baking applies by default now: the pipeline was verified against the reference
            // viewer's own bakes for this avatar (head 2.6/255) and confirmed in-world. SLNG_BAKE_DRY
            // still holds everything back, which is what to reach for if an outfit ever bakes wrong
            // -- it composites and previews without touching the account.
            bool send = Environment.GetEnvironmentVariable("SLNG_BAKE_DRY") != "1";
            bool upload = send;
            var uploaded = new Dictionary<int, LibreMetaverse.UUID>();
            bool sent = false;
            if (!send)
                Console.Error.WriteLine("[Bake] SLNG_BAKE_DRY=1 -- composited only; nothing uploaded, nothing sent");

            foreach (var bakeType in new[] { BakeType.Head, BakeType.UpperBody, BakeType.LowerBody, BakeType.Eyes, BakeType.Hair })
            {
                var indices = AppearanceManager.BakeTypeToTextures(bakeType);
                var oven = new LibreMetaverse.Imaging.Baker(bakeType);
                int fed = 0, usable = 0;
                var detail = new List<string>();
                // Every layer belonging to this channel, in wear order -- several may share a slot
                // (five tattoos all contribute a HeadTattoo), which is exactly what the old
                // one-slot-one-texture feed threw away.
                foreach (var t in layers.Where(l => indices.Contains(l.TextureIndex)))
                {
                    oven.AddTexture(t);
                    if (t.Texture == null) continue;
                    fed++;
                    // Decoded image present is what the baker can actually composite; a fetched
                    // but undecoded texture contributes nothing and is the difference between a
                    // real bake and a 507-byte blank.
                    if (t.Texture.Image != null) { usable++; detail.Add($"{t.TextureIndex}:{t.Texture.Image.Width}x{t.Texture.Image.Height}"); }
                    else detail.Add($"{t.TextureIndex}:UNDECODED");
                }

                await Task.Run(() => oven.Bake(), ct).ConfigureAwait(false);
                int bytes = oven.BakedTexture?.AssetData?.Length ?? 0;

                // Separate "the compositing wrote nothing" from "it composited and the ENCODER
                // produced nothing". Baker.Bake ends in AssetTexture.Encode ->
                // CompleteConfigurationPresets.Streaming.Encode(Image.ExportBitmap()), so a filled
                // ManagedImage with a tiny AssetData means the encode is at fault, while a uniform
                // image means DrawLayer never put anything in. Distinct red values is the cheapest
                // test: a blank image has exactly one.
                string composed = "image=null";
                var img = oven.BakedTexture?.Image;
                if (img?.Red != null)
                {
                    var seen = new HashSet<byte>();
                    for (int i = 0; i < img.Red.Length && seen.Count <= 8; i++) seen.Add(img.Red[i]);
                    composed = $"image={img.Width}x{img.Height} distinctRed={(seen.Count > 8 ? ">8" : seen.Count.ToString())}";
                }

                // Re-encode what the Baker composited. LibreMetaverse's own Encode is hardcoded to a
                // CoreJ2K preset that is broken in the pinned version -- see IBakeTextureEncoder --
                // so the bytes above are meaningless no matter how good the image is. This is the
                // number that says whether a bake could actually be uploaded.
                int reBytes = 0;
                byte[] slngBake = Array.Empty<byte>();
                if (img != null && _bakeEncoder != null)
                {
                    var bgra = ToBgra(img);
                    var encoder = _bakeEncoder;
                    int w = img.Width, h = img.Height;
                    slngBake = await Task.Run(() => encoder.EncodeBake(bgra, w, h), ct).ConfigureAwait(false);
                    reBytes = slngBake.Length;
                }

                // Write the composite out so it can actually be looked at. Only on request: these
                // are megabyte PNGs and a wardrobe change should not spend that every time.
                if (verbose) DumpPreview(bakeType.ToString(), img);

                string uploadNote = string.Empty;
                if (upload && reBytes > 0)
                {
                    try
                    {
                        var id = await _client.Assets.RequestUploadBakedTextureAsync(slngBake, ct).ConfigureAwait(false);
                        if (id != LibreMetaverse.UUID.Zero)
                        {
                            // Read it back. An upload capability answering with an asset id is not
                            // the same as the asset existing -- NewFileAgentInventory did exactly
                            // that for four inventory items on this grid -- and an appearance
                            // pointing at bake ids that resolve to nothing renders the avatar
                            // untextured, which is what SLNG shows while Firestorm, using its own
                            // bake, shows the outfit correctly.
                            var readBack = await _client.Appearance.TextureProvider
                                .RequestTextureAsync(id, ct).ConfigureAwait(false);
                            bool readable = false;
                            try { readable = readBack != null && readBack.Decode() && readBack.Image != null; }
                            catch { readable = false; }

                            uploaded[(int)AppearanceManager.BakeTypeToAgentTextureIndex(bakeType)] = id;
                            uploadNote = readable
                                ? $"  uploaded={id.ToString()[..8]} (verified {readBack!.Image!.Width}x{readBack.Image.Height})"
                                : $"  uploaded={id.ToString()[..8]} but READ-BACK FAILED -- the grid did not keep it";

                            if (!readable)
                                Console.Error.WriteLine($"[Bake] WARNING: {bakeType} bake {id} cannot be fetched back; " +
                                    "the avatar will render untextured for anyone using it");
                        }
                        else uploadNote = "  upload REJECTED (grid returned no asset id)";
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { uploadNote = $"  upload FAILED: {ex.Message}"; }
                }

                if (verbose)
                {
                    Console.Error.WriteLine($"[Bake] {bakeType,-10} inputs={indices.Count} withTexture={fed} usable={usable} " +
                        $"-> lmv {(bytes > 0 ? bytes + "B" : "NOTHING")} / slng {(reBytes > 0 ? reBytes + "B" : "NOTHING")}  {composed}" +
                        (detail.Count > 0 ? "  [" + string.Join(" ", detail) + "]" : "") + uploadNote);
                }
                else if (reBytes == 0)
                {
                    Console.Error.WriteLine($"[Bake] WARNING: {bakeType} produced no texture{uploadNote}");
                }
            }

            if (upload)
            {
                // A partial set is the dangerous case: an appearance built from it would carry empty
                // slots and strip the avatar. Report completeness explicitly so the next step can
                // refuse rather than discover it on a live avatar.
                var have = AgentAppearanceParams.EssentialBakeSlots.Count(s => uploaded.ContainsKey(s));
                if (verbose || have < AgentAppearanceParams.EssentialBakeSlots.Length)
                    Console.Error.WriteLine($"[Bake] uploaded {have}/{AgentAppearanceParams.EssentialBakeSlots.Length} " +
                    $"essential slots: {string.Join("  ", AgentAppearanceParams.EssentialBakeSlots.Select(s => $"{s}=" + (uploaded.TryGetValue(s, out var u) ? u.ToString()[..8] : "MISSING")))}");

                if (send)
                {
                    // The wearables in layer order, each as its decoded paramId -> weight map. Same
                    // list the bake was composited from, so shape and textures describe one outfit.
                    var wearableParams = ordered
                        .Where(w => w.Asset != null)
                        .Select(w => (IReadOnlyDictionary<int, float>)w.Asset!.Params)
                        .ToList();

                    sent = SendAppearanceFromOwnBake(uploaded, wearableParams);
                }

            }

            // 6. The reference: the bakes a working viewer produced for this same avatar, which the
            //    simulator still holds. Comparing our composite against those is the only check that
            //    says "right" rather than "plausible", and it costs nothing since they are ordinary
            //    texture assets. Skipped once we have sent, because the relay now holds OUR ids and
            //    fetching them would only compare the bake against itself.
            if (send || !verbose)
            {
                // Nothing to compare against once we have sent -- the relay now holds our own ids --
                // and five texture fetches are not worth spending on an unasked-for comparison.
            }
            else
            {
                foreach (var (slot, name) in new[] { (8, "Head"), (9, "UpperBody"), (10, "LowerBody"), (11, "Eyes"), (20, "Hair") })
                {
                    if (!_lastSelfRelayBakes.TryGetValue(slot, out var id) || id == Guid.Empty) continue;
                    try
                    {
                        var tex = await _client.Appearance.TextureProvider
                            .RequestTextureAsync(new LibreMetaverse.UUID(id), ct).ConfigureAwait(false);
                        if (tex == null) continue;
                        try { if (!tex.Decode()) continue; } catch { continue; }
                        DumpPreview($"ref_{name}_{id.ToString()[..8]}", tex.Image);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { Console.Error.WriteLine($"[Bake]   reference {name}: {ex.Message}"); }
                }
            }

            return send
                ? (sent ? (testPattern
                        ? "Testmuster gebacken und gesendet — Kopf grün, Oberkörper blau, Unterkörper rot."
                        : $"Aussehen neu gebacken ({worn.Count} Kleidungsstücke).")
                        : "Bake fertig, aber NICHT gesendet — Grund steht im Log ([Appearance]-Zeile).")
                : "Bake fertig — nichts gesendet (SLNG_BAKE_DRY=1).";
        }
        catch (OperationCanceledException) { return "abgebrochen"; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Bake] failed: {ex.Message}");
            return $"Bake fehlgeschlagen: {ex.Message}";
        }
    }

    /// <summary>"Avatar neu backen" -- forces a fresh appearance composite. FEAT-SL-01 audit
    /// finding: this was a COMPLETE NO-OP on every grid including SL, because the 2026-08-31 hard
    /// stop below refuses unconditionally. That stop is correct for OpenSim / legacy baking --
    /// <c>RequestSetAppearance</c> is NOT gated by <c>SendAppearance = false</c> on that path, so
    /// calling it sends whatever <c>MakeAppearancePacket</c> produces, which was the scrambled/
    /// all-zero send that broke this avatar repeatedly (see the stop's own comment). It is WRONG
    /// for a server-side-baking region: there, <c>RequestSetAppearance</c> never reaches
    /// <c>MakeAppearancePacket</c> at all -- verified against the pinned package's own branch
    /// (<c>AppearanceManager.RequestSetAppearanceAsync</c>: <c>useClientSideBaking = false</c> on
    /// SSB skips straight to <c>UpdateAvatarAppearanceAsync</c>, a <c>{ cof_version }</c> capability
    /// POST, no visual params, no textures) -- none of the Aug-31 concerns apply, and refusing it
    /// left "Avatar neu backen" silently doing nothing while the chat message claimed a bake had
    /// happened. On SL this is also the only way SLNG could ever ask the sim to re-push a self
    /// avatar's <c>AvatarAppearance</c> outside of an actual wearable edit -- e.g. after a login
    /// whose initial appearance never arrived or was dropped.</summary>
    public void RebakeAvatar()
    {
        if (!_client.Network.Connected)
        {
            Console.Error.WriteLine("[Appearance] rebake skipped: not connected");
            return;
        }

        if (RegionHasServerSideBaking())
        {
            Console.Error.WriteLine("[Appearance] rebake requested on a server-side-baking region -- " +
                "nudging the region to re-composite (UpdateAvatarAppearance cap)");
            _ = RequestServerSideRebakeAsync();
            return;
        }

        // Report the bake state first -- it is the one number that says whether a rebake can work
        // at all (all-ZERO means LibreMetaverse composited nothing), and it costs nothing.
        try
        {
            var packet = _client.Appearance.MakeAppearancePacket();
            var te = packet.ObjectData.TextureEntry;
            var entry = te is { Length: > 1 } ? new Primitive.TextureEntry(te, 0, te.Length) : null;

            var report = new List<string>();
            foreach (var idx in new[] { 8, 9, 10, 11, 20 }) // head, upper, lower, eyes, hair
            {
                var face = entry?.FaceTextures is { } faces && idx < faces.Length ? faces[idx] : null;
                var id = face?.TextureID ?? LibreMetaverse.UUID.Zero;
                report.Add($"{idx}=" + (id == LibreMetaverse.UUID.Zero ? "ZERO"
                    : id == AppearanceManager.DEFAULT_AVATAR_TEXTURE ? "DEFAULT"
                    : id.ToString()[..8]));
            }

            int wearables = _client.Appearance.GetWearables().Count();
            int decoded = _client.Appearance.GetWearables().Count(w => w.Asset != null);
            var relay = string.Join(" ", _lastSelfRelayBakes
                .Where(kv => kv.Key is 8 or 9 or 10 or 11 or 20)
                .OrderBy(kv => kv.Key)
                .Select(kv => $"{kv.Key}={kv.Value.ToString("N")[..8]}"));

            Console.Error.WriteLine(
                $"[Appearance] rebake requested -- state before:\n" +
                $"  LibreMetaverse bake slots : {string.Join("  ", report)}\n" +
                $"  worn wearables            : {wearables} ({decoded} decoded)\n" +
                $"  simulator's last relay    : {(relay.Length == 0 ? "(none seen)" : relay)}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Appearance] bake-state report failed: {ex.Message}");
        }

        // HARD STOP 2026-08-31. Every appearance send is disabled while SendAppearance is off.
        // RequestSetAppearance is NOT gated by that flag -- it bakes and sends whatever
        // MakeAppearancePacket produces -- so a wearable edit or a rebake still reached the grid
        // even with the pipeline nominally disabled, from a cold state where LibreMetaverse has no
        // decoded wearables and no bakes. That combination is what has broken this avatar
        // repeatedly. Nothing here sends until the whole path has been proven on a throwaway alt.
        // This stop covers LibreMetaverse's OWN send path only. SLNG composites, uploads and sends
        // its own appearance (SendAppearanceFromOwnBake) -- so this is no longer a refusal the user
        // needs to hear about, and reporting it as a discarded change was simply wrong.
        Console.Error.WriteLine("[Appearance] LibreMetaverse's own send stays disabled; SLNG bakes and sends its own");
        return;
    }

    /// <summary>The SSB half of <see cref="RebakeAvatar"/>. Nudges the region to re-composite the
    /// avatar by POSTing <c>{ "cof_version": N }</c> to the <c>UpdateAvatarAppearance</c> cap --
    /// exactly what <c>LLAppearanceMgr::serverAppearanceUpdateCoro</c> does (<c>llappearancemgr.cpp</c>).
    ///
    /// <para>Deliberately NOT <c>AppearanceManager.RequestSetAppearance</c>: with
    /// <c>SendAppearance</c> off, LibreMetaverse's cache is empty, so that call rebuilds the worn
    /// set from a fresh COF fetch inside itself (<c>RezMultipleAttachmentsFromInv</c>) and, on a
    /// rate-limited grid, silently drops a worn attachment link whose target doesn't resolve in the
    /// window -- live on Agni 2026-09-03, worn hair/shoes gone after a rebake (BUG-AVATAR-03). The
    /// cap POST here is a pure nudge: the sim composites from its OWN copy of the COF at
    /// <c>cof_version</c> and pushes a fresh <c>AvatarAppearance</c> back, touching nothing local.</para></summary>
    private Task RequestServerSideRebakeAsync() => SendServerAppearanceUpdateAsync();

    /// <summary>Builds the <c>UpdateAvatarAppearance</c> POST body -- pure + internal so a test can
    /// pin the shape (<c>{ "cof_version": &lt;int&gt; }</c>, mirroring the reference viewer's
    /// <c>postData["cof_version"] = cofVersion</c>).</summary>
    internal static OSDMap BuildServerAppearanceUpdate(int cofVersion) =>
        new() { ["cof_version"] = OSD.FromInteger(cofVersion) };

    /// <summary>Current Outfit Folder version from LibreMetaverse's local store -- the value the
    /// SSB cap POST is keyed on. -1 (<c>InventoryFolder.VERSION_UNKNOWN</c>) when the COF folder
    /// isn't in the store yet. AIS write-backs (CreateLink / RemoveItem) update this in place, so
    /// it tracks a wearable edit without needing a re-fetch.</summary>
    private int GetCofVersion()
    {
        var cofUuid = _client.Inventory.FindFolderForType(LibreMetaverse.FolderType.CurrentOutfit);
        if (cofUuid == LibreMetaverse.UUID.Zero) return -1;
        return (_client.Inventory.Store?.GetNodeOrDefault(cofUuid)?.Data
            as LibreMetaverse.InventoryFolder)?.Version ?? -1;
    }

    /// <summary>POSTs <c>{ cof_version }</c> to the region's <c>UpdateAvatarAppearance</c> cap and,
    /// on a version-mismatch reply (<c>{ success:false, expected:M }</c>), retries with the
    /// server's expected version (up to 3x, 500 ms apart) -- the reference viewer's
    /// <c>serverAppearanceUpdateCoro</c> retry loop, minus the UDP texture re-request.</summary>
    private async Task SendServerAppearanceUpdateAsync(CancellationToken ct = default)
    {
        var uri = _client.Network.CurrentSim?.Caps?.CapabilityURI("UpdateAvatarAppearance");
        if (uri == null)
        {
            Console.Error.WriteLine("[Appearance] no UpdateAvatarAppearance cap on this region -- cannot nudge a rebake");
            return;
        }

        int cofVersion = GetCofVersion();
        if (cofVersion < 0)
        {
            Console.Error.WriteLine("[Appearance] COF version unknown -- skipping the rebake nudge");
            return;
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var (res, data) = await _client.HttpCapsClient
                    .PostAsync(uri, OSDFormat.Xml, BuildServerAppearanceUpdate(cofVersion), ct).ConfigureAwait(false);
                int status = (int)(res?.StatusCode ?? 0);
                var reply = data is { Length: > 0 } ? OSDParser.Deserialize(data) as OSDMap : null;

                if (reply != null && reply["success"].AsBoolean())
                {
                    Console.Error.WriteLine($"[Appearance] server appearance update accepted (cof_version={cofVersion}, HTTP {status})");
                    return;
                }

                int expected = reply != null && reply.ContainsKey("expected") ? reply["expected"].AsInteger() : -1;
                if (expected > cofVersion)
                {
                    Console.Error.WriteLine($"[Appearance] server appearance update: sent cof_version={cofVersion}, server expected {expected} -- retrying");
                    cofVersion = expected;
                    await Task.Delay(500, ct).ConfigureAwait(false);
                    continue;
                }

                string err = reply != null && reply.ContainsKey("error") ? reply["error"].AsString() : $"HTTP {status}";
                Console.Error.WriteLine($"[Appearance] server appearance update rejected (cof_version={cofVersion}): {err}");
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Appearance] server appearance update failed: {ex.Message}");
                return;
            }
        }
        Console.Error.WriteLine($"[Appearance] server appearance update: gave up after 3 cof_version retries (last {cofVersion})");
    }

    /// <summary>Set once the simulator has told us our OWN baked-texture ids. Watched by
    /// <see cref="ArmSelfBakeWatchdog"/>.</summary>
    private volatile bool _selfAppearanceWithBakesSeen;

    private int _selfBakeWatchdogArmed;

    /// <summary>
    /// One-shot safety net for "the avatar logged in with no bake at all".
    ///
    /// <para>The simulator does not reliably send the local agent its own <c>AvatarAppearance</c>
    /// after login. When it doesn't, every bake channel stays <c>Guid.Empty</c>, and the result is
    /// not a subtle one: the system hair mesh renders as its full uncut helmet, the head renders
    /// blank, and worn alpha layers do not cut the system body (live, Agni 2026-09-03 --
    /// *"das backen des avatars geht nicht mehr"*). Other avatars in the same scene bake normally,
    /// because their appearance arrives with their ObjectUpdate; only our own is missing.</para>
    ///
    /// <para>The remedy is the one the reference viewer already uses on every login,
    /// <c>LLAppearanceMgr::serverAppearanceUpdateCoro</c>: POST <c>{ cof_version }</c> to the
    /// <c>UpdateAvatarAppearance</c> cap and let the sim composite from its own copy of the COF.
    /// <see cref="SendServerAppearanceUpdateAsync"/> is that POST and nothing else -- explicitly
    /// NOT <c>RequestSetAppearance</c>, which reconciles the worn set from a COF fetch inside
    /// itself and drops worn attachments on a rate-limited grid (BUG-AVATAR-03).</para>
    ///
    /// <para>Two attempts, then it stops. The first waits 25 s, long enough for the COF to reach
    /// LibreMetaverse's store -- the POST refuses to send with an unknown <c>cof_version</c>, so
    /// firing earlier would just waste the attempt. Armed once per session; a fresh login is a
    /// fresh process.</para>
    /// </summary>
    /// <summary>
    /// Reads the local agent's bake ids out of its own <c>ObjectUpdate</c> TextureEntry, for when
    /// the simulator never sent us an <c>AvatarAppearance</c>.
    ///
    /// <para>Why this is needed at all: <c>AvatarComponent.BakedTextures</c> has exactly ONE source,
    /// the <c>AvatarAppearance</c> packet (<c>WorldSimulation.ApplyAvatarAppearance</c>). Miss that
    /// packet and there is no second chance — the head renders blank and the system hair as an uncut
    /// helmet until the next login. But the bake ids are also carried in the avatar's TextureEntry,
    /// which LibreMetaverse parses out of the ordinary ObjectUpdate into
    /// <c>Avatar.Textures.FaceTextures</c>, in the same per-slot layout
    /// <see cref="OnAvatarAppearance"/> already reads. So the information is usually sitting right
    /// there, unused.</para>
    ///
    /// <para>This is the better first move than the cap nudge, and the reference viewer says why.
    /// <c>LLAppearanceMgr::serverAppearanceUpdateCoro</c> (llappearancemgr.cpp:3899-3925) refuses to
    /// send at all when <c>cofVersion &lt;= mLastUpdateReceivedCOFVersion</c>: <b>the server will not
    /// re-composite for a COF version it has already served.</b> Measured live 2026-09-04 — the
    /// watchdog POSTed twice, the region answered <c>HTTP 200</c> both times for
    /// <c>cof_version=40</c>, and no appearance ever came back. A successful POST is not a bake.
    /// </para>
    ///
    /// <para>Publishes with an EMPTY visual-param array on purpose: <c>ApplyAvatarAppearance</c>
    /// treats that as "this event carries no shape" and keeps the shape it already has, which is
    /// exactly right here — this event knows about textures and nothing else. The hover offset is
    /// read from the same cached Avatar so it is not silently reset to zero.</para>
    /// </summary>
    private bool TryPublishSelfBakesFromScene()
    {
        var sim = _client.Network.CurrentSim;
        if (sim == null) return false;

        LibreMetaverse.Avatar? me = null;
        foreach (var kv in sim.ObjectsAvatars)
        {
            if (kv.Value != null && kv.Value.ID == _client.Self.AgentID) { me = kv.Value; break; }
        }

        var faces = me?.Textures?.FaceTextures;
        if (faces == null) return false;

        var textures = new Dictionary<int, Guid>();
        for (int i = 0; i < faces.Length; i++)
        {
            var face = faces[i];
            if (face != null && face.TextureID != LibreMetaverse.UUID.Zero)
                textures[i] = face.TextureID.Guid;
        }
        if (textures.Count == 0) return false;

        _lastSelfRelayBakes = new Dictionary<int, Guid>(textures);
        _selfAppearanceWithBakesSeen = true;

        Console.Error.WriteLine(
            $"[Appearance] recovered {textures.Count} bake id(s) from our own ObjectUpdate TextureEntry " +
            "-- the sim never sent an AvatarAppearance for us");

        AvatarAppearanceReceived?.Invoke(this, new AvatarAppearanceEvent(
            sim.Handle,
            _client.Self.AgentID.Guid,
            Array.Empty<byte>(),
            textures,
            _lastSelfHoverOffsetZ));
        return true;
    }

    private void ArmSelfBakeWatchdog()
    {
        if (System.Threading.Interlocked.Exchange(ref _selfBakeWatchdogArmed, 1) != 0) return;

        _ = Task.Run(async () =>
        {
            try
            {
                foreach (int delaySeconds in new[] { 25, 30 })
                {
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds)).ConfigureAwait(false);
                    if (!_client.Network.Connected) return;
                    if (_selfAppearanceWithBakesSeen) return;

                    // Look before asking. The ids are usually already in our own ObjectUpdate, and
                    // the cap POST cannot help when the server has already served this COF version
                    // (see TryPublishSelfBakesFromScene for the viewer's own check and the live
                    // measurement of a POST that was accepted and changed nothing).
                    if (TryPublishSelfBakesFromScene()) return;

                    // OpenSim's client-side bake path is a different mechanism entirely and this
                    // cap does not exist there.
                    if (!RegionHasServerSideBaking()) return;

                    Console.Error.WriteLine(
                        "[Appearance] the sim has not sent our own bake ids -- nudging a server re-composite " +
                        "(until it arrives the system hair renders as an uncut helmet and the head blank)");
                    await SendServerAppearanceUpdateAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Appearance] self-bake watchdog failed: {ex.Message}");
            }
        });
    }

    private System.Threading.CancellationTokenSource? _wearableRebakeCts;

    /// <summary>Second Life only shows a system-wearable edit (wear / take off / swap an alpha
    /// layer) once the avatar re-composites. Fires that nudge automatically, debounced so a swap
    /// (a take-off + a wear, or several layers) coalesces into ONE POST ~1.8 s after the last edit
    /// settles -- long enough for the AIS COF write-backs to bump <c>cof_version</c> in the local
    /// store. SSB only, and the nudge is the pure cap POST (<see cref="SendServerAppearanceUpdateAsync"/>),
    /// NOT the attachment-dropping <c>RequestSetAppearance</c> path (BUG-AVATAR-03).</summary>
    private void ScheduleRebakeAfterWearableEdit()
    {
        _wearableRebakeCts?.Cancel();
        var cts = _wearableRebakeCts = new System.Threading.CancellationTokenSource();
        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1800), token).ConfigureAwait(false);
                if (token.IsCancellationRequested || !_client.Network.Connected) return;
                if (!RegionHasServerSideBaking()) return;
                Console.Error.WriteLine("[Appearance] wearable edit settled -- nudging a server re-composite");
                await SendServerAppearanceUpdateAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Console.Error.WriteLine($"[Appearance] auto rebake nudge failed: {ex.Message}"); }
        });
    }

    // Both blockers are now handled, each by a guard that refuses to send rather than guessing:
    //
    //   1. Visual-param ORDER. LibreMetaverse writes 195 of 218 params to the wrong slot;
    //      AgentAppearanceParams rebuilds them in wire order and VerifyRoundTrip re-reads the
    //      result the way the simulator will before anything goes out.
    //   2. Bake TEXTURES. LibreMetaverse only composites under SendAppearance, which is off, so its
    //      Textures[] can be all-zero -- measured 2026-08-31 as 8/9/10/11/20 = ZERO. Sending that
    //      says "I have no baked textures" and strips the avatar on the grid, which is exactly what
    //      happened. MergeBakeSlots fills empty slots from the simulator's own last relay and the
    //      send is refused outright if a hole remains.
    //
    // What this cannot do is produce a NEW bake, so a change that needs one (an alpha layer
    // altering what the system body shows) may not become visible until another viewer re-bakes.
    // It is non-destructive either way, which is the property that was missing.

    /// <summary>Collects the worn system wearables from the Current Outfit Folder as
    /// (itemId, wearableType) pairs — the payload of <c>AgentIsNowWearing</c>.
    ///
    /// <para>Read from the COF rather than <c>AppearanceManager.Wearables</c> on purpose: the
    /// legacy <c>AgentWearablesUpdate</c> that populates the latter carries only ONE wearable per
    /// type slot, so a modern multi-layer outfit (several skin/tattoo layers) is unrepresentable in
    /// it. The COF is the complete set, and OpenSim's handler does
    /// <c>Wearables[type].Add(...)</c> — an add, not an assign — so multiple layers of one type are
    /// accepted.</para></summary>
    private List<(LibreMetaverse.UUID ItemId, byte WearableType)> CollectWornWearablesFromCof(
        LibreMetaverse.UUID? excludeItem = null, (LibreMetaverse.UUID Id, byte Type)? extra = null)
    {
        var worn = new List<(LibreMetaverse.UUID, byte)>();
        var store = _client.Inventory.Store;
        var cofUuid = _client.Inventory.FindFolderForType(LibreMetaverse.FolderType.CurrentOutfit);
        var cofNode = cofUuid != LibreMetaverse.UUID.Zero ? store?.GetNodeOrDefault(cofUuid) : null;
        if (cofNode == null) return worn;

        foreach (var childNode in cofNode.Nodes.Values)
        {
            if (childNode.Data is not LibreMetaverse.InventoryItem link) continue;
            if (link.AssetType == LibreMetaverse.AssetType.LinkFolder) continue;

            var targetUuid = link.IsLink() ? link.ResolvedItemID : link.UUID;
            if (targetUuid == LibreMetaverse.UUID.Zero) continue;
            if (excludeItem.HasValue && (targetUuid == excludeItem.Value || link.UUID == excludeItem.Value)) continue;

            if (store?.GetNodeOrDefault(targetUuid)?.Data is not LibreMetaverse.InventoryWearable w) continue;
            worn.Add((targetUuid, (byte)w.WearableType));
        }

        if (extra.HasValue && !worn.Any(e => e.Item1 == extra.Value.Id))
            worn.Add((extra.Value.Id, extra.Value.Type));

        return worn;
    }

    /// <summary>Tells the simulator which system wearables are worn now.
    ///
    /// <para>This is the ONE appearance-related packet that is safe to send from SLNG today: it
    /// carries item ids and wearable-type bytes and <b>nothing else</b> — no visual parameters, no
    /// texture entry — so it cannot write a wrong shape or strip a bake, which is what every
    /// previous attempt did. OpenSim's <c>AvatarFactoryModule</c> applies it to
    /// <c>sp.Appearance.Wearables</c> and persists it (<c>QueueAppearanceSave</c>), then waits for
    /// a viewer to bake. So the change is genuinely recorded server-side; it becomes VISIBLE once
    /// something re-bakes, which SLNG cannot do yet.</para></summary>
    private void SendAgentIsNowWearing(List<(LibreMetaverse.UUID ItemId, byte WearableType)> worn)
    {
        var packet = new LibreMetaverse.Packets.AgentIsNowWearingPacket
        {
            AgentData = { AgentID = _client.Self.AgentID, SessionID = _client.Self.SessionID },
            WearableData = worn
                .Select(w => new LibreMetaverse.Packets.AgentIsNowWearingPacket.WearableDataBlock
                {
                    ItemID = w.ItemId,
                    WearableType = w.WearableType,
                })
                .ToArray(),
        };

        _client.Network.SendPacket(packet);
        Console.Error.WriteLine($"[Appearance] AgentIsNowWearing sent ({worn.Count} wearable(s))");
    }

    /// <summary>Puts a system wearable on: adds its Current-Outfit link, then tells the simulator
    /// the new worn set. Deliberately does NOT go through <c>AppearanceManager.AddToOutfit</c>,
    /// which ends in the appearance send that has corrupted this avatar three times.</summary>
    private async Task WearWearableAsync(LibreMetaverse.InventoryItem wearable, bool replace)
    {
        var cofUuid = _client.Inventory.FindFolderForType(LibreMetaverse.FolderType.CurrentOutfit);
        if (cofUuid == LibreMetaverse.UUID.Zero)
        {
            Console.Error.WriteLine($"[Appearance] wear of \"{wearable.Name}\" not sent: no Current Outfit folder");
            WearableEditUnavailable?.Invoke(this, wearable.Name);
            return;
        }

        try
        {
            byte type = wearable is LibreMetaverse.InventoryWearable iw ? (byte)iw.WearableType : (byte)0;
            var wearType = wearable is LibreMetaverse.InventoryWearable iw2
                ? iw2.WearableType : LibreMetaverse.WearableType.Invalid;

            // Body parts replace, they do not layer: an avatar has exactly one shape, skin, hair
            // and eyes. Without this a second one is simply added -- and since the layer-ordering
            // token is written below, it would even be given a position in a stack that cannot
            // exist. Take the old one's link out first, so wearing means swapping.
            if (WearableRules.ReplacesSameType(wearable.AssetType, wearType))
            {
                int replaced = await RemoveCofLinksOfWearableTypeAsync(type, keep: wearable.UUID)
                    .ConfigureAwait(false);
                if (replaced > 0)
                    Console.Error.WriteLine($"[Appearance] replacing {replaced} worn {wearType} " +
                        "-- body parts are replaced, not layered");
            }

            // The COF link's description is where Second Life keeps the layer's position in the
            // stack -- '@' + type * 100 + index, see WearableLayerOrder. Passing the item's own
            // description there, as this did, leaves every layer SLNG puts on untokened, and an
            // untokened layer sorts BELOW every tokened one. So anything worn here landed at the
            // bottom of its type's stack and disappeared under whatever was already on. A new layer
            // belongs on top, which is index = however many of that type are already worn.
            int existing = wearable is LibreMetaverse.InventoryWearable
                ? CollectWornWearablesFromCof().Count(e => e.WearableType == type)
                : 0;
            string linkDescription = wearable is LibreMetaverse.InventoryWearable
                ? WearableLayerOrder.BuildOrderString(type, existing)
                : wearable.Description;

            await _client.Inventory.CreateLinkAsync(
                cofUuid, wearable.UUID, wearable.Name, linkDescription,
                LibreMetaverse.InventoryType.Wearable, LibreMetaverse.UUID.Zero).ConfigureAwait(false);
            SendAgentIsNowWearing(CollectWornWearablesFromCof(extra: (wearable.UUID, type)));

            Console.Error.WriteLine($"[Appearance] wore \"{wearable.Name}\" ({wearable.AssetType}) " +
                "-- recorded server-side; auto re-composite scheduled");
            WornItemsChanged?.Invoke(this, EventArgs.Empty);
            ScheduleRebakeAfterWearableEdit();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Appearance] wear of \"{wearable.Name}\" failed: {ex.Message}");
            WearableEditUnavailable?.Invoke(this, wearable.Name);
        }
    }

    /// <summary>Takes a system wearable off: deletes its Current-Outfit link(s), then tells the
    /// simulator the new worn set.
    ///
    /// <para>The link is REMOVED, not trashed. BUG-NET-02 moves stale COF links to Trash because
    /// there the link is the only evidence of an ambiguous state and might be wanted back. Here the
    /// removal is what the user asked for, and a COF link is a pointer, not content — the wearable
    /// itself stays in inventory and wearing it again just makes a new link. Trashing would only
    /// pile up junk (raised live: "die Links landen dann aber nicht jedes Mal im Trash?").</para></summary>
    /// <summary>Removes the Current Outfit links for every worn wearable of one type, except
    /// <paramref name="keep"/> — the "replace" half of wearing a body part. Returns how many were
    /// taken off.</summary>
    private async Task<int> RemoveCofLinksOfWearableTypeAsync(byte wearableType, LibreMetaverse.UUID keep)
    {
        var store = _client.Inventory.Store;
        var cof = _client.Inventory.FindFolderForType(LibreMetaverse.FolderType.CurrentOutfit);
        var cofNode = cof != LibreMetaverse.UUID.Zero ? store?.GetNodeOrDefault(cof) : null;
        if (cofNode == null) return 0;

        var doomed = new List<LibreMetaverse.UUID>();
        foreach (var child in cofNode.Nodes.Values)
        {
            if (child.Data is not LibreMetaverse.InventoryItem link) continue;
            if (link.AssetType == LibreMetaverse.AssetType.LinkFolder) continue;

            var target = link.IsLink() ? link.ResolvedItemID : link.UUID;
            if (target == keep || target == LibreMetaverse.UUID.Zero) continue;
            if (store?.GetNodeOrDefault(target)?.Data is not LibreMetaverse.InventoryWearable worn) continue;
            if ((byte)worn.WearableType != wearableType) continue;

            doomed.Add(link.UUID);
        }

        foreach (var linkId in doomed)
        {
            try { await _client.Inventory.RemoveItemAsync(linkId).ConfigureAwait(false); }
            catch (Exception ex) { Console.Error.WriteLine($"[Appearance] could not remove COF link: {ex.Message}"); }
        }

        return doomed.Count;
    }

    private async Task<DetachResult> RemoveWearableAsync(LibreMetaverse.InventoryItem wearable)
    {
        // Body parts are replace-only: an avatar always has exactly one shape, skin, hair and eyes,
        // and a real viewer offers no take-off for them at all. Removing the COF link the way this
        // does for clothing would leave the avatar with no shape.
        var wearableType = wearable is LibreMetaverse.InventoryWearable w
            ? w.WearableType : LibreMetaverse.WearableType.Invalid;
        if (!WearableRules.CanTakeOff(wearable.AssetType, wearableType))
        {
            string reason = WearableRules.TakeOffRefusedReason(wearableType);
            Console.Error.WriteLine($"[Appearance] refused to take off \"{wearable.Name}\": {reason}");
            WearableEditRefused?.Invoke(this, reason);
            return new DetachResult(false, 0);
        }

        var store = _client.Inventory.Store;
        var cofUuid = _client.Inventory.FindFolderForType(LibreMetaverse.FolderType.CurrentOutfit);
        var cofNode = cofUuid != LibreMetaverse.UUID.Zero ? store?.GetNodeOrDefault(cofUuid) : null;

        if (cofNode == null)
        {
            Console.Error.WriteLine($"[Appearance] detach of \"{wearable.Name}\" not sent: no Current Outfit folder");
            WearableEditUnavailable?.Invoke(this, wearable.Name);
            return new DetachResult(false, 0);
        }

        int removed = 0;
        foreach (var childNode in cofNode.Nodes.Values.ToList())
        {
            if (childNode.Data is not LibreMetaverse.InventoryItem link) continue;
            if (link.AssetType == LibreMetaverse.AssetType.LinkFolder) continue;

            var targetUuid = link.IsLink() ? link.ResolvedItemID : link.UUID;
            if (targetUuid != wearable.UUID && link.UUID != wearable.UUID) continue;

            try
            {
                await _client.Inventory.RemoveItemAsync(link.UUID).ConfigureAwait(false);
                cofNode.Nodes.Remove(link.UUID);
                removed++;
            }
            catch (Exception ex) { Console.Error.WriteLine($"[Appearance] could not remove COF link: {ex.Message}"); }
        }

        if (removed == 0)
        {
            Console.Error.WriteLine($"[Appearance] \"{wearable.Name}\" has no Current-Outfit link -- nothing to take off");
            return new DetachResult(false, 0);
        }

        SendAgentIsNowWearing(CollectWornWearablesFromCof(excludeItem: wearable.UUID));
        Console.Error.WriteLine($"[Appearance] removed \"{wearable.Name}\" ({wearable.AssetType}) " +
            $"-- {removed} outfit link(s) removed, recorded server-side; auto re-composite scheduled");
        WornItemsChanged?.Invoke(this, EventArgs.Empty);
        ScheduleRebakeAfterWearableEdit();

        return new DetachResult(false, removed, WearableRemoved: true);
    }

    /// <summary>The simulator's own last relay of the self avatar's shape, captured in
    /// <see cref="OnAvatarAppearance"/>. In <c>VisualParams.Group0ParamIds</c> order — the wire /
    /// decoder order that <c>AvatarShapeService.ComputeEffectiveWeights</c> indexes positionally.
    /// Kept because it is the only shape array in that order: LibreMetaverse's
    /// <c>MyVisualParameters</c> is built by <c>MakeAppearancePacket</c> in a different one
    /// (see <see cref="OnAppearanceSet"/>). FEAT-AVATAR-01.</summary>
    private byte[] _lastSelfRelayVisualParams = Array.Empty<byte>();
    private float _lastSelfHoverOffsetZ;

    /// <summary>The simulator's own last view of our baked textures, keyed by AvatarTextureIndex
    /// (8 head, 9 upper, 10 lower, 11 eyes, 20 hair). These demonstrably work — another viewer
    /// composited and uploaded them. FEAT-AVATAR-01 keeps them as the reference the bake diagnostic
    /// compares LibreMetaverse's own <c>Textures[]</c> against.</summary>
    private Dictionary<int, Guid> _lastSelfRelayBakes = new();

    private void OnAppearanceSet(object? sender, AppearanceSetEventArgs e)
    {
        if (!e.Success) return;

        // FEAT-UI-16: a completed bake / outfit apply changes the worn set.
        WornItemsChanged?.Invoke(this, EventArgs.Empty);

        // Records what LibreMetaverse just put into MyVisualParameters. That array is in
        // MakeAppearancePacket's (wrong) order, so this is a diagnostic only -- never a shape.
        LogVisualParamHealth();

        // FEAT-AVATAR-01: LibreMetaverse has just baked (correctly) and sent an AgentSetAppearance
        // whose visual params are scrambled. Replace it with a correctly-ordered one while its
        // fresh bake textures are still what MakeAppearancePacket hands out.
        //
        // UNCONDITIONAL, deliberately. With SendAppearance on, LibreMetaverse bakes and sends on
        // its own -- at login (Simulator_OnCapabilitiesReceived), on a region change, and on the
        // simulator's RebakeAvatarTextures request -- not only after a wearable edit we initiated.
        // Every one of those writes the scrambled param array to the account, so every one of them
        // has to be followed by the correction. Gating this on a user action would have left the
        // login send uncorrected, which is the single most damaging one.
        SendCorrectedAppearance();

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

        // FEAT-AVATAR-01 -- the shape MUST NOT come from Appearance.MyVisualParameters here.
        //
        // Two different orderings exist and they are not interchangeable (pinned by
        // SLNG.Assets.Tests.VisualParamOrderTests):
        //   * DECODER order = VisualParams.Group0ParamIds, ascending id over the TRANSMITTED params.
        //     This is what the simulator sends, what LMV's Avatar.DecodeVisualParams assumes, and
        //     what AvatarShapeService.ComputeEffectiveWeights indexes positionally.
        //   * ENCODER order = whatever AppearanceManager.MakeAppearancePacket produces: it iterates
        //     VisualParams.Params -- ALL params, including the never-transmitted group-1/2 ones --
        //     and takes the first 218, then copies that into MyVisualParameters.
        // The first 218 of Params are provably NOT the first 218 of Group0ParamIds, so
        // MyVisualParameters assigns each byte to the WRONG parameter when read as a shape.
        //
        // This event only fires when LibreMetaverse runs its own bake, i.e. never while
        // SendAppearance is off -- which is why the bug stayed latent. With SLNG_APPEARANCE_SYNC on
        // it fires, and feeding the encoder-order array to the shape service scrambled every
        // skeletal param: live 2026-08-31 the rigged mesh head tore apart.
        //
        // The valuable part of this event is the freshly composited BAKE IDS. Take those, and pair
        // them with the simulator's own last relay of the shape, which is in decoder order.
        RaiseAvatarAppearance(new AvatarAppearanceEvent(
            _client.Network.CurrentSim?.Handle ?? 0,
            _client.Self.AgentID.Guid,
            _lastSelfRelayVisualParams,
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
            // See OnAvatarUpdate: neighbor sims (MultipleSims, BUG-NET-03) also stream avatar
            // terse updates, including our own child-agent copy -- the current sim is the sole
            // authority for avatars.
            if (e.Simulator != _client.Network.CurrentSim) return;

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
        // BUG-NET-03: neighbor sims (MultipleSims) replicate worn attachments as child-agent
        // copies with foreign LocalIds and a parent avatar we deliberately don't track (see
        // OnAvatarUpdate). Passing the local agent's copies through churned the skeleton
        // (ObjectDisposedException from AvatarRenderer.UpdateAttachment); other residents'
        // neighbor attachments are just orphans. World objects from neighbors are the whole point
        // of MultipleSims, so drop only attachment-flagged prims from a non-current sim.
        if (simulator != _client.Network.CurrentSim
            && prim.PrimData.AttachmentPoint != LibreMetaverse.AttachmentPoint.Default)
            return;

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
        // BUG-NET-03: with neighbor circuits (MultipleSims) this now also fires for a neighbor
        // the grid told us to drop (DisableSimulator) as we moved away from a border -- the
        // consumer (WorldSimulation) unloads that region's entities/terrain via World.RemoveRegion.
        Console.WriteLine($"[Neighbor] disconnected {e.Simulator.Name} ({e.Simulator.Handle})");
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

    /// <summary>The Body Parts folder — where <see cref="CreateTestSkinAsync"/> puts the skin, so the
    /// UI can refresh exactly that folder rather than making the user close and reopen the window to
    /// see an item it was just told about.</summary>
    public Guid? BodyPartsFolderId => _client.Inventory.FindFolderForType(FolderType.BodyPart).Guid;

    /// <summary>The Textures folder — the other destination <see cref="CreateTestSkinAsync"/> writes
    /// to.</summary>
    public Guid? TexturesFolderId => _client.Inventory.FindFolderForType(FolderType.Texture).Guid;

    /// <summary>Folder id of the <c>#Outfits</c> system folder (each direct subfolder is one saved
    /// outfit), or null if the grid doesn't have one / before login. FEAT-INV-04.</summary>
    public Guid? MyOutfitsFolderId
    {
        get
        {
            var id = _client.Inventory.FindFolderForType(FolderType.MyOutfits);
            return id == LibreMetaverse.UUID.Zero ? null : id.Guid;
        }
    }

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
    /// Wears an inventory item. An attachment/object is attached via <c>Attach</c>. A system
    /// wearable (Clothing/Bodypart layer) is added via <c>AppearanceManager.AddToOutfit</c> — on an
    /// SL server-side-baking region straight through, otherwise only after every currently-worn
    /// wearable is decoded so the rebake can't persist a default shape (FEAT-AVATAR-01); if that
    /// preparation fails the wear is refused with <see cref="WearableEditUnavailable"/>. Handles
    /// item IDs and links.
    /// </summary>
    public async Task AttachItemAsync(Guid itemId, byte attachPoint = 0, bool replace = false)
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
            // FEAT-AVATAR-01: a system wearable is not an attachment. AddToOutfit → RequestSetAppearance
            // triggers a rebake; MakeAppearancePacket rebuilds the 218 visual params from the DECODED
            // worn wearables, falling back to vp.DefaultValue for any it can't decode — which is how
            // the 2026-08-02 / 2026-08-29 flatten happened. PrepareWearableEditAsync makes that safe
            // (SL SSB: server composes, nothing sent; else: decode every worn wearable first).
            if (ClassifyItem(realItem is LibreMetaverse.InventoryWearable, (int)realItem.AssetType)
                == WearableKind.Wearable)
            {
                await WearWearableAsync(realItem, replace).ConfigureAwait(false);
                return;
            }

            // A #Library item can't be linked into an outfit (you don't own it). Attach the OWNED
            // COPY instead of the Library original, so the scene attachment id, the COF link and
            // any outfit link all point at the same item — otherwise the worn marker never matches
            // ("Schuhe angezogen, im Outfit stehen sie als nicht getragen"). The copy is content-
            // identical and reused across wears (CopyLibraryItemForOutfitAsync dedups by AssetUUID).
            if (IsUnderLibrary(realItem.UUID))
            {
                var owned = await CopyLibraryItemForOutfitAsync(realItem).ConfigureAwait(false);
                if (owned != null) realItem = owned;
            }

            _client.Appearance.Attach(realItem, (LibreMetaverse.AttachmentPoint)attachPoint, replace);
            // LibreMetaverse's Attach only sends RezSingleAttachmentFromInv — it never records the
            // item in the Current Outfit folder. Without a COF link the attachment is on the avatar
            // this session only: SL's server-side bake recomposites from the COF on the next relog,
            // and every outfit-save slams COF links, so a worn-but-unlinked attachment silently
            // vanishes on relog and is missing from any outfit saved while it was on.
            // WearWearableAsync already writes this link for system layers.
            await EnsureCofLinkForItemAsync(realItem, LibreMetaverse.InventoryType.Object).ConfigureAwait(false);
            WornItemsChanged?.Invoke(this, EventArgs.Empty);
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
    }

    /// <summary>Creates a Current-Outfit link for <paramref name="item"/> unless one already
    /// exists. See the call in <see cref="AttachItemAsync"/> for why an attachment needs this
    /// explicitly — no bake or appearance send is triggered, this is an inventory link only.
    ///
    /// <para>The link target is resolved to the <b>base</b> inventory item first: AIS rejects a
    /// link whose <c>linked_id</c> is itself a link (link-to-link is illegal) or points at
    /// something not in agent inventory — the <c>Create inventory in &lt;COF&gt;: Bad Request</c>
    /// pairs BUG-INV-01 kept hitting on a couple of worn attachments.</para></summary>
    private async Task EnsureCofLinkForItemAsync(LibreMetaverse.InventoryItem item, LibreMetaverse.InventoryType invType)
    {
        var cofUuid = _client.Inventory.FindFolderForType(LibreMetaverse.FolderType.CurrentOutfit);
        if (cofUuid == LibreMetaverse.UUID.Zero) return;

        var store = _client.Inventory.Store;

        // Walk to the real item: a link's linked_id must be a base item, never another link.
        var target = item;
        for (int hop = 0; hop < 4 && target.IsLink(); hop++)
        {
            var next = target.ResolvedItemID != LibreMetaverse.UUID.Zero ? target.ResolvedItemID : target.AssetUUID;
            if (next == LibreMetaverse.UUID.Zero) break;
            if (store?.GetNodeOrDefault(next)?.Data is not LibreMetaverse.InventoryItem resolved) { target = null!; break; }
            target = resolved;
        }
        if (target is null || target.UUID == LibreMetaverse.UUID.Zero || target.IsLink())
        {
            Console.Error.WriteLine(
                $"[Appearance] not COF-linking '{item.Name}' ({item.UUID}) — does not resolve to a real inventory item " +
                $"(isLink={item.IsLink()} resolvedItemId={item.ResolvedItemID} assetUuid={item.AssetUUID})");
            return;
        }

        // A #Library item (SL starter-avatar hair/clothing, freebies) is owned by the Library
        // account, not you. It wears fine, but AIS refuses to link one into your COF
        // ("Create inventory in <COF>: Bad Request") — the reference viewer copies it into your
        // inventory first and links the copy (LLAppearanceMgr::wearItemsOnAvatar). Do the same.
        if (IsUnderLibrary(target.UUID))
        {
            var owned = await CopyLibraryItemForOutfitAsync(target).ConfigureAwait(false);
            if (owned is null)
            {
                Console.Error.WriteLine(
                    $"[Appearance] '{target.Name}' is a Library item and could not be copied into your inventory — " +
                    "it will wear this session but cannot be saved to an outfit");
                return;
            }
            target = owned;
        }

        bool foreignOwner = target.OwnerID != LibreMetaverse.UUID.Zero && target.OwnerID != _client.Self.AgentID;

        var cofNode = store?.GetNodeOrDefault(cofUuid);
        if (cofNode != null)
            foreach (var child in cofNode.Nodes.Values)
            {
                if (child.Data is not LibreMetaverse.InventoryItem link || !link.IsLink()) continue;
                var t = link.ResolvedItemID != LibreMetaverse.UUID.Zero ? link.ResolvedItemID : link.AssetUUID;
                if (t == target.UUID) return; // already recorded
            }

        try
        {
            var created = await _client.Inventory.CreateLinkAsync(
                cofUuid, target.UUID, target.Name, string.Empty, invType, LibreMetaverse.UUID.Zero).ConfigureAwait(false);
            if (created != null)
            {
                Console.Error.WriteLine($"[Appearance] recorded '{target.Name}' in the Current Outfit folder");
                return;
            }

            // AIS refused it.
            if (foreignOwner)
            {
                Console.Error.WriteLine(
                    $"[Appearance] '{target.Name}' ({target.UUID}) was not added to your outfit — the grid says it is " +
                    $"owned by {target.OwnerID}, not you, so it is not in your inventory (worn from a shared/demo source?)");
                return;
            }
            string where = "?";
            for (var n = store?.GetNodeOrDefault(target.UUID); n != null; n = n.Parent)
                if (n.Data is LibreMetaverse.InventoryFolder pf)
                { where = pf.PreferredType != LibreMetaverse.FolderType.None ? pf.PreferredType.ToString() : pf.Name; break; }
            Console.Error.WriteLine(
                $"[Appearance] AIS refused COF link for '{target.Name}' ({target.UUID}): " +
                $"assetType={target.AssetType} invType={target.InventoryType} isLink={target.IsLink()} " +
                $"owner={target.OwnerID} mine={target.OwnerID == _client.Self.AgentID} " +
                $"perms={target.Permissions.OwnerMask} parentFolder={where}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Appearance] could not COF-link '{target.Name}': {ex.Message}");
        }
    }

    /// <summary>True when <paramref name="itemId"/>'s node sits anywhere under the store's
    /// <c>#Library</c> root — a Linden-owned item that this agent can wear but not link or
    /// modify.</summary>
    private bool IsUnderLibrary(LibreMetaverse.UUID itemId)
    {
        var store = _client.Inventory.Store;
        var libRoot = store?.LibraryFolder;
        if (libRoot == null) return false;
        for (var n = store!.GetNodeOrDefault(itemId); n != null; n = n.Parent)
            if (n.Data?.UUID == libRoot.UUID) return true;
        return false;
    }

    // Library-item-id -> the owned copy we made this session, so re-wearing the same starter
    // item never copies twice. Cross-session dedup is the AssetUUID scan in the method below.
    private readonly Dictionary<LibreMetaverse.UUID, LibreMetaverse.UUID> _libraryCopyCache = new();

    /// <summary>Copies a <c>#Library</c> item into the agent's own inventory so it can be linked
    /// into an outfit. Never makes a second copy of the same starter item: a session cache
    /// short-circuits a re-wear, and otherwise the whole owned inventory is scanned for an
    /// existing copy of the <b>same asset</b> (<c>AssetUUID</c>, which a copy shares with its
    /// original). Returns the owned copy, or null if the copy failed.</summary>
    private async Task<LibreMetaverse.InventoryItem?> CopyLibraryItemForOutfitAsync(LibreMetaverse.InventoryItem libItem)
    {
        var store = _client.Inventory.Store;
        if (store == null) return null;

        // 1. Already copied this session?
        if (_libraryCopyCache.TryGetValue(libItem.UUID, out var cachedId)
            && store.GetNodeOrDefault(cachedId)?.Data is LibreMetaverse.InventoryItem cached && !cached.IsLink())
            return cached;

        // 2. A copy from an earlier session? A copy shares the original's AssetUUID.
        if (libItem.AssetUUID != LibreMetaverse.UUID.Zero && store.RootFolder != null)
        {
            var stack = new Stack<LibreMetaverse.UUID>();
            stack.Push(store.RootFolder.UUID);
            while (stack.Count > 0)
            {
                var node = store.GetNodeOrDefault(stack.Pop());
                if (node == null) continue;
                foreach (var child in node.Nodes.Values)
                {
                    switch (child.Data)
                    {
                        case LibreMetaverse.InventoryFolder:
                            stack.Push(child.Data.UUID);
                            break;
                        case LibreMetaverse.InventoryItem c when !c.IsLink()
                            && c.OwnerID == _client.Self.AgentID
                            && c.AssetUUID == libItem.AssetUUID
                            && c.AssetType == libItem.AssetType:
                            Console.Error.WriteLine($"[Appearance] reusing existing copy of Library item '{libItem.Name}'");
                            _libraryCopyCache[libItem.UUID] = c.UUID;
                            return c;
                    }
                }
            }
        }

        // 3. Make the copy — into the system folder for the item's asset type.
        var destType = libItem.AssetType switch
        {
            LibreMetaverse.AssetType.Bodypart => LibreMetaverse.FolderType.BodyPart,
            LibreMetaverse.AssetType.Clothing => LibreMetaverse.FolderType.Clothing,
            _ => LibreMetaverse.FolderType.Object,
        };
        var dest = _client.Inventory.FindFolderForType(destType);
        if (dest == LibreMetaverse.UUID.Zero) dest = store.RootFolder?.UUID ?? LibreMetaverse.UUID.Zero;
        if (dest == LibreMetaverse.UUID.Zero) return null;

        try
        {
            var copied = await _client.Inventory.RequestCopyItemAsync(
                libItem.UUID, dest, libItem.Name, libItem.OwnerID, CancellationToken.None).ConfigureAwait(false);
            if (copied is LibreMetaverse.InventoryItem ci)
            {
                Console.Error.WriteLine($"[Appearance] copied Library item '{libItem.Name}' into your inventory ({ci.UUID})");
                _libraryCopyCache[libItem.UUID] = ci.UUID;
                return ci;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Appearance] copy of Library item '{libItem.Name}' failed: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// Takes an inventory item off the agent. An attachment is removed via
    /// <c>DetachAttachmentIntoInv</c> (plus stale-COF-link cleanup). A system wearable
    /// (Clothing/Bodypart layer) is removed via <c>AppearanceManager.RemoveFromOutfit</c> — on an
    /// SL server-side-baking region straight through, otherwise only after every currently-worn
    /// wearable is decoded so the rebake can't persist a default shape (FEAT-AVATAR-01); if that
    /// preparation fails the detach is refused with <see cref="WearableEditUnavailable"/>. Handles
    /// item IDs and links inside Current Outfit.
    /// </summary>
    public Task<DetachResult> DetachItemAsync(Guid itemId)
    {
        var itemUuid = new LibreMetaverse.UUID(itemId);
        var store = _client.Inventory.Store;
        var node = store?.GetNodeOrDefault(itemUuid);

        // FEAT-AVATAR-01: a system wearable is not an attachment (the sim ignores
        // DetachAttachmentIntoInv for a Clothing/Bodypart layer). RemoveFromOutfit → RequestSetAppearance
        // triggers a rebake; see WearWearableAsync's comment for why it needs preparation.
        {
            var real = node?.Data as LibreMetaverse.InventoryItem;
            if (real is { } && real.IsLink())
                real = store?.GetNodeOrDefault(real.ResolvedItemID)?.Data as LibreMetaverse.InventoryItem ?? real;
            if (real is { }
                && ClassifyItem(real is LibreMetaverse.InventoryWearable, (int)real.AssetType) == WearableKind.Wearable)
            {
                return RemoveWearableAsync(real);
            }
        }

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
                    // FEAT-INV-03: this now runs for a real attachment too. It used to be gated on
                    // !wasAttached, trusting the sim to drop the link as part of the detach -- but
                    // OpenSim does not do that reliably, so the link survived and the item was
                    // re-worn on the next login.
                    //
                    // DELETE, not move-to-Trash. MoveInventoryItem on a Current-Outfit link gets
                    // HTTP 400 from AIS on SL ("Move item … to <Trash>: Bad Request") -- the COF
                    // handler rejects the move -- so the link never left and the item stayed worn
                    // across logins ("Ablegen geht nicht persistent", live 2026-09-03). staleKeys
                    // are the links for the one item the user explicitly chose to take off, so a
                    // durable delete is well-targeted; the linked inventory item is untouched.
                    var toDelete = staleKeys.Where(k => k != LibreMetaverse.UUID.Zero).ToList();
                    if (toDelete.Count > 0)
                    {
                        try
                        {
                            _ = _client.Inventory.RemoveItemsAsync(toDelete, System.Threading.CancellationToken.None);
                            staleLinksRemoved = toDelete.Count;
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"[Detach] RemoveItemsAsync threw: {ex.Message}");
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
        var itemIds = new List<Guid>();
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

            // FEAT-INV-03: the inventory item id travels in the AttachItemID name-value -- the
            // same field the sim matches DetachAttachmentIntoInv on. Needed to trash the COF link
            // so "Detach All" survives a relog.
            var attId = ExtractAttachItemId(p);
            if (attId != Guid.Empty) itemIds.Add(attId);
        }

        // Named, not just counted: "0 detached" and "3 detached but one is still on screen" are
        // the two outcomes this escape hatch has to be able to tell apart afterwards.
        foreach (var (localId, point, name) in report)
            Console.Error.WriteLine($"[Detach] localId={localId} point={point} \"{name}\"");

        if (ids.Count > 0) _client.Objects.DetachObjects(sim, ids);

        int linksRemoved = RemoveOutfitLinksForItems(itemIds);
        if (linksRemoved > 0)
            Console.Error.WriteLine($"[Detach] moved {linksRemoved} Current-Outfit link(s) to Trash");

        return ids.Count;
    }

    /// <summary>Pulls the <c>AttachItemID</c> name-value (the inventory item id) off an attachment
    /// prim, or <see cref="Guid.Empty"/> if it isn't present / parseable. FEAT-INV-03.</summary>
    private static Guid ExtractAttachItemId(Primitive p)
    {
        try
        {
            if (p.NameValues == null) return Guid.Empty;
            foreach (var nv in p.NameValues)
            {
                if (nv.Name != "AttachItemID") continue;
                if (LibreMetaverse.UUID.TryParse(nv.Value?.ToString() ?? string.Empty, out var u))
                    return u.Guid;
            }
        }
        catch { }
        return Guid.Empty;
    }

    /// <summary>The avatar's attachments as they exist in the SCENE right now — prims parented to
    /// the local agent, keyed by inventory item id (<c>AttachItemID</c>), valued by attachment
    /// point. This is the authoritative "worn right now" for attachments;
    /// <c>AppearanceManager.GetAttachmentsByItemId()</c> is a cache that keeps listing an item for
    /// a while after it's detached (see the project memory). FEAT-INV-03 / FEAT-INV-04.</summary>
    private Dictionary<Guid, LibreMetaverse.AttachmentPoint> GetSceneWornAttachments()
    {
        var map = new Dictionary<Guid, LibreMetaverse.AttachmentPoint>();
        try
        {
            var sim = _client.Network.CurrentSim;
            if (sim == null) return map;
            foreach (var p in sim.ObjectsPrimitives.Values)
            {
                if (p == null || p.ParentID != _client.Self.LocalID) continue;
                var pt = p.PrimData.AttachmentPoint;
                if (pt == LibreMetaverse.AttachmentPoint.Default) continue;
                var aid = ExtractAttachItemId(p);
                if (aid != Guid.Empty) map[aid] = pt;
            }
        }
        catch { }
        return map;
    }

    /// <summary>Deletes every Current-Outfit link that points at one of <paramref name="itemIds"/>
    /// (or whose own id is in the set) and drops it from the local store. Returns how many were
    /// removed. Edits the outfit only -- the linked items stay in inventory. FEAT-INV-03.
    ///
    /// DELETE, not move-to-Trash: <c>MoveInventoryItem</c> on a Current-Outfit link gets HTTP 400
    /// from AIS on SL (the COF handler rejects the move), so the link never actually left and the
    /// item came back worn on the next login. The caller passes explicit ids of attachments it
    /// just detached, so a durable delete is well-targeted here (unlike the heuristic scan in
    /// <see cref="CleanUpCurrentOutfit"/>, which is load-gated for that reason).</summary>
    private int RemoveOutfitLinksForItems(ICollection<Guid> itemIds)
    {
        if (itemIds.Count == 0 || TrashFolderId is null) return 0;

        var store = _client.Inventory.Store;
        var cofUuid = _client.Inventory.FindFolderForType(LibreMetaverse.FolderType.CurrentOutfit);
        var cofNode = cofUuid != LibreMetaverse.UUID.Zero ? store?.GetNodeOrDefault(cofUuid) : null;
        if (cofNode == null) return 0;

        var want = new HashSet<Guid>(itemIds);
        var linkKeys = new List<LibreMetaverse.UUID>();
        foreach (var childNode in cofNode.Nodes.Values)
        {
            if (childNode.Data is not LibreMetaverse.InventoryItem link) continue;
            var target = link.IsLink()
                ? (link.ResolvedItemID != LibreMetaverse.UUID.Zero ? link.ResolvedItemID : link.AssetUUID)
                : link.UUID;
            if (link.UUID != LibreMetaverse.UUID.Zero
                && (want.Contains(link.UUID.Guid) || want.Contains(target.Guid)))
                linkKeys.Add(link.UUID);
        }

        foreach (var k in linkKeys) cofNode.Nodes.Remove(k);
        if (linkKeys.Count > 0)
        {
            try { _ = _client.Inventory.RemoveItemsAsync(linkKeys, System.Threading.CancellationToken.None); }
            catch (Exception ex) { Console.Error.WriteLine($"[Detach] RemoveItemsAsync threw: {ex.Message}"); }
        }
        return linkKeys.Count;
    }

    /// <summary>Walks up from <paramref name="node"/> to see if any ancestor folder is the Trash
    /// folder. Bounded so a cyclic store can't hang it. FEAT-INV-03.</summary>
    private bool IsUnderTrash(LibreMetaverse.InventoryNode node)
    {
        if (TrashFolderId is not { } trashId) return false;
        var trashUuid = new LibreMetaverse.UUID(trashId);
        var cur = node;
        for (int guard = 0; guard < 32 && cur != null; guard++)
        {
            if (cur.Data != null && cur.Data.UUID == trashUuid) return true;
            cur = cur.Parent;
        }
        return false;
    }

    /// <summary>Tidies the Current Outfit Folder: deletes (a) links that resolve to nothing,
    /// (b) links whose target item is already in Trash, (c) duplicate links to the same target,
    /// and (d) links to <c>AssetType.Object</c> items that are not currently attached. Never
    /// touches a Clothing/Bodypart link (removing one needs a rebake -- FEAT-AVATAR-01) or a
    /// currently-worn item. A COF link has no asset, so a delete only drops the outfit entry.
    ///
    /// Refuses to do anything while the inventory store or the scene is still loading (see the
    /// safety gate): a COF-link delete is a durable AIS delete on SL and any COF change forces a
    /// server re-composite, so acting on a half-loaded folder -- where an unresolved target reads
    /// as "dead" and an attachment not yet in the scene reads as "unworn" -- once deleted two real
    /// links and left the avatar with no bake (live regression 2026-09-03). FEAT-INV-03.</summary>
    /// <param name="targetsResolved">Set by <see cref="CleanUpCurrentOutfitAsync"/> after it has
    /// explicitly asked the server for every COF link target missing from the store. Without it the
    /// store-ready half of the gate below is <b>unsatisfiable by waiting</b>: LibreMetaverse's store
    /// only ever holds folders somebody fetched, so a link pointing into a folder the user never
    /// opened never resolves, no matter how long you wait. Live, Agni 2026-09-03:
    /// <c>[OutfitCleanup] deferred — still loading (links=28 unresolved=10 …)</c> on a fully-loaded
    /// session — "Outfit aufräumen" had become a permanent no-op ("bereinigen hilft auch nicht").
    /// Any link still unresolved after that fetch is skipped individually further down
    /// (<c>uncachedSkipped</c>), never deleted, so relaxing the gate cannot delete a link we failed
    /// to understand. The half that actually caused the v0.20.36 regression -- <c>sceneReady</c>,
    /// which stops an attachment that has not rezzed yet from reading as "not worn" -- is
    /// untouched, and that is the path the incident's own log line
    /// (<c>unworn-attachment=2</c>) came from.</param>
    /// <summary>Resolves every Current-Outfit link target that is missing from LibreMetaverse's
    /// inventory store, then runs <see cref="CleanUpCurrentOutfit"/>.
    ///
    /// <para>The fetch is the point. A COF link carries the target item's NAME, so the Worn tab can
    /// list an item perfectly well without its target ever being in the store -- which is why two
    /// attachments could show as worn-but-inactive while the cleanup that should have removed them
    /// skipped them as "uncached" and, worse, refused to run at all because its store-ready gate
    /// counted them as "still loading". Asking the server for them turns both into a decision that
    /// can actually be made.</para>
    ///
    /// <para>Anything the server does not return stays unresolved and is skipped by the cleanup
    /// loop, exactly as before -- this only removes the case where SLNG had simply never asked.</para>
    /// </summary>
    public async Task<OutfitCleanupResult> CleanUpCurrentOutfitAsync(CancellationToken ct = default)
    {
        HashSet<Guid>? confirmedMissing = null;
        try { confirmedMissing = await ResolveCofLinkTargetsAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) { Console.Error.WriteLine($"[OutfitCleanup] link-target fetch failed: {ex.Message}"); }
        return CleanUpCurrentOutfit(targetsResolved: true, confirmedMissing);
    }

    /// <returns>Targets we explicitly asked the server for that did NOT come back, i.e. items the
    /// server does not have. <c>null</c> when the fetch itself failed or was cut short -- then we
    /// know nothing, and the caller must not treat any link as dead.</returns>
    private async Task<HashSet<Guid>?> ResolveCofLinkTargetsAsync(CancellationToken ct)
    {
        var store = _client.Inventory.Store;
        var cofUuid = _client.Inventory.FindFolderForType(LibreMetaverse.FolderType.CurrentOutfit);
        var cofNode = cofUuid != LibreMetaverse.UUID.Zero ? store?.GetNodeOrDefault(cofUuid) : null;
        if (cofNode == null || store == null) return null;

        int linkCount = 0;
        var missing = new Dictionary<LibreMetaverse.UUID, LibreMetaverse.UUID>();
        foreach (var n in cofNode.Nodes.Values)
        {
            if (n.Data is not LibreMetaverse.InventoryItem li || !li.IsLink()) continue;
            linkCount++;
            var t = li.ResolvedItemID != LibreMetaverse.UUID.Zero ? li.ResolvedItemID : li.AssetUUID;
            if (t == LibreMetaverse.UUID.Zero) continue;
            if (store.GetNodeOrDefault(t)?.Data is LibreMetaverse.InventoryItem) continue;
            missing[t] = _client.Self.AgentID;
        }
        if (missing.Count == 0) return new HashSet<Guid>();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        bool fetchCompleted = true;
        try
        {
            await _client.Inventory.RequestFetchInventoryAsync(missing, timeout.Token, items =>
            {
                if (items == null) return;
                foreach (var item in items)
                {
                    if (item == null) continue;
                    // UpdateNodeFor is LibreMetaverse's own way of putting a fetched item into the
                    // store -- its fetch reply handler uses it too.
                    try { store.UpdateNodeFor(item); } catch { }
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { fetchCompleted = false; }
        catch (Exception ex)
        {
            fetchCompleted = false;
            Console.Error.WriteLine($"[OutfitCleanup] COF link-target fetch threw: {ex.Message}");
        }

        // Then WAIT FOR THE STORE, rather than trusting the call above to have finished the job.
        // Whether that method returns once the reply is in, or merely once the request is sent, is
        // an implementation detail of the pinned LibreMetaverse build -- and the callback is not
        // the only writer either, since LMV's own reply handler also fills the store. Polling what
        // the cleanup actually reads makes the outcome independent of both. Short poll, hard cap:
        // a target the server will not return must not hold the button hostage, and one that stays
        // missing is skipped by the cleanup loop anyway.
        int resolved = 0;
        for (int i = 0; i < 25; i++)
        {
            resolved = missing.Keys.Count(t => store.GetNodeOrDefault(t)?.Data is LibreMetaverse.InventoryItem);
            if (resolved == missing.Count) break;
            try { await Task.Delay(200, timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { fetchCompleted = false; break; }
        }

        var stillMissing = missing.Keys
            .Where(t => store.GetNodeOrDefault(t)?.Data is not LibreMetaverse.InventoryItem)
            .Select(t => t.Guid)
            .ToHashSet();

        // Link count AND distinct-target count, because they diverge in a way that matters: a live
        // session showed uncached=10 links against exactly ONE unresolved target -- ten Current
        // Outfit links all pointing at the same vanished item. The per-link number on its own reads
        // like ten separate problems.
        Console.Error.WriteLine(
            $"[OutfitCleanup] link targets: {linkCount} link(s), {missing.Count} distinct uncached target(s), " +
            $"resolved {resolved}, still missing {stillMissing.Count}, fetchCompleted={fetchCompleted}");

        // Only a CLEAN fetch licenses "the server does not have this". A cancelled or throwing one
        // tells us nothing, and the caller must not delete anything on the strength of it.
        return fetchCompleted ? stillMissing : null;
    }

    /// <param name="confirmedMissingTargets">Link targets the server was explicitly asked for and
    /// did not return -- so the item is gone and its Current-Outfit links are dead weight that the
    /// <c>uncached</c> skip below would otherwise preserve forever. <c>null</c> means "we did not
    /// ask, or the asking failed", and then nothing here is treated as missing.</param>
    public OutfitCleanupResult CleanUpCurrentOutfit(bool targetsResolved = false,
        HashSet<Guid>? confirmedMissingTargets = null)
    {
        // Proxy for "inventory skeleton is loaded" -- if the Trash folder isn't known yet, the
        // Current Outfit folder almost certainly isn't either.
        if (TrashFolderId is null) return new OutfitCleanupResult(0, 0, 0, Deferred: true);

        var store = _client.Inventory.Store;
        var cofUuid = _client.Inventory.FindFolderForType(LibreMetaverse.FolderType.CurrentOutfit);
        var cofNode = cofUuid != LibreMetaverse.UUID.Zero ? store?.GetNodeOrDefault(cofUuid) : null;
        if (cofNode == null)
        {
            Console.Error.WriteLine("[OutfitCleanup] no Current Outfit folder in the store — nothing to do");
            return new OutfitCleanupResult(0, 0, 0, Deferred: true);
        }

        // "Worn right now" from the SCENE, not LibreMetaverse's GetAttachmentsByItemId() cache --
        // that cache lags a detach, which is exactly the "I took it all off but the outfit still
        // lists it" case.
        var wornAttachItemIds = new HashSet<Guid>(GetSceneWornAttachments().Keys);

        HashSet<Guid> cacheAttachIds;
        try { cacheAttachIds = _client.Appearance.GetAttachmentsByItemId().Keys.Select(k => k.Guid).ToHashSet(); }
        catch { cacheAttachIds = new HashSet<Guid>(); }

        // SAFETY GATE — live regression 2026-09-03 (v0.20.34: "Outfit aufräumen" deleted two links
        // and the avatar came back grey on the next login). Since v0.20.33 a removal here is a
        // durable AIS delete on SL, and *any* COF change makes the server re-composite the avatar.
        // While the store is still streaming, a link whose target node hasn't arrived is
        // indistinguishable from a genuinely dead one; while the region prims are still arriving,
        // an attachment reads as "unworn". Only touch the COF once BOTH are demonstrably in.
        int linkTotal = 0, linkUnresolved = 0;
        foreach (var n in cofNode.Nodes.Values)
        {
            if (n.Data is not LibreMetaverse.InventoryItem li || !li.IsLink()) continue;
            linkTotal++;
            var t = li.ResolvedItemID != LibreMetaverse.UUID.Zero ? li.ResolvedItemID : li.AssetUUID;
            // t == Zero is a genuinely targetless ("dead") link regardless of load state; only a
            // link WITH a target whose node is missing from the store means "still loading".
            if (t != LibreMetaverse.UUID.Zero &&
                store?.GetNodeOrDefault(t)?.Data is not LibreMetaverse.InventoryItem)
                linkUnresolved++;
        }

        // You are always wearing at least a body — zero scene attachments means the region prims
        // haven't arrived, so "not in the scene" can't yet be read as "not worn".
        bool sceneReady = wornAttachItemIds.Count > 0;
        bool storeReady = linkTotal > 0 && (linkUnresolved == 0 || targetsResolved);
        if (!storeReady || !sceneReady)
        {
            Console.Error.WriteLine(
                $"[OutfitCleanup] deferred — still loading (links={linkTotal} unresolved={linkUnresolved} " +
                $"scene-worn-attachments={wornAttachItemIds.Count} targetsResolved={targetsResolved}); " +
                "nothing removed, retry in a moment");
            return new OutfitCleanupResult(0, 0, 0, Deferred: true);
        }

        int dead = 0, trashedTarget = 0, unworn = 0, duplicate = 0, missingTarget = 0;

        // A Current Outfit folder holds ONE link per worn item. More than one is corruption, and it
        // is not cosmetic: a wearable linked twice is worn twice, drawn twice, and the copy without
        // an ordering token sorts below everything that has one -- so a second copy of an opaque
        // skin quietly reappears underneath the whole stack. Measured live 2026-09-01: 20 links for
        // 10 wearables. The link carrying a valid ordering token is the one to keep, since that is
        // what the layer order is built from.
        var seenTargets = new Dictionary<LibreMetaverse.UUID, LibreMetaverse.UUID>();
        int links = 0, wearableSkipped = 0, wornSkipped = 0, uncachedSkipped = 0;
        var toRemove = new List<LibreMetaverse.UUID>();

        // DELETE the COF link, don't move it to Trash. A COF link has no asset -- deleting one
        // only drops the outfit entry, the linked item is untouched -- and MoveInventoryItem on a
        // Current-Outfit link does NOT stick on SL/OpenSim: the link reappears on the next COF
        // refetch (user-reported: "beim aufräumen verschwinden die kurz, tauchen aber wieder auf").
        // RemoveItemsAsync is what the reference viewer uses for COF link removal
        // (llappearancemgr.cpp removeCOFItemLinks -> remove_inventory_item); LibreMetaverse routes
        // it through the AIS capability on SL (durable) and a RemoveInventoryObjects packet on
        // OpenSim, either a real delete rather than a move the COF handler reverts.
        bool Trash(LibreMetaverse.UUID linkKey)
        {
            if (linkKey == LibreMetaverse.UUID.Zero) return false;
            toRemove.Add(linkKey);
            cofNode!.Nodes.Remove(linkKey);
            return true;
        }

        foreach (var childNode in cofNode.Nodes.Values.ToList())
        {
            if (childNode.Data is not LibreMetaverse.InventoryItem link || !link.IsLink()) continue;
            links++;

            var targetUuid = link.ResolvedItemID != LibreMetaverse.UUID.Zero ? link.ResolvedItemID : link.AssetUUID;

            if (targetUuid == LibreMetaverse.UUID.Zero)
            {
                if (Trash(link.UUID)) dead++;
                continue;
            }

            var targetNode = store?.GetNodeOrDefault(targetUuid);
            if (targetNode != null && IsUnderTrash(targetNode))
            {
                if (Trash(link.UUID)) trashedTarget++;
                continue;
            }

            var target = targetNode?.Data as LibreMetaverse.InventoryItem;
            if (target == null)
            {
                // "Not in the store" on its own is evidence of nothing -- LibreMetaverse's store
                // only holds folders somebody fetched, so this is the normal state for an item in a
                // folder the user never opened, and skipping is right. Once the server has been
                // asked for this exact id and did not return it, though, the item is GONE and the
                // link is dead weight the skip would preserve forever. Measured live: 10 such links
                // in one COF, all pointing at a single vanished item, every one of them skipped --
                // which is a large part of why "Outfit aufraeumen" looked like it did nothing.
                if (confirmedMissingTargets != null && confirmedMissingTargets.Contains(targetUuid.Guid))
                {
                    if (Trash(link.UUID)) missingTarget++;
                    continue;
                }
                uncachedSkipped++;
                continue;
            }

            if (seenTargets.TryGetValue(targetUuid, out var keptLink))
            {
                // Keep whichever of the two carries a usable ordering token.
                var wearType = target is LibreMetaverse.InventoryWearable dupWearable
                    ? (int)dupWearable.WearableType : -1;
                bool thisTokened = wearType >= 0 && WearableLayerOrder.IsValidOrderString(link.Description, wearType);
                bool keptTokened = wearType >= 0
                    && store?.GetNodeOrDefault(keptLink)?.Data is LibreMetaverse.InventoryItem k
                    && WearableLayerOrder.IsValidOrderString(k.Description, wearType);

                var drop = thisTokened && !keptTokened ? keptLink : link.UUID;
                if (drop == keptLink) seenTargets[targetUuid] = link.UUID;
                if (Trash(drop)) duplicate++;
                continue;
            }
            seenTargets[targetUuid] = link.UUID;
            if (target.AssetType != LibreMetaverse.AssetType.Object) { wearableSkipped++; continue; }
            if (wornAttachItemIds.Contains(targetUuid.Guid)) { wornSkipped++; continue; }

            if (Trash(link.UUID)) unworn++;
        }

        if (toRemove.Count > 0)
        {
            try { _ = _client.Inventory.RemoveItemsAsync(toRemove, System.Threading.CancellationToken.None); }
            catch (Exception ex) { Console.Error.WriteLine($"[OutfitCleanup] RemoveItemsAsync threw: {ex.Message}"); }
        }

        Console.Error.WriteLine(
            $"[OutfitCleanup] links={links} scene-worn={wornAttachItemIds.Count} cache-worn={cacheAttachIds.Count} " +
            $"| deleted dead={dead} target-in-trash={trashedTarget} unworn-attachment={unworn} duplicate={duplicate} " +
            $"missing-target={missingTarget} " +
            $"(via RemoveItems, AIS={_client.AisClient?.IsAvailable}) " +
            $"| kept worn={wornSkipped} clothing/bodypart={wearableSkipped} uncached={uncachedSkipped}");

        return new OutfitCleanupResult(dead + missingTarget, trashedTarget, unworn);
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

        // Scene-derived, not GetAttachmentsByItemId() — that cache lags a detach (project memory).
        foreach (var kvp in GetSceneWornAttachments())
            result[kvp.Key] = FormatAttachmentPoint(kvp.Value);

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

    /// <summary>Which <see cref="WornCategory"/> a system wearable of the given SL <c>AssetType</c>
    /// wire value falls under. FEAT-UI-16.</summary>
    internal static WornCategory CategorizeWearable(int assetType)
        => assetType == (int)LibreMetaverse.AssetType.Bodypart
            ? WornCategory.BodyPart
            : WornCategory.Clothing;

    /// <summary>Whether a raw SL attachment-point value is a HUD point (31..38 =
    /// <c>HUDCenter2</c>..<c>HUDBottomRight</c>) or a body point. Mirrors the HUD test in
    /// <see cref="DetachAllAttachments"/>. FEAT-UI-16.</summary>
    internal static WornCategory CategorizeAttachment(int attachPointRaw)
        => attachPointRaw >= 31 && attachPointRaw <= 38
            ? WornCategory.Hud
            : WornCategory.Attachment;

    /// <summary>Item ids we've already asked the grid to fetch details for (FEAT-UI-16 name
    /// resolution). Prevents <see cref="GetWornItems"/> re-requesting the same ids on every poll.</summary>
    private readonly HashSet<Guid> _wornDetailFetchRequested = new();

    /// <summary>Every item the local avatar is wearing right now — live attachments and the live
    /// wearables set, plus anything referenced only by a Current-Outfit link (reported with
    /// <c>Live == false</c>: a stale link, or a wear that has not taken effect). Backs the
    /// inventory "Worn" tab (FEAT-UI-16); <see cref="GetWornItemsMap"/> still backs the
    /// "(getragen)" labels in the folder tree.</summary>
    public IReadOnlyList<WornItem> GetWornItems()
    {
        var byId = new Dictionary<Guid, WornItem>();
        var store = _client.Inventory.Store;

        string StoreName(LibreMetaverse.UUID id) =>
            (store?.GetNodeOrDefault(id)?.Data as LibreMetaverse.InventoryItem)?.Name ?? string.Empty;

        // Worn attachments come from the SCENE, not AppearanceManager.GetAttachmentsByItemId() --
        // that cache keeps listing an item after it's detached, which showed detached attachments
        // as still-worn in the tab (project memory). A prim parented to us also carries a usable
        // name in Properties.Name when the inventory item isn't in the lazily-loaded store yet.
        var scenePrimNames = new Dictionary<Guid, string>();
        try
        {
            var sim = _client.Network.CurrentSim;
            if (sim != null)
                foreach (var p in sim.ObjectsPrimitives.Values)
                {
                    if (p == null || p.ParentID != _client.Self.LocalID) continue;
                    var aid = ExtractAttachItemId(p);
                    var nm = p.Properties?.Name;
                    if (aid != Guid.Empty && !string.IsNullOrEmpty(nm)) scenePrimNames[aid] = nm!;
                }
        }
        catch { }

        var unresolved = new List<LibreMetaverse.UUID>();

        foreach (var kvp in GetSceneWornAttachments())
        {
            var id = kvp.Key;
            if (id == Guid.Empty) continue;
            var uuid = new LibreMetaverse.UUID(id);
            var name = StoreName(uuid);
            if (name.Length == 0 && scenePrimNames.TryGetValue(id, out var sn)) name = sn;
            if (name.Length == 0) unresolved.Add(uuid);
            byId[id] = new WornItem(id, name,
                CategorizeAttachment((int)kvp.Value), FormatAttachmentPoint(kvp.Value),
                (int)LibreMetaverse.AssetType.Object, Live: true);
        }

        try
        {
            foreach (var w in _client.Appearance.GetWearables())
            {
                var id = w.ItemID.Guid;
                if (id == Guid.Empty || byId.ContainsKey(id)) continue;
                var name = StoreName(w.ItemID);
                if (name.Length == 0) unresolved.Add(w.ItemID);
                byId[id] = new WornItem(id, name,
                    CategorizeWearable((int)w.AssetType), null, (int)w.AssetType, Live: true);
            }
        }
        catch { }

        try
        {
            var cofUuid = _client.Inventory.FindFolderForType(LibreMetaverse.FolderType.CurrentOutfit);
            var cofNode = cofUuid != LibreMetaverse.UUID.Zero ? store?.GetNodeOrDefault(cofUuid) : null;
            if (cofNode != null)
            {
                foreach (var childNode in cofNode.Nodes.Values)
                {
                    if (childNode.Data is not LibreMetaverse.InventoryItem link) continue;

                    // Skip the outfit FOLDER link. The Current Outfit Folder carries one link to
                    // the outfit folder itself so a viewer can name the worn outfit -- Firestorm
                    // shows it as "Aktuelles Outfit: <name>". It is not a worn item, and listing it
                    // put the outfit's own name in the attachments group as a permanently
                    // "(nicht aktiv)" row (reported live: "Standard Enzo").
                    if (link.AssetType == LibreMetaverse.AssetType.LinkFolder) continue;

                    var targetUuid = link.IsLink() ? link.ResolvedItemID : link.UUID;
                    var id = targetUuid.Guid;
                    if (id == Guid.Empty || byId.ContainsKey(id)) continue;

                    var target = store?.GetNodeOrDefault(targetUuid)?.Data as LibreMetaverse.InventoryItem;
                    int assetType = (int)(target?.AssetType ?? link.AssetType);
                    var cat = assetType == (int)LibreMetaverse.AssetType.Bodypart ? WornCategory.BodyPart
                        : assetType == (int)LibreMetaverse.AssetType.Clothing ? WornCategory.Clothing
                        : WornCategory.Attachment;
                    var name = target?.Name ?? link.Name ?? string.Empty;
                    if (name.Length == 0 && scenePrimNames.TryGetValue(id, out var sn)) name = sn;
                    if (name.Length == 0 && targetUuid != LibreMetaverse.UUID.Zero) unresolved.Add(targetUuid);

                    // A Current-Outfit link IS the worn state for a WEARABLE. There is nothing else
                    // to check it against: unlike an attachment, a Clothing/Bodypart layer has no
                    // in-scene object, so BUG-NET-02's "stale link with no live attachment" test
                    // simply does not apply to it. Marking these not-live was wrong and showed most
                    // of the outfit as "(nicht aktiv)" while Firestorm -- which reads the COF --
                    // listed the same items as worn.
                    //
                    // The legacy AgentWearablesUpdate cannot stand in for this: it carries ONE
                    // wearable per type slot, so a modern multi-layer outfit (several skin/tattoo
                    // layers) is unrepresentable in it and the extra layers never appear in
                    // Appearance.GetWearables() at all. The COF is the only complete source.
                    bool live = cat is WornCategory.BodyPart or WornCategory.Clothing;
                    byId[id] = new WornItem(id, name, cat, null, assetType, Live: live);
                }
            }
        }
        catch { }

        // Pull missing item details into the store so the next poll has real names. Requested
        // once per id per session -- the tab's 2.5 s refresh (FEAT-UI-16) surfaces the result.
        var toFetch = new Dictionary<LibreMetaverse.UUID, LibreMetaverse.UUID>();
        foreach (var u in unresolved)
            if (_wornDetailFetchRequested.Add(u.Guid)) toFetch[u] = _client.Self.AgentID;
        if (toFetch.Count > 0)
        {
            try { _client.Inventory.RequestFetchInventory(toFetch); } catch { }
        }

        return byId.Values.ToList();
    }

    /// <summary>The saved outfits — every direct subfolder of the <c>#Outfits</c> system folder,
    /// with the one the avatar is currently wearing flagged. Empty if the grid has no
    /// <c>#Outfits</c> folder or we're not connected. FEAT-INV-04.</summary>
    public async Task<IReadOnlyList<OutfitEntry>> GetSavedOutfitsAsync(CancellationToken ct = default)
    {
        if (MyOutfitsFolderId is not { } outfitsId) return Array.Empty<OutfitEntry>();

        var children = await FetchInventoryChildrenAsync(outfitsId, ct).ConfigureAwait(false);
        var folders = children.Where(e => e.IsFolder).ToList();

        // (1) The Current Outfit Folder carries a folder-link to the outfit it's "based on"
        // (SL / Firestorm mechanism). Cheap — one fetch.
        var activeOutfit = Guid.Empty;
        try
        {
            var cofUuid = _client.Inventory.FindFolderForType(LibreMetaverse.FolderType.CurrentOutfit);
            if (cofUuid != LibreMetaverse.UUID.Zero)
            {
                await FetchInventoryChildrenAsync(cofUuid.Guid, ct).ConfigureAwait(false);
                var cofNode = _client.Inventory.Store?.GetNodeOrDefault(cofUuid);
                if (cofNode != null)
                    foreach (var n in cofNode.Nodes.Values)
                        if (n.Data is LibreMetaverse.InventoryItem it
                            && it.AssetType == LibreMetaverse.AssetType.LinkFolder
                            && it.AssetUUID != LibreMetaverse.UUID.Zero)
                        { activeOutfit = it.AssetUUID.Guid; break; }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        // (2) Fallback (common on OpenSim, which doesn't write that folder-link): the current
        // outfit is the fully-worn one with the most items — i.e. every item it links is worn now.
        if (activeOutfit == Guid.Empty && folders.Count is > 0 and <= 60)
        {
            var wornIds = new HashSet<Guid>(GetWornItems().Where(w => w.Live).Select(w => w.ItemId));
            if (wornIds.Count > 0)
            {
                int bestCount = 0;
                var contents = await Task.WhenAll(folders.Select(f =>
                    FetchInventoryChildrenAsync(f.Id, ct))).ConfigureAwait(false);
                for (int i = 0; i < folders.Count; i++)
                {
                    var targets = contents[i]
                        .Where(e => !e.IsFolder)
                        .Select(e => e.IsLink && e.LinkTargetId != Guid.Empty ? e.LinkTargetId : e.Id)
                        .Where(g => g != Guid.Empty)
                        .ToHashSet();
                    if (targets.Count > bestCount && targets.IsSubsetOf(wornIds))
                    {
                        bestCount = targets.Count;
                        activeOutfit = folders[i].Id;
                    }
                }
            }
        }

        Console.Error.WriteLine($"[SavedOutfits] {folders.Count} outfits, active={activeOutfit}");

        var result = folders
            .Select(e => new OutfitEntry(e.Id, e.Name, e.Id == activeOutfit && activeOutfit != Guid.Empty))
            .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return result;
    }

    /// <summary>Current worn set, re-read once after a short wait if any name is still unresolved
    /// (wearables have no in-scene prim to fall back on), so links get real names not "Link".</summary>
    private async Task<IReadOnlyList<WornItem>> GetWornItemsWithNamesAsync(CancellationToken ct)
    {
        var worn = GetWornItems();
        if (worn.Any(w => w.Live && string.IsNullOrEmpty(w.Name)))
        {
            try { await Task.Delay(700, ct).ConfigureAwait(false); } catch (OperationCanceledException) { throw; }
            worn = GetWornItems();
        }
        return worn;
    }

    /// <summary>The set of inventory-item ids an outfit folder already links to.</summary>
    private async Task<HashSet<Guid>> GetOutfitTargetIdsAsync(Guid outfitFolderId, CancellationToken ct)
    {
        var set = new HashSet<Guid>();
        try
        {
            foreach (var e in await FetchInventoryChildrenAsync(outfitFolderId, ct).ConfigureAwait(false))
            {
                if (e.IsFolder) continue;
                var t = e.IsLink && e.LinkTargetId != Guid.Empty ? e.LinkTargetId : e.Id;
                if (t != Guid.Empty) set.Add(t);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return set;
    }

    /// <summary>Creates a link in <paramref name="folder"/> for each live worn item not in
    /// <paramref name="skip"/>. Returns how many links were created.</summary>
    private async Task<int> LinkWornIntoAsync(
        LibreMetaverse.UUID folder, IReadOnlyList<WornItem> worn, HashSet<Guid> skip, CancellationToken ct)
    {
        int added = 0;
        foreach (var w in worn)
        {
            if (!w.Live || w.ItemId == Guid.Empty || skip.Contains(w.ItemId)) continue;
            ct.ThrowIfCancellationRequested();

            var linkName = w.Name;
            if (string.IsNullOrEmpty(linkName))
                linkName = (_client.Inventory.Store?.GetNodeOrDefault(new LibreMetaverse.UUID(w.ItemId))?.Data
                    as LibreMetaverse.InventoryItem)?.Name ?? string.Empty;

            var invType = w.Category is WornCategory.BodyPart or WornCategory.Clothing
                ? LibreMetaverse.InventoryType.Wearable
                : LibreMetaverse.InventoryType.Object;
            try
            {
                // CreateLinkAsync does NOT throw on an AIS rejection (e.g. "Create inventory in
                // <folder>: Bad Request") -- InventoryAISClient swallows it and resolves to a
                // null InventoryItem. Counting every call as `added` regardless of this return
                // value reported a link as saved when AIS had silently refused it, so the outfit
                // came back short after a relog with no error anywhere in the UI (v0.20.96).
                var created = await _client.Inventory.CreateLinkAsync(
                    folder, new LibreMetaverse.UUID(w.ItemId), linkName, string.Empty,
                    invType, LibreMetaverse.UUID.Zero, ct).ConfigureAwait(false);
                if (created != null)
                {
                    added++;
                }
                else
                {
                    Console.Error.WriteLine(
                        $"[Outfits] link create for {w.ItemId} ('{linkName}') into {folder} came back empty -- " +
                        "see the preceding 'Create inventory' warning for the AIS reason");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Outfits] CreateLinkAsync threw for {w.ItemId} ('{linkName}'): {ex.Message}");
            }
        }
        return added;
    }

    /// <summary>Saves what is worn right now into a <c>#Outfits</c> subfolder as inventory links.
    /// A same-named subfolder is reused rather than spawning a duplicate. Returns the folder id,
    /// or null if there's no <c>#Outfits</c> folder / the folder create failed. FEAT-INV-04.
    ///
    /// <para>On an AISv3 grid this is <c>LLAppearanceMgr::makeNewOutfitLinks</c>: create the
    /// folder, then one atomic <c>slamCategoryLinks(getCOF(), folder)</c> — the same COF-sourced
    /// slam as <see cref="ReplaceOutfitWithCurrentAsync"/>, so it never feeds AIS a scene
    /// <c>AttachItemID</c> that resolves to a link or a since-gone item (the
    /// <c>Create inventory in … Bad Request</c> pairs). OpenSim keeps the per-item link
    /// pass.</para></summary>
    public async Task<Guid?> SaveCurrentOutfitAsync(string name, CancellationToken ct = default)
    {
        if (MyOutfitsFolderId is not { } outfitsId) return null;
        name = string.IsNullOrWhiteSpace(name) ? "Outfit" : name.Trim();

        // Reuse an existing same-name outfit folder rather than creating a duplicate.
        var folder = LibreMetaverse.UUID.Zero;
        var outfitsNode = _client.Inventory.Store?.GetNodeOrDefault(new LibreMetaverse.UUID(outfitsId));
        if (outfitsNode != null)
            foreach (var n in outfitsNode.Nodes.Values)
                if (n.Data is LibreMetaverse.InventoryFolder f
                    && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                { folder = f.UUID; break; }

        bool freshlyCreated = folder == LibreMetaverse.UUID.Zero;
        if (freshlyCreated)
            folder = _client.Inventory.CreateFolder(new LibreMetaverse.UUID(outfitsId), name);
        if (folder == LibreMetaverse.UUID.Zero) return null;

        if (_client.AisClient?.IsAvailable == true)
        {
            // CreateFolder is a fire-and-forget UDP packet with a client-side UUID; give the
            // server a moment to register it before the slam PUT lands, and retry once.
            if (freshlyCreated) await SafeDelayAsync(600, ct).ConfigureAwait(false);
            try
            {
                if (await SlamOutfitLinksFromCofAsync(folder, ct).ConfigureAwait(false) is null && freshlyCreated)
                {
                    await SafeDelayAsync(1200, ct).ConfigureAwait(false);
                    await SlamOutfitLinksFromCofAsync(folder, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Outfits] new-outfit slam into {folder} failed: {ex.Message}");
            }
        }
        else
        {
            var worn = await GetWornItemsWithNamesAsync(ct).ConfigureAwait(false);
            var already = await GetOutfitTargetIdsAsync(folder.Guid, ct).ConfigureAwait(false);
            await LinkWornIntoAsync(folder, worn, already, ct).ConfigureAwait(false);
        }

        // Saving the look you are wearing makes that outfit the active one — the COF folder-link
        // marker the Outfits list reads (LLAppearanceMgr::makeNewOutfitLinks → createBaseOutfitLink).
        try { await SetCurrentOutfitLinkAsync(folder.Guid, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Console.Error.WriteLine($"[Outfits] set-active-outfit link failed: {ex.Message}"); }

        return folder.Guid;
    }

    private static async Task SafeDelayAsync(int ms, CancellationToken ct)
    {
        try { await Task.Delay(ms, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
    }

    /// <summary>Adds the current worn set to an existing outfit folder — links only the items that
    /// aren't already in it. Returns how many links were added. FEAT-INV-04.</summary>
    public async Task<int> AddCurrentToOutfitAsync(Guid outfitFolderId, CancellationToken ct = default)
    {
        if (outfitFolderId == Guid.Empty) return 0;
        var worn = await GetWornItemsWithNamesAsync(ct).ConfigureAwait(false);
        var already = await GetOutfitTargetIdsAsync(outfitFolderId, ct).ConfigureAwait(false);
        return await LinkWornIntoAsync(new LibreMetaverse.UUID(outfitFolderId), worn, already, ct).ConfigureAwait(false);
    }

    /// <summary>The decision half of <see cref="ReplaceOutfitWithCurrentAsync"/>: every entry in an
    /// outfit folder that is a <b>link</b> (those are deleted so the folder can be re-linked from
    /// scratch), plus the ids of entries that are real items rather than links — those are left
    /// alone, because deleting one would destroy inventory over an "edit this outfit" action (same
    /// rule as <see cref="SelectOutfitLinksToRemove"/>). Folders are ignored. Pure so it can be
    /// tested without a grid.</summary>
    internal static (List<Guid> LinkIds, List<Guid> NonLinkItemIds) SelectOutfitLinksToClear(
        IEnumerable<InventoryEntry> children)
    {
        var linkIds = new List<Guid>();
        var nonLinkItemIds = new List<Guid>();
        foreach (var e in children)
        {
            if (e.IsFolder) continue;
            if (e.IsLink) linkIds.Add(e.Id);
            else nonLinkItemIds.Add(e.Id);
        }
        return (linkIds, nonLinkItemIds);
    }

    /// <summary>One row of the Current Outfit folder, reduced to just what
    /// <see cref="SelectCofLinkTargetsToSlam"/> needs — engine-neutral so the selection is
    /// unit-testable without a grid.</summary>
    internal readonly record struct CofLinkRow(bool IsLink, bool IsFolderLink, Guid Target);

    /// <summary>Pure: the ordered, de-duplicated list of <b>link targets</b> to write when
    /// slamming a saved outfit from the Current Outfit folder. Item-links only — real items,
    /// subfolders and the COF folder-link are excluded, and a broken (targetless) link is
    /// dropped. Mirrors <c>LLAppearanceMgr::slamCategoryLinks</c> with
    /// <c>include_folder_links = false</c>.</summary>
    internal static List<Guid> SelectCofLinkTargetsToSlam(IEnumerable<CofLinkRow> rows)
    {
        var outp = new List<Guid>();
        var seen = new HashSet<Guid>();
        foreach (var r in rows)
        {
            if (!r.IsLink || r.IsFolderLink || r.Target == Guid.Empty) continue;
            if (!seen.Add(r.Target)) continue;
            outp.Add(r.Target);
        }
        return outp;
    }

    /// <summary>Replaces an existing outfit folder's contents with what is worn right now.
    /// FEAT-INV-04.
    ///
    /// <para>On any grid with AISv3 (real Second Life) this is <b>one atomic "slam"</b> that
    /// rewrites the outfit folder's entire link set from the resolved Current-Outfit-Folder
    /// links — exactly <c>LLAppearanceMgr::updateBaseOutfit → slamCategoryLinks →
    /// AISAPI::SlamFolder</c> in the reference viewer. No delete pass and no per-item
    /// <c>CreateInventory</c> POST (each of which AIS can reject on its own — link-to-link, an
    /// unresolved target, an item already sitting in the folder — which is what produced the
    /// <c>warn: Create inventory in … Bad Request</c> pairs that silently dropped clothing from a
    /// saved outfit). Real (non-link) items already in the folder are left untouched because a
    /// slam only rewrites links. Returns the number of links written, or <c>-1</c> if the COF is
    /// not fully loaded yet (the caller shows "try again in a moment" rather than slam a
    /// truncated outfit — the failure mode <c>v0.20.36</c> was created to prevent).</para>
    ///
    /// <para>OpenSim and other AIS-less grids fall through to the legacy path: delete the
    /// folder's links (<c>RemoveItemsAsync</c>, not <c>MoveItem → Trash</c> which 400s on SL),
    /// then re-link the worn set, skipping any worn item already present as a real item in the
    /// folder.</para></summary>
    public async Task<int> ReplaceOutfitWithCurrentAsync(Guid outfitFolderId, CancellationToken ct = default)
    {
        if (outfitFolderId == Guid.Empty) return 0;
        var folderUuid = new LibreMetaverse.UUID(outfitFolderId);

        if (_client.AisClient?.IsAvailable == true)
        {
            var slammed = await SlamOutfitLinksFromCofAsync(folderUuid, ct).ConfigureAwait(false);
            return slammed ?? -1; // null == COF still loading
        }

        return await ReplaceOutfitLegacyAsync(folderUuid, outfitFolderId, ct).ConfigureAwait(false);
    }

    /// <summary>The Firestorm "Save Outfit" mechanism: take the resolved Current-Outfit-Folder
    /// links and PUT them as <paramref name="outfitFolder"/>'s entire link set in one AIS
    /// request (<c>AISAPI::SlamFolder</c> → <c>PUT {cap}/category/{id}/links</c>, body a bare
    /// LLSD array of <c>{name, desc, linked_id, type}</c> maps — matched to
    /// <c>LLAppearanceMgr::slamCategoryLinks</c>). Returns the link count, or <c>null</c> when
    /// the COF is not fully resolved in the store yet.</summary>
    private async Task<int?> SlamOutfitLinksFromCofAsync(LibreMetaverse.UUID outfitFolder, CancellationToken ct)
    {
        var cofUuid = _client.Inventory.FindFolderForType(LibreMetaverse.FolderType.CurrentOutfit);
        if (cofUuid == LibreMetaverse.UUID.Zero) return null;

        // Authoritative refetch so the slam list is not a stale local snapshot.
        try { await FetchInventoryChildrenAsync(cofUuid.Guid, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { /* fall back to whatever the store already holds */ }

        var store = _client.Inventory.Store;
        var cofNode = cofUuid != LibreMetaverse.UUID.Zero ? store?.GetNodeOrDefault(cofUuid) : null;
        if (cofNode == null) return null;

        var rows = cofNode.Nodes.Values.Select(n =>
        {
            if (n.Data is not LibreMetaverse.InventoryItem li || !li.IsLink())
                return new CofLinkRow(false, false, Guid.Empty);
            var t = li.ResolvedItemID != LibreMetaverse.UUID.Zero ? li.ResolvedItemID : li.AssetUUID;
            return new CofLinkRow(true, li.AssetType == LibreMetaverse.AssetType.LinkFolder, t.Guid);
        });
        var targets = SelectCofLinkTargetsToSlam(rows);
        if (targets.Count == 0)
        {
            Console.Error.WriteLine("[Outfits] slam aborted — no resolvable item-links in the Current Outfit folder");
            return null;
        }

        // Readiness gate (mirrors CleanUpCurrentOutfit's storeReady): every target node must be
        // in the store, or the list we just built could be missing links that haven't streamed in.
        int unresolved = targets.Count(g =>
            store?.GetNodeOrDefault(new LibreMetaverse.UUID(g))?.Data is not LibreMetaverse.InventoryItem);
        if (unresolved > 0)
        {
            Console.Error.WriteLine(
                $"[Outfits] slam deferred — COF still loading ({unresolved}/{targets.Count} link targets not in the store)");
            return null;
        }

        var contents = new OSDArray();
        foreach (var g in targets)
        {
            var target = new LibreMetaverse.UUID(g);
            var name = (store?.GetNodeOrDefault(target)?.Data as LibreMetaverse.InventoryItem)?.Name ?? string.Empty;
            contents.Add(new OSDMap
            {
                ["name"] = OSD.FromString(name),
                ["desc"] = OSD.FromString(string.Empty),
                ["linked_id"] = OSD.FromUUID(target),
                ["type"] = OSD.FromInteger((int)LibreMetaverse.AssetType.Link),
            });
        }

        bool ok = await _client.AisClient.SlamFolderAsync(outfitFolder, contents, ct).ConfigureAwait(false);
        if (!ok)
            throw new InvalidOperationException($"AIS rejected the outfit slam for {outfitFolder}");

        // Reconcile the local store with what the server now holds.
        try { await FetchInventoryChildrenAsync(outfitFolder.Guid, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { }

        Console.Error.WriteLine($"[Outfits] slammed {contents.Count} link(s) into outfit {outfitFolder} from the Current Outfit folder");
        return contents.Count;
    }

    /// <summary>Pre-AISv3 replace path (OpenSim): delete the outfit folder's links, then re-link
    /// the current worn set. See <see cref="ReplaceOutfitWithCurrentAsync"/>.</summary>
    private async Task<int> ReplaceOutfitLegacyAsync(
        LibreMetaverse.UUID folderUuid, Guid outfitFolderId, CancellationToken ct)
    {
        var worn = await GetWornItemsWithNamesAsync(ct).ConfigureAwait(false);

        IReadOnlyList<InventoryEntry> existing;
        try { existing = await FetchInventoryChildrenAsync(outfitFolderId, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { existing = Array.Empty<InventoryEntry>(); }

        var (linkGuids, nonLinkItemIds) = SelectOutfitLinksToClear(existing);
        if (nonLinkItemIds.Count > 0)
            Console.Error.WriteLine(
                $"[Outfits] outfit {outfitFolderId} holds {nonLinkItemIds.Count} real item(s), not links — " +
                "leaving them in place; replace only rewrites the outfit's links");

        if (linkGuids.Count > 0)
        {
            var linkIds = linkGuids.Select(g => new LibreMetaverse.UUID(g)).ToList();
            try { await _client.Inventory.RemoveItemsAsync(linkIds, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Console.Error.WriteLine($"[Outfits] RemoveItemsAsync threw: {ex.Message}"); }
        }

        var skip = new HashSet<Guid>(nonLinkItemIds);
        return await LinkWornIntoAsync(folderUuid, worn, skip, ct).ConfigureAwait(false);
    }

    /// <summary>The contents of a saved outfit folder as resolved <see cref="WornItem"/>s — each
    /// link's <b>target</b> name / asset type (not the link's own), category, and whether that
    /// item is worn right now (<c>Live</c>). Fires <c>RequestFetchInventory</c> for any target the
    /// store doesn't have yet, so a second call fills the gaps. FEAT-INV-04.</summary>
    public async Task<IReadOnlyList<WornItem>> GetOutfitContentsAsync(Guid outfitFolderId, CancellationToken ct = default)
    {
        var children = await FetchInventoryChildrenAsync(outfitFolderId, ct).ConfigureAwait(false);
        var store = _client.Inventory.Store;
        var wornNow = new HashSet<Guid>(GetWornItems().Where(w => w.Live).Select(w => w.ItemId));

        var result = new List<WornItem>();
        var toFetch = new Dictionary<LibreMetaverse.UUID, LibreMetaverse.UUID>();
        var seen = new HashSet<Guid>();

        foreach (var e in children)
        {
            if (e.IsFolder) continue;

            var targetId = e.IsLink && e.LinkTargetId != Guid.Empty ? e.LinkTargetId : e.Id;
            if (targetId != Guid.Empty && !seen.Add(targetId)) continue; // dedup duplicate links
            var targetUuid = new LibreMetaverse.UUID(targetId);
            var target = store?.GetNodeOrDefault(targetUuid)?.Data as LibreMetaverse.InventoryItem;

            int assetType = target != null ? (int)target.AssetType : e.AssetType;
            string name = target?.Name ?? string.Empty;
            // The link's own name is only useful if it isn't the "Link" placeholder.
            if (name.Length == 0 && !string.Equals(e.Name, "Link", StringComparison.OrdinalIgnoreCase))
                name = e.Name;
            if (name.Length == 0 && targetUuid != LibreMetaverse.UUID.Zero
                && _wornDetailFetchRequested.Add(targetUuid.Guid))
                toFetch[targetUuid] = _client.Self.AgentID;

            var cat = assetType == (int)LibreMetaverse.AssetType.Bodypart ? WornCategory.BodyPart
                : assetType == (int)LibreMetaverse.AssetType.Clothing ? WornCategory.Clothing
                : assetType == (int)LibreMetaverse.AssetType.Object ? WornCategory.Attachment
                : WornCategory.Clothing; // unresolved link — usually a wearable; refines once fetched

            result.Add(new WornItem(targetId, name, cat, null, assetType, Live: wornNow.Contains(targetId)));
        }

        if (toFetch.Count > 0)
        {
            try { _client.Inventory.RequestFetchInventory(toFetch); } catch { }
        }
        return result;
    }

    /// <summary>
    /// FEAT-INV-05: removes one item from ONE saved outfit — the "Aus diesem Outfit entfernen"
    /// action. Deletes the <b>link</b> to <paramref name="itemId"/> inside
    /// <paramref name="outfitFolderId"/> and nothing else: not the inventory item, not the same
    /// item's link in any other outfit, and not its Current-Outfit link (taking a thing off is
    /// <see cref="DetachItemAsync"/>, a different verb the menu offers separately).
    ///
    /// <para><b>Only links are ever deleted.</b> An outfit folder normally holds nothing else, but
    /// if a real item has been dropped into one, deleting it would destroy inventory over a menu
    /// entry that promises to edit an outfit — so a non-link match is refused and reported instead.
    /// </para>
    ///
    /// <para><c>RemoveItemsAsync</c>, not <c>MoveItem → Trash</c>: a move 400s on SL and the entry
    /// simply reappears on the next refetch (BUG-INV-01 / v0.20.33 switched the Current-Outfit
    /// cleanup off that same path for the same reason). Returns how many links were removed.</para>
    /// </summary>
    /// <summary>The decision half of <see cref="RemoveItemFromOutfitFolderAsync"/>, pure so it can
    /// be tested without a grid: which of a folder's entries are LINKS to <paramref name="itemId"/>
    /// (all of them — a duplicate link left behind looks like the action failed), and how many
    /// matches were real items rather than links, which the caller must refuse to delete.</summary>
    internal static (List<Guid> LinkIds, int NonLinkMatches) SelectOutfitLinksToRemove(
        IEnumerable<InventoryEntry> children, Guid itemId)
    {
        var linkIds = new List<Guid>();
        int nonLinkMatches = 0;
        if (itemId == Guid.Empty) return (linkIds, 0);

        foreach (var e in children)
        {
            if (e.IsFolder) continue;
            var target = e.IsLink && e.LinkTargetId != Guid.Empty ? e.LinkTargetId : e.Id;
            if (target != itemId) continue;

            if (e.IsLink) linkIds.Add(e.Id);
            else nonLinkMatches++;
        }
        return (linkIds, nonLinkMatches);
    }

    public async Task<int> RemoveItemFromOutfitFolderAsync(Guid outfitFolderId, Guid itemId, CancellationToken ct = default)
    {
        if (outfitFolderId == Guid.Empty || itemId == Guid.Empty) return 0;

        var children = await FetchInventoryChildrenAsync(outfitFolderId, ct).ConfigureAwait(false);
        var (linkGuids, nonLinkMatches) = SelectOutfitLinksToRemove(children, itemId);
        var linkIds = linkGuids.Select(g => new LibreMetaverse.UUID(g)).ToList();

        if (nonLinkMatches > 0)
            Console.Error.WriteLine(
                $"[Outfits] {itemId} sits in outfit {outfitFolderId} as a REAL item, not a link — " +
                "refusing to delete it; move it out by hand if that is what you want");

        if (linkIds.Count == 0) return 0;

        await _client.Inventory.RemoveItemsAsync(linkIds, ct).ConfigureAwait(false);
        Console.Error.WriteLine($"[Outfits] removed {linkIds.Count} link(s) to {itemId} from outfit {outfitFolderId}");
        return linkIds.Count;
    }

    /// <summary>Wears the <b>attachment</b> part of a saved outfit — every link in
    /// <paramref name="outfitFolderId"/> whose target is an <c>AssetType.Object</c>. Wearables
    /// (Clothing/Bodypart) are skipped: applying those is a rebake and waits on FEAT-AVATAR-01
    /// Phase 2. Returns how many attach calls were sent. FEAT-INV-04.</summary>
    public async Task<int> WearOutfitAttachmentsAsync(Guid outfitFolderId, CancellationToken ct = default)
    {
        var children = await FetchInventoryChildrenAsync(outfitFolderId, ct).ConfigureAwait(false);
        var store = _client.Inventory.Store;
        int sent = 0;

        foreach (var e in children)
        {
            if (e.IsFolder) continue;
            ct.ThrowIfCancellationRequested();

            var targetUuid = new LibreMetaverse.UUID(e.IsLink ? e.LinkTargetId : e.Id);
            var target = store?.GetNodeOrDefault(targetUuid)?.Data as LibreMetaverse.InventoryItem;
            var assetType = target?.AssetType ?? (LibreMetaverse.AssetType)e.AssetType;

            // Skip only what we can positively identify as a wearable; attach the rest (an
            // Attach for a wearable is a server-side no-op anyway).
            if (assetType is LibreMetaverse.AssetType.Clothing or LibreMetaverse.AssetType.Bodypart)
                continue;

            await AttachItemAsync(e.Id, replace: false).ConfigureAwait(false);
            sent++;
        }
        return sent;
    }

    /// <summary>Makes the avatar's <b>attachments</b> match a saved outfit's: detaches every worn
    /// attachment the outfit doesn't contain, then attaches the outfit's objects that aren't worn.
    /// Items common to both are left alone. Clothing / body parts are untouched — swapping those
    /// is a rebake (FEAT-AVATAR-01 Phase 2). Returns (detached, attached). FEAT-INV-04.</summary>
    public async Task<(int Detached, int Attached)> ReplaceWornWithOutfitAttachmentsAsync(
        Guid outfitFolderId, CancellationToken ct = default)
    {
        var contents = await GetOutfitContentsAsync(outfitFolderId, ct).ConfigureAwait(false);
        if (contents.Any(w => string.IsNullOrEmpty(w.Name)))
        {
            try { await Task.Delay(800, ct).ConfigureAwait(false); } catch (OperationCanceledException) { throw; }
            contents = await GetOutfitContentsAsync(outfitFolderId, ct).ConfigureAwait(false);
        }

        // Everything the outfit references (so we never detach an item it wants to keep), and the
        // subset we're confident is an attachment (so we only attach real objects).
        var targetAll = new HashSet<Guid>(contents.Select(w => w.ItemId));
        var targetObjs = new HashSet<Guid>(contents
            .Where(w => w.Category is WornCategory.Attachment or WornCategory.Hud)
            .Select(w => w.ItemId));

        var wornAttach = GetSceneWornAttachments().Keys.ToHashSet();

        int detached = 0, attached = 0;

        foreach (var id in wornAttach)
        {
            if (targetAll.Contains(id)) continue;
            ct.ThrowIfCancellationRequested();
            await DetachItemAsync(id).ConfigureAwait(false);
            detached++;
        }

        foreach (var id in targetObjs)
        {
            if (wornAttach.Contains(id)) continue;
            ct.ThrowIfCancellationRequested();
            await AttachItemAsync(id, replace: false).ConfigureAwait(false);
            attached++;
        }

        await SetCurrentOutfitLinkAsync(outfitFolderId, ct).ConfigureAwait(false);
        return (detached, attached);
    }

    /// <summary>Points the Current Outfit Folder at a saved outfit — trashes any existing
    /// folder-link in the COF and creates one to <paramref name="outfitFolderId"/>. This is the
    /// marker SL / Firestorm (and <see cref="GetSavedOutfitsAsync"/>) use for "the outfit you're
    /// wearing". FEAT-INV-04.</summary>
    private async Task SetCurrentOutfitLinkAsync(Guid outfitFolderId, CancellationToken ct)
    {
        if (outfitFolderId == Guid.Empty) return;
        var cofUuid = _client.Inventory.FindFolderForType(LibreMetaverse.FolderType.CurrentOutfit);
        if (cofUuid == LibreMetaverse.UUID.Zero) return;

        try
        {
            await FetchInventoryChildrenAsync(cofUuid.Guid, ct).ConfigureAwait(false);
            var cofNode = _client.Inventory.Store?.GetNodeOrDefault(cofUuid);
            if (cofNode != null)
            {
                // DELETE, not MoveItem → Trash: moving a COF link 400s on AIS (SL) and the link
                // stays put -- same reason BUG-INV-01 switched the other COF cleanups off that path.
                var oldFolderLinks = cofNode.Nodes.Values.ToList()
                    .Where(n => n.Data is LibreMetaverse.InventoryItem it
                                && it.AssetType == LibreMetaverse.AssetType.LinkFolder)
                    .Select(n => n.Data.UUID)
                    .ToList();
                if (oldFolderLinks.Count > 0)
                    await _client.Inventory.RemoveItemsAsync(oldFolderLinks, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        var name = (_client.Inventory.Store?.GetNodeOrDefault(new LibreMetaverse.UUID(outfitFolderId))?.Data
            as LibreMetaverse.InventoryFolder)?.Name ?? "Outfit";
        try
        {
            await _client.Inventory.CreateLinkAsync(
                cofUuid, new LibreMetaverse.UUID(outfitFolderId), name, string.Empty,
                LibreMetaverse.InventoryType.Folder, LibreMetaverse.UUID.Zero, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch { }
    }

    /// <summary>Takes off the <b>attachment</b> part of a saved outfit — detaches every currently
    /// worn attachment the outfit contains. Clothing / body parts untouched. Returns how many were
    /// detached. FEAT-INV-04.</summary>
    public async Task<int> RemoveOutfitFromWornAsync(Guid outfitFolderId, CancellationToken ct = default)
    {
        var contents = await GetOutfitContentsAsync(outfitFolderId, ct).ConfigureAwait(false);
        var ids = new HashSet<Guid>(contents.Select(w => w.ItemId));
        var wornAttach = GetSceneWornAttachments().Keys.ToHashSet();

        int removed = 0;
        foreach (var id in wornAttach)
        {
            if (!ids.Contains(id)) continue;
            ct.ThrowIfCancellationRequested();
            await DetachItemAsync(id).ConfigureAwait(false);
            removed++;
        }

        // FEAT-AVATAR-01: the outfit's system wearables too. This used to stop at attachments
        // ("Kleidung & Körper unverändert (Phase 2)") only because a wearable could not be removed
        // at all -- DetachAttachmentIntoInv is a server-side no-op for a Clothing/Bodypart layer.
        // DetachItemAsync now routes those through the COF + AgentIsNowWearing path instead, so the
        // limitation is gone. Only the ones actually worn: an outfit lists what it contains, and
        // GetWornItems says what is on right now.
        var wornWearables = GetWornItems()
            .Where(w => w.Live && w.Category is WornCategory.Clothing or WornCategory.BodyPart)
            .Select(w => w.ItemId)
            .ToHashSet();

        foreach (var id in wornWearables)
        {
            if (!ids.Contains(id)) continue;
            ct.ThrowIfCancellationRequested();
            var result = await DetachItemAsync(id).ConfigureAwait(false);
            if (result.WearableRemoved) removed++;
        }

        return removed;
    }

    /// <summary>Renames a saved outfit folder. FEAT-INV-04.</summary>
    public bool RenameOutfitAsync(Guid folderId, string newName)
    {
        newName = newName?.Trim() ?? string.Empty;
        if (folderId == Guid.Empty || newName.Length == 0) return false;
        var node = _client.Inventory.Store?.GetNodeOrDefault(new LibreMetaverse.UUID(folderId));
        if (node?.Data is not LibreMetaverse.InventoryFolder f) return false;
        _client.Inventory.UpdateFolderProperties(f.UUID, f.ParentUUID, newName, f.PreferredType);
        return true;
    }

    /// <summary>Deletes a saved outfit folder (recoverable — on SL an AIS category delete lands it
    /// in Trash; the linked items stay in inventory). FEAT-INV-04.
    ///
    /// <para><c>RemoveFolderAsync</c> (AIS <c>DELETE {cap}/category/{id}</c> on SL, a
    /// <c>RemoveInventoryObjects</c> packet on OpenSim), <b>not</b> <c>MoveFolder → Trash</c>:
    /// a <c>parent_id</c> PATCH of an <c>#Outfits</c> subfolder HTTP-400s on SL
    /// (<c>warn: Move category … Bad Request</c>) and the outfit stayed visible — the same
    /// move-to-Trash trap BUG-INV-01 already retired for items and COF links.</para></summary>
    public bool DeleteOutfitAsync(Guid folderId)
    {
        if (folderId == Guid.Empty) return false;
        var folderUuid = new LibreMetaverse.UUID(folderId);
        if (_client.Inventory.Store?.GetNodeOrDefault(folderUuid)?.Data is not LibreMetaverse.InventoryFolder)
            return false;

        if (_client.AisClient?.IsAvailable == true)
        {
            _ = _client.Inventory.RemoveFolderAsync(folderUuid, System.Threading.CancellationToken.None);
        }
        else
        {
            if (TrashFolderId is not { } trashId || trashId == Guid.Empty) return false;
            _client.Inventory.MoveFolder(folderUuid, new LibreMetaverse.UUID(trashId));
        }

        var node = _client.Inventory.Store?.GetNodeOrDefault(folderUuid);
        if (node != null) node.Parent?.Nodes.Remove(folderUuid);
        return true;
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

        // BUG-RENDER-06 follow-up: leaf faces on Agni carry a real legacy-material id
        // (seen in [FaceParams] as mat=...) but the whole material never resolved, so every such
        // face fell back to DetectAlpha() and landed in the sorted transparent pass -> flicker.
        // This says which link in the chain is broken: no RenderMaterials cap on this region at
        // all, vs. the cap present but the fetch coming back with fewer (or zero) materials than
        // asked for. One line per fetch call (already batched/debounced upstream in AssetService).
        var capUri = sim.Caps?.CapabilityURI("RenderMaterials");
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

        Console.Error.WriteLine($"[LegacyMat] requested {materialIds.Count} -> resolved {result.Count} " +
            $"(RenderMaterials cap {(capUri == null ? "MISSING on this region" : "present")})");

        return result;
    }

    /// <summary>MATERIALS_GET_MAX_ENTRIES (llmaterialmgr.cpp:58). The sim rejects more.</summary>
    private const int MaterialsPerRequest = 50;

    /// <summary>BUG-RENDER-06 follow-up: LibreMetaverse 3.1.3's <c>ObjectManager.RequestMaterialsAsync</c>
    /// builds the RenderMaterials query by <c>array.Add(uuid)</c>, which serialises each id as an
    /// LLSD <c>uuid</c> element. The real viewer sends each id as an LLSD <b>binary(16)</b>:
    /// <c>llmaterialmgr.cpp</c> <c>processGetQueue</c> does <c>materialsData.append((*itMaterial).asLLSD())</c>,
    /// and <c>LLMaterialID::asLLSD()</c> is a 16-byte <c>LLSD::Binary</c> (llmaterialid.cpp). The sim
    /// unzips the request and reads each entry with <c>.asBinary()</c>, which yields nothing for a
    /// <c>uuid</c>-typed element -- so LMV's request matches zero materials and the cap returns an
    /// empty result with no error. Every Blinn-Phong-materialled face on SL then fell back to
    /// <c>Image.DetectAlpha()</c>, and a soft-alpha foliage texture guessed as BLEND lands in the
    /// sorted transparent pass -> the leaf-canopy flicker report. We build and POST the query
    /// ourselves so the ids go out as binary; the response shape is unchanged, so LMV's own
    /// <c>LegacyMaterial(OSDMap)</c> still parses each returned entry.</summary>
    internal static OSDMap BuildRenderMaterialsQuery(IEnumerable<LibreMetaverse.UUID> ids)
    {
        var array = new OSDArray();
        foreach (var id in ids) array.Add(OSD.FromBinary(id.GetBytes()));
        return new OSDMap { ["Zipped"] = OSD.FromBinary(Helpers.ZCompressOSD(array)) };
    }

    private async Task FetchOneBatchAsync(
        LibreMetaverse.Simulator sim, List<LibreMetaverse.UUID> ids,
        List<LegacyMaterialData> into, CancellationToken cancellationToken)
    {
        var uri = sim.Caps?.CapabilityURI("RenderMaterials");
        if (uri == null) return;

        try
        {
            var request = BuildRenderMaterialsQuery(ids);
            var (res, data) = await _client.HttpCapsClient
                .PostAsync(uri, OSDFormat.Xml, request, cancellationToken).ConfigureAwait(false);

            int status = (int)(res?.StatusCode ?? 0);
            if (data == null || data.Length == 0)
            {
                Console.Error.WriteLine($"[LegacyMat] POST {status}: empty body for {ids.Count} ids");
                return;
            }

            if (OSDParser.Deserialize(data) is not OSDMap top || !top.ContainsKey("Zipped"))
            {
                Console.Error.WriteLine($"[LegacyMat] POST {status}: no Zipped field; body starts: " +
                    System.Text.Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 200)));
                return;
            }

            if (Helpers.ZDecompressOSD(top["Zipped"].AsBinary()) is not OSDArray mats)
            {
                Console.Error.WriteLine($"[LegacyMat] POST {status}: unzipped payload is not an array");
                return;
            }

            int before = into.Count;
            foreach (var entry in mats)
            {
                if (entry is not OSDMap em) continue;
                try { into.Add(ToLegacyMaterialData(new LibreMetaverse.Materials.LegacyMaterial(em))); }
                catch (Exception ex) { Console.Error.WriteLine($"[LegacyMat] entry parse failed: {ex.Message}"); }
            }
            Console.Error.WriteLine($"[LegacyMat] POST {status}: {ids.Count} ids -> {into.Count - before} materials");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LegacyMat] request for {ids.Count} legacy materials failed: {ex.Message}");
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
    private static readonly System.Threading.SemaphoreSlim _textureFetchSemaphore = new System.Threading.SemaphoreSlim(8, 8);

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
        /// <summary>The sim's own GetTexture/ViewerAsset cap answered 403/401 -- a permission
        /// decision, not a transient error. No transport will get these bytes; the caller should
        /// stop retrying rather than fall this back onto LibreMetaverse's UDP pipeline (which just
        /// re-hits the same wall and spams "Failed to fetch texture ... Forbidden"). Only the
        /// generic per-face path sets this -- the bake path (BUG-AVATAR-02) has a real fallback.</summary>
        public bool Gone;
    }

    /// <summary>BUG-AVATAR-02: fetches an avatar BAKE texture. Every single bake channel was
    /// coming back <c>HTTP 403</c> ("AccessDenied", a raw S3 error body) through the normal asset
    /// path (<see cref="FetchTextureDataAsync"/>) -- confirmed by capturing the same texture id
    /// fetched successfully by Firestorm: it used a completely different host and URL shape,
    /// <c>http://bake-texture.glb.{grid}.lindenlab.com/texture/{agent}/{slot}/{textureId}</c>, not
    /// the generic <c>GetTexture</c>/<c>ViewerAsset</c> CAP's <c>?texture_id=</c> query. That host
    /// is real, documented reference-viewer infrastructure -- <c>llappcorehttp.h</c> lists
    /// <c>bake-texture</c> as its own HTTP connection-pool destination, distinct from the general
    /// asset <c>cdn</c> -- not something a capability hands out; the reference viewer constructs it
    /// itself from the grid name, the agent id, and the bake slot name (see
    /// <see cref="BakeChannelNames"/> for where the eleven slot-name strings come from).
    ///
    /// Only meaningful on a Linden grid (<see cref="_lindenGridShortName"/> is null everywhere
    /// else -- OpenSim has no such host); returns a failed result immediately otherwise so the
    /// caller can fall back to the normal path without paying for a fetch that cannot succeed.
    /// Always a full fetch (no Range/desiredDiscard) -- the reference viewer does the same for
    /// baked textures, to reduce interim blurring while the bake streams in.
    ///
    /// <paramref name="agentId"/> is the id of the avatar WEARING this bake, and it is part of the
    /// URL path -- the reference viewer builds it from <c>LLVOAvatar::getID()</c>
    /// (<c>llvoavatar.cpp</c> <c>getImageURL</c>: <c>url = ... + "texture/" + getID().asString() +
    /// "/" + mDefaultImageName + "/" + uuid.asString()</c>), which is the DISPLAYED avatar, not the
    /// viewing agent. Passing our own id for someone else's bake gets a flat 403 from the CDN and
    /// then a second 403 from the generic fallback, leaving every other mesh-body avatar untextured
    /// (found live on Agni 2026-09-02: only the local avatar's own bakes ever resolved). Empty
    /// falls back to our own id so a caller that only ever deals with the local avatar can omit
    /// it.</summary>
    public async Task<TextureFetchResult> FetchBakeTextureDataAsync(Guid textureId, int bakeChannel, Guid agentId = default, CancellationToken ct = default)
    {
        if (_lindenGridShortName == null) return new TextureFetchResult { Data = null, IsReliable = false };

        var slot = SLNG.Core.BakeChannelNames.NameFor(bakeChannel);
        if (slot == null) return new TextureFetchResult { Data = null, IsReliable = false };

        var owner = agentId == Guid.Empty ? _client.Self.AgentID.Guid : agentId;
        var url = BuildBakeTextureUrl(_lindenGridShortName, owner, slot, textureId);
        var bytes = await FetchTextureViaHttpRangeAsync(textureId, desiredDiscard: 0, capUri: null, maxRetries: 2, fetchUrl: url).ConfigureAwait(false);
        return new TextureFetchResult { Data = bytes, IsReliable = bytes != null };
    }

    /// <summary>Builds the dedicated bake-texture CDN URL -- pulled out as a pure function so a
    /// test can pin the shape (and specifically that the OWNING avatar's id, not the viewer's,
    /// lands in the path). Mirrors <c>LLVOAvatar::getImageURL</c>; see
    /// <see cref="FetchBakeTextureDataAsync"/>.</summary>
    internal static Uri BuildBakeTextureUrl(string gridShortName, Guid agentId, string slot, Guid textureId) =>
        new($"http://bake-texture.glb.{gridShortName}.lindenlab.com/texture/{agentId}/{slot}/{textureId}");

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
        // Already proven permanently denied (403/401) by the generic cap earlier this session --
        // don't re-run the HTTP attempts and, crucially, don't fall through to LibreMetaverse's
        // UDP pipeline, which re-hits the same wall and spams its own logger (a remote avatar's
        // hair face flickering all session, 2026-09-03). A relog / restart clears the set.
        if (_permanentlyDeniedTextures.ContainsKey(textureId))
            return new TextureFetchResult { Data = null, IsReliable = true, Gone = true };

        // Already 403'd on HTTP this session: skip the caps entirely (a permission decision will
        // not have changed) but still go down to the UDP pipeline below -- see _httpDeniedTextures.
        bool httpDenied = _httpDeniedTextures.ContainsKey(textureId);
        skipHttp = skipHttp || httpDenied;

        var capUri = skipHttp ? null : _client.Network.CurrentSim?.Caps?.GetTextureCapURI();
        var viewerAssetCap = skipHttp ? null : _client.Network.CurrentSim?.Caps?.CapabilityURI("ViewerAsset");
        var getTextureCap = skipHttp ? null : _client.Network.CurrentSim?.Caps?.CapabilityURI("GetTexture");

        if (viewerAssetCap != null)
        {
            var httpResult = await FetchTextureViaHttpRangeAsync(textureId, desiredDiscard, viewerAssetCap).ConfigureAwait(false);
            if (httpResult != null) return new TextureFetchResult { Data = httpResult, IsReliable = true };
        }

        if (getTextureCap != null && getTextureCap != viewerAssetCap)
        {
            var httpResult = await FetchTextureViaHttpRangeAsync(textureId, desiredDiscard, getTextureCap, maxRetries: 5).ConfigureAwait(false);
            if (httpResult != null) return new TextureFetchResult { Data = httpResult, IsReliable = true };
        }

        // A cap attempt just above may have marked this 403/401. That is NOT the end of the road:
        // Firestorm shows textures the generic cap refuses us, so the UDP pipeline below gets one
        // attempt. It is only recorded as genuinely gone if that comes back empty too.
        bool deniedByCaps = httpDenied || _httpDeniedTextures.ContainsKey(textureId);

        // Every "UDP produced nothing" exit goes through this. When the caps already refused the
        // id, that combination -- 403 on HTTP AND empty over UDP -- is the only evidence strong
        // enough to call a texture genuinely gone and stop asking for the rest of the session.
        // Without the caps denial an empty UDP result is just a normal transient failure and
        // AssetService's own 45 s negative cache handles it.
        TextureFetchResult UdpGaveNothing(byte[]? data)
        {
            if (data is { Length: > 0 }) return new TextureFetchResult { Data = data, IsReliable = false };
            if (deniedByCaps && _permanentlyDeniedTextures.TryAdd(textureId, 0))
                Console.Error.WriteLine($"[TextureFetch] {textureId}: 403 on HTTP and nothing over UDP -- giving up for this session");
            return new TextureFetchResult { Data = null, IsReliable = deniedByCaps, Gone = deniedByCaps };
        }

        var tcs = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var pipeline = typeof(AssetManager).GetField("Texture", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(_client.Assets);
            if (pipeline == null)
                return UdpGaveNothing(UdpFailed(textureId, "AssetManager.Texture field not found (reflection)"));
            {
                var reqMethod = pipeline.GetType().GetMethod("RequestTexture", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (reqMethod == null)
                    return UdpGaveNothing(UdpFailed(textureId, "TexturePipeline.RequestTexture not found (reflection)"));
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
                        return UdpGaveNothing(UdpFailed(textureId, "TexturePipeline never called back within 20s"));

                    var udpBytes = await tcs.Task.ConfigureAwait(false);
                    if (udpBytes is { Length: > 0 })
                    {
                        if (deniedByCaps)
                            Console.Error.WriteLine($"[TextureFetch] {textureId}: HTTP 403 but UDP delivered {udpBytes.Length} bytes -- the asset exists, the CDN just would not serve it");
                        return new TextureFetchResult { Data = udpBytes, IsReliable = false };
                    }
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

        return UdpGaveNothing(await fallbackTcs.Task);
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

    // Texture ids the sim's own GetTexture/ViewerAsset cap answered 403/401 for. A permission
    // decision does not change within a session, so HTTP is never retried for these -- that
    // unwinnable retry loop was the flickering remote-avatar hair face (2026-09-03).
    // NOT populated for the bake path (fetchUrl != null): BUG-AVATAR-02's bake 403 has a real
    // fallback to the generic cap.
    //
    // v0.20.40 also skipped the UDP fallback for these, on the reasoning that "no transport will
    // get the bytes". That reasoning was wrong, and the user disproved it: Firestorm displays the
    // very texture SLNG gives up on (dda710d4, a remote avatar's hair, confirmed 2026-09-03). The
    // asset EXISTS and is servable -- the CDN just will not serve it over the generic cap. So an
    // HTTP 403 now costs exactly ONE UDP attempt through LibreMetaverse's legacy image transfer,
    // the same transport the reference viewer falls back to; only if that also comes back empty is
    // the id recorded as genuinely gone.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _httpDeniedTextures = new();

    /// <summary>Ids that 403'd on HTTP **and** produced nothing over UDP. Session-permanent, and
    /// the only set that makes <see cref="TextureFetchResult.Gone"/> true -- an id in
    /// <see cref="_httpDeniedTextures"/> alone is still worth one UDP attempt per request.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _permanentlyDeniedTextures = new();

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

    /// <summary><paramref name="fetchUrl"/>, when given, is used verbatim instead of the usual
    /// <c>{capUri}?texture_id={id}</c> shape -- BUG-AVATAR-02's bake-texture fetch needs a
    /// completely different URL (<c>/texture/&lt;agent&gt;/&lt;slot&gt;/&lt;id&gt;</c>, no query
    /// string, always a full fetch) but the same validation and retry logic below, which is why
    /// this exists as an extra parameter here rather than a copy of the whole method.</summary>
    private async Task<byte[]?> FetchTextureViaHttpRangeAsync(Guid textureId, int desiredDiscard, Uri? capUri, int maxRetries = 0, Uri? fetchUrl = null)
    {
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                var url = fetchUrl ?? new Uri($"{capUri}?texture_id={textureId}");
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (desiredDiscard > 0)
                {
                    int byteLimit = J2kByteSizeEstimator.CalcDataSizeJ2C(0, 0, desiredDiscard);
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, byteLimit - 1);
                }

                HttpResponseMessage? response = null;
                byte[]? bytes = null;
                long? declaredLength = null;
                await _textureFetchSemaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    response = await _textureHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        // Forbidden is deliberately NOT retryable, unlike ServiceUnavailable and
                        // NotFound. A 403 is a permission decision the server already made -- no
                        // amount of retrying changes it, and retrying it anyway (5 attempts, 2s
                        // apart) is exactly what turned one denied texture into repeated "Failed
                        // to fetch texture ... Forbidden" log spam and real, wasted load against
                        // the sim. 503/404 stay retryable: a 503 can be genuinely transient, and a
                        // 404 can be an asset that was just created and has not propagated yet.
                        bool retryable = response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable
                                      || response.StatusCode == System.Net.HttpStatusCode.NotFound;
                        if (attempt < maxRetries && retryable)
                        {
                            // Will retry. Release semaphore and delay.
                        }
                        else
                        {
                            // A 403/401 from the generic cap is permanent -- remember it so the
                            // caller skips the UDP fallback (see _permanentlyDeniedTextures). The
                            // bake path (fetchUrl != null) is excluded: it 403s by design and then
                            // legitimately falls back to the generic cap (BUG-AVATAR-02).
                            if (fetchUrl == null &&
                                (response.StatusCode == System.Net.HttpStatusCode.Forbidden
                                 || response.StatusCode == System.Net.HttpStatusCode.Unauthorized))
                                _httpDeniedTextures.TryAdd(textureId, 0);
                            // Host in the message: a 403 could be the wrong URL class (see
                            // BUG-AVATAR-02 -- bakes needed bake-texture.glb..., not the generic
                            // asset CDN) or a genuine permission denial / non-persisted local
                            // texture. The host is the first thing that tells those apart.
                            return FetchFailed(textureId, $"HTTP {(int)response.StatusCode} from {url.Host}{url.AbsolutePath}");
                        }
                    }
                    else
                    {
                        declaredLength = response.Content.Headers.ContentLength;
                        bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    }
                }
                finally
                {
                    response?.Dispose();
                    _textureFetchSemaphore.Release();
                }

                if (bytes != null)
                {
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
            }
            catch (Exception ex)
            {
                if (attempt == maxRetries) return FetchFailed(textureId, $"Exception: {ex.Message}");
            }
            if (attempt < maxRetries) await Task.Delay(2000).ConfigureAwait(false);
        }
        return null;
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
        _parcelEnvironmentPollCts.Cancel();
        _parcelEnvironmentPollCts.Dispose();
        try { _wearableRebakeCts?.Cancel(); _wearableRebakeCts?.Dispose(); } catch { }
        _client.Self.ChatFromSimulator -= OnChatFromSimulator;
        _client.Objects.ObjectUpdate -= OnObjectUpdate;
        _client.Objects.TerseObjectUpdate -= OnTerseObjectUpdate;
        _client.Objects.ObjectPropertiesFamily -= OnObjectPropertiesFamily;
        _client.Objects.ObjectProperties -= OnObjectPropertiesFull;
        _client.Objects.PhysicsProperties -= OnPhysicsProperties;
        _client.Avatars.UUIDNameReply -= OnUUIDNameReply;
        _client.Avatars.DisplayNameUpdate -= OnDisplayNameUpdate;
        _client.Groups.GroupNamesReply -= OnGroupNamesReply;
        _client.Groups.CurrentGroups -= OnCurrentGroups;
        _client.Self.GroupChatJoined -= OnGroupChatJoined;
        _client.Self.AlertMessage -= OnAlertMessage;
        _client.Objects.KillObject -= OnKillObject;
        _client.Objects.KillObjects -= OnKillObjects;
        _client.Terrain.LandPatchReceived -= OnLandPatchReceived;
        _client.Network.SimConnected -= OnSimConnected;
        _client.Network.SimDisconnected -= OnSimDisconnected;
        _client.Network.SimChanged -= OnSimChanged;
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
        _client.Grid.CoarseLocationUpdate -= OnCoarseLocationUpdate;
        _client.Grid.GridRegion -= OnGridRegion;
        _client.Network.UnregisterCallback(PacketType.ObjectUpdate, OnRawObjectUpdatePacket);
        Logout();
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
