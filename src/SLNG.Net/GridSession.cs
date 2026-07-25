using System.Collections.Concurrent;
using System.Net.Http;
using LibreMetaverse;
using LibreMetaverse.Packets;
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
    private readonly GridClient _client;

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

    public event EventHandler<ChatMessageEvent>? ChatMessageReceived;
    public event EventHandler<ObjectUpdateEvent>? ObjectUpdateReceived;
    public event EventHandler<AvatarUpdateEvent>? AvatarUpdateReceived;
    public event EventHandler<ObjectRemovedEvent>? ObjectRemovedReceived;
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
        _client.Settings.Agent.SendAppearance = true;

        // Use the HTTP GetTexture CAP instead of the legacy UDP image transfer. UDP transfers
        // time out and hand back truncated JPEG2000 streams on busy grids (the "Tile part
        // length inconsistent" decode failures / white untextured objects); HTTP is reliable.
        _client.Settings.TexturePipeline.Enabled = true;
        _client.Settings.TexturePipeline.UseHttpTextures = true;
        _client.Self.ChatFromSimulator += OnChatFromSimulator;
        _client.Objects.ObjectUpdate += OnObjectUpdate;
        _client.Objects.TerseObjectUpdate += OnTerseObjectUpdate;
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
        _client.Avatars.AvatarAppearance += OnAvatarAppearance;
        _client.Avatars.AvatarAnimation += OnAvatarAnimation;
        _client.Appearance.AppearanceSet += OnAppearanceSet;
        _client.Friends.FriendOnline += OnFriendOnline;
        _client.Friends.FriendOffline += OnFriendOffline;
        _client.Self.IM += OnInstantMessage;

        // Coexists with ObjectManager's own internal ObjectUpdate handler (packet callbacks are
        // multicast) -- see _lightPresentByLocalId for why this is needed.
        _client.Network.RegisterCallback(PacketType.ObjectUpdate, OnRawObjectUpdatePacket);
    }

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

    private void OnChatFromSimulator(object? sender, ChatEventArgs e)
    {
        ChatMessageReceived?.Invoke(this, new ChatMessageEvent(
            e.FromName,
            e.Message,
            (byte)e.Type));
    }

    private void OnAvatarUpdate(object? sender, AvatarUpdateEventArgs e)
    {
        bool isLocalAgent = e.Avatar.ID == _client.Self.AgentID;
        AvatarUpdateReceived?.Invoke(this, new AvatarUpdateEvent(
            e.Simulator.Handle,
            e.Avatar.LocalID,
            e.Avatar.ID.Guid,
            new System.Numerics.Vector3(e.Avatar.Position.X, e.Avatar.Position.Y, e.Avatar.Position.Z),
            new System.Numerics.Quaternion(e.Avatar.Rotation.X, e.Avatar.Rotation.Y, e.Avatar.Rotation.Z, e.Avatar.Rotation.W),
            e.Avatar.FirstName,
            e.Avatar.LastName,
            isLocalAgent,
            e.Avatar.Scale.Z,
            new System.Numerics.Vector3(e.Avatar.Velocity.X, e.Avatar.Velocity.Y, e.Avatar.Velocity.Z),
            e.TimeDilation / 65535.0f));
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

    private void OnAvatarAppearance(object? sender, AvatarAppearanceEventArgs e)
    {
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
    private void OnAppearanceSet(object? sender, AppearanceSetEventArgs e)
    {
        if (!e.Success) return;

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
            AvatarUpdateReceived?.Invoke(this, new AvatarUpdateEvent(
                e.Simulator.Handle,
                e.Prim.LocalID,
                agentId,
                new System.Numerics.Vector3(e.Update.Position.X, e.Update.Position.Y, e.Update.Position.Z),
                new System.Numerics.Quaternion(e.Update.Rotation.X, e.Update.Rotation.Y, e.Update.Rotation.Z, e.Update.Rotation.W),
                firstName,
                lastName,
                isLocalAgent,
                e.Prim.Scale.Z,
                new System.Numerics.Vector3(e.Update.Velocity.X, e.Update.Velocity.Y, e.Update.Velocity.Z),
                e.TimeDilation / 65535.0f));
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
            else if (prim.Sculpt.Type != LibreMetaverse.SculptType.None)
            {
                // Sphere/Torus/Plane/Cylinder sculpt: geometry comes from the sculpt-map texture.
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
        System.Numerics.Vector4 colorTint = new System.Numerics.Vector4(1, 1, 1, 1);

        var defaultFace = prim.Textures?.DefaultTexture;
        if (defaultFace != null)
        {
            textureId = defaultFace.TextureID.Guid;
            renderMaterialId = defaultFace.RenderMaterialID.Guid;
            colorTint = new System.Numerics.Vector4(defaultFace.RGBA.R, defaultFace.RGBA.G, defaultFace.RGBA.B, defaultFace.RGBA.A);
        }

        // Per-face textures: each prim face can have its own texture/colour. Resolve each face
        // (its own entry, or the default) to a neutral FaceTexture indexed by face number.
        FaceTexture[]? faces = null;
        var faceArr = prim.Textures?.FaceTextures;
        if (faceArr != null && faceArr.Length > 0 && defaultFace != null)
        {
            faces = new FaceTexture[faceArr.Length];
            for (int i = 0; i < faceArr.Length; i++)
            {
                var f = faceArr[i] ?? defaultFace;
                faces[i] = new FaceTexture(
                    f.TextureID.Guid,
                    f.RenderMaterialID.Guid,
                    new System.Numerics.Vector4(f.RGBA.R, f.RGBA.G, f.RGBA.B, f.RGBA.A),
                    f.RepeatU,
                    f.RepeatV,
                    f.OffsetU,
                    f.OffsetV,
                    f.Rotation);
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
            timeDilation));
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
            IsLocalAgent: true));
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
    public Task DetachItemAsync(Guid itemId)
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

        // Query active attachments from AppearanceManager ONLY for matching candidate UUIDs
        try
        {
            var activeAtts = _client.Appearance.GetAttachmentsByItemId();
            foreach (var kvp in activeAtts)
            {
                if (uuidsToDetach.Contains(kvp.Key))
                {
                    uuidsToDetach.Add(kvp.Key);
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
                    foreach (var k in staleKeys)
                    {
                        cofNode.Nodes.Remove(k);
                    }
                }
            }
        }
        catch { }

        return Task.CompletedTask;
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
    public void SetMovement(bool forward, bool backward, bool left, bool right, bool up, bool down, System.Numerics.Quaternion cameraRotation, bool fly = false)
    {
        if (!_client.Network.Connected) return;

        // Map Godot/SLNG axes to LibreMetaverse (which uses OpenSim/SL axes: X forward, Y left, Z up)
        // For LibreMetaverse, we just pass the rotation directly.
        var slQuat = new LibreMetaverse.Quaternion(cameraRotation.X, cameraRotation.Y, cameraRotation.Z, cameraRotation.W);

        // Update the agent's movement state
        _client.Self.Movement.Camera.LookDirection(LibreMetaverse.Vector3.UnitX * slQuat);
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
    public async Task<byte[]?> FetchTextureDataAsync(Guid textureId, int desiredDiscard = 0)
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
        var capUri = _client.Network.CurrentSim?.Caps?.GetTextureCapURI();
        if (capUri != null)
        {
            var httpResult = await FetchTextureViaHttpRangeAsync(textureId, desiredDiscard, capUri).ConfigureAwait(false);
            if (httpResult != null) return httpResult;
            // Falls through to the UDP path below on any HTTP failure (network error,
            // non-success status) -- never a hard failure just because HTTP didn't pan out.
        }

        var tcs = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var pipeline = typeof(AssetManager).GetField("Texture", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(_client.Assets);
            if (pipeline != null)
            {
                var reqMethod = pipeline.GetType().GetMethod("RequestTexture", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (reqMethod != null)
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
                    return await tcs.Task;
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

        return await fallbackTcs.Task;
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
            if (!response.IsSuccessStatusCode) return null; // 4xx/5xx -- let the UDP fallback try

            long? declaredLength = response.Content.Headers.ContentLength;
            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (bytes.Length == 0) return null;

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
            if (declaredLength.HasValue && bytes.Length < declaredLength.Value) return null;

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
                return null;
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
        _client.Network.UnregisterCallback(PacketType.ObjectUpdate, OnRawObjectUpdatePacket);
        Logout();
    }
}
