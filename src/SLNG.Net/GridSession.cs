using System.Collections.Concurrent;
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
    public event EventHandler<AlertMessageEvent>? AlertMessageReceived;
    public event EventHandler<TerrainPatchEvent>? TerrainPatchReceived;
    public event EventHandler<TerrainSettingsEvent>? TerrainSettingsReceived;
    public event EventHandler<RegionDisconnectedEvent>? RegionDisconnectedReceived;
    public event EventHandler<AvatarAppearanceEvent>? AvatarAppearanceReceived;
    public event EventHandler<AvatarAnimationEvent>? AvatarAnimationReceived;
    public event EventHandler<FriendStatusEvent>? FriendStatusChanged;

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
        LibreMetaverse.Settings.LogLevel = Microsoft.Extensions.Logging.LogLevel.Warning;

        _client = new GridClient();
        _client.Settings.Agent.SendAppearance = false;

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
        _client.Groups.GroupNamesReply += OnGroupNamesReply;
        _client.Self.AlertMessage += OnAlertMessage;
        _client.Objects.KillObject += OnKillObject;
        _client.Objects.KillObjects += OnKillObjects;
        _client.Terrain.LandPatchReceived += OnLandPatchReceived;
        _client.Network.SimConnected += OnSimConnected;
        _client.Network.SimDisconnected += OnSimDisconnected;
        _client.Avatars.AvatarAppearance += OnAvatarAppearance;
        _client.Avatars.AvatarAnimation += OnAvatarAnimation;
        _client.Friends.FriendOnline += OnFriendOnline;
        _client.Friends.FriendOffline += OnFriendOffline;

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
            isLocalAgent));
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
        foreach (var kvp in e.Names)
        {
            var id = kvp.Key.Guid;
            _nameCache[id] = kvp.Value;
            NameResolved?.Invoke(this, new NameResolvedEvent(id, kvp.Value));
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

        AvatarAppearanceReceived?.Invoke(this, new AvatarAppearanceEvent(
            e.Simulator.Handle,
            e.AvatarID.Guid,
            e.VisualParams?.ToArray() ?? Array.Empty<byte>(),
            textures
        ));
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
        if (e.Update.Avatar) return; // avatar terse updates: not covered by this event pipeline
        RaiseObjectUpdate(e.Simulator, e.Prim, isFullUpdate: false);
    }

    private void RaiseObjectUpdate(LibreMetaverse.Simulator simulator, Primitive prim, bool isFullUpdate)
    {
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
                sculptType = (byte)prim.Sculpt.Type;
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
        var pd = prim.PrimData;
        var shape = new PrimShape(
            (byte)pd.ProfileCurve,
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
            new System.Numerics.Vector3(prim.Position.X, prim.Position.Y, prim.Position.Z),
            new System.Numerics.Quaternion(prim.Rotation.X, prim.Rotation.Y, prim.Rotation.Z, prim.Rotation.W),
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
            isFullUpdate));
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

    /// <summary>
    /// Attempts to log in to the grid described by <paramref name="credentials"/>.
    /// Uses LibreMetaverse's async login API; failures (including unreachable grids)
    /// are returned as a failed <see cref="LoginResult"/> rather than thrown.
    /// </summary>
    public async Task<LoginResult> LoginAsync(LoginCredentials credentials, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);

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
                    result.Add(new InventoryEntry(
                        i.UUID.Guid, i.ParentUUID.Guid, i.OwnerID.Guid, i.Name,
                        IsFolder: false, PreferredFolderType: -1,
                        // For links the "asset" id actually points at the linked inventory item —
                        // report it as the link target and leave AssetId empty (resolving the
                        // target's real asset takes a second fetch the UI doesn't need yet).
                        AssetId: i.ResolvedAssetID.Guid,
                        (int)i.AssetType, (int)i.InventoryType,
                        i.IsLink(), i.IsLink() ? i.ResolvedItemID.Guid : Guid.Empty,
                        owned.HasFlag(LibreMetaverse.PermissionMask.Copy),
                        owned.HasFlag(LibreMetaverse.PermissionMask.Modify),
                        owned.HasFlag(LibreMetaverse.PermissionMask.Transfer)));
                    break;
            }
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
    /// Copies an inventory item to a new parent folder.
    /// </summary>
    public async Task CopyItemAsync(Guid itemId, Guid newParentId, string newName)
    {
        var itemUuid = new LibreMetaverse.UUID(itemId);
        var parentUuid = new LibreMetaverse.UUID(newParentId);
        
        await _client.Inventory.RequestCopyItemAsync(itemUuid, parentUuid, newName, CancellationToken.None).ConfigureAwait(false);
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

    /// <summary>
    /// Fetches the raw bytes of a texture asset (JPEG2000) from the simulator. Returns null
    /// if the fetch times out or fails.
    /// </summary>
    public Task<byte[]?> FetchTextureDataAsync(Guid textureId)
    {
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
                    // packetStart is UInt32 — passing int 0 makes Invoke throw "Int32 cannot be
                    // converted to UInt32", which silently fell back to the truncating RequestImageAsync.
                    reqMethod.Invoke(pipeline, new object[] { new UUID(textureId), ImageType.Normal, 100000.0f, 0, 0u, delegateObj, false });
                    return tcs.Task;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GridSession] TexturePipeline reflection failed: {ex.Message}");
        }

        // Fallback if reflection fails
        _client.Assets.RequestImageAsync(new UUID(textureId), ImageType.Normal, CancellationToken.None)
            .ContinueWith(t =>
            {
                var data = t.Result?.AssetData;
                tcs.TrySetResult(data is { Length: > 0 } ? data : null);
            });

        return tcs.Task;
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
        _client.Groups.GroupNamesReply -= OnGroupNamesReply;
        _client.Self.AlertMessage -= OnAlertMessage;
        _client.Objects.KillObject -= OnKillObject;
        _client.Objects.KillObjects -= OnKillObjects;
        _client.Terrain.LandPatchReceived -= OnLandPatchReceived;
        _client.Network.SimConnected -= OnSimConnected;
        _client.Network.SimDisconnected -= OnSimDisconnected;
        _client.Friends.FriendOnline -= OnFriendOnline;
        _client.Friends.FriendOffline -= OnFriendOffline;
        _client.Network.UnregisterCallback(PacketType.ObjectUpdate, OnRawObjectUpdatePacket);
        Logout();
    }
}
