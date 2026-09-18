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

// GridSession, Movement part.
//
// Self-movement, sitting and standing, typing, and animation start/stop plus the
// animation-name table.
//
// Split out of the single 10,042-line GridSession.cs -- a pure move, no member
// changed. The class is still one type; `partial` only spreads it over files that
// can be read, reviewed and merged independently.
public sealed partial class GridSession
{
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
    public bool Stand()
    {
        var sim = _client.Network.CurrentSim;
        if (sim != null && _client.Self.SittingOn != 0)
        {
            var seatLocalId = _client.Self.SittingOn;
            var candidateSourceIds = new HashSet<Guid>();
            if (sim.ObjectsPrimitives.TryGetValue(seatLocalId, out var seatPrim) && seatPrim != null)
            {
                candidateSourceIds.Add(seatPrim.ID.Guid);
                uint rootId = seatPrim.ParentID == 0 ? seatPrim.LocalID : seatPrim.ParentID;
                if (rootId != seatPrim.LocalID && sim.ObjectsPrimitives.TryGetValue(rootId, out var root) && root != null)
                {
                    candidateSourceIds.Add(root.ID.Guid);
                }
                foreach (var child in sim.ObjectsPrimitives.Values)
                {
                    if (child != null && (child.ParentID == rootId || child.ParentID == seatLocalId))
                    {
                        candidateSourceIds.Add(child.ID.Guid);
                    }
                }
            }
            StopMotionsFromSources(candidateSourceIds);
        }
        return _client.Self.Stand();
    }

    public void StopAnimation(Guid animId) => _client.Self.AnimationStop(new UUID(animId), true);

    public void StartAnimation(Guid animId) => _client.Self.AnimationStart(new UUID(animId), true);

    /// <summary>
    /// FEAT-ANIM-10: When enabled, the avatar's head turns to look toward the camera's
    /// direction in third person for other observers. When disabled (default), the head stays aligned with
    /// the body (neutral forward gaze for portraits).
    /// </summary>
    public bool HeadFollowsCamera { get; set; } = false;

    /// <summary>FEAT-UI-30: hide the local agent's group title from EVERYONE, not just locally.
    /// Mirrors the reference viewer's <c>RenderHideGroupTitle</c> (default off, "Don't show my
    /// group title in my name label"), which reaches the grid as <c>AU_FLAGS_HIDETITLE</c> on the
    /// AgentUpdate packet -- the simulator then leaves the Title NameValue off the broadcast.
    /// Distinct from the purely local nametag toggles in UiSettings: this one changes what other
    /// people's viewers receive.</summary>
    public bool HideOwnGroupTitle { get; set; }

    private bool _playTypingAnimation = false;

    /// <summary>
    /// FEAT-ANIM-10: When enabled, typing in local chat plays ANIM_AGENT_TYPE and sends
    /// ChatType.StartTyping/StopTyping indicators to the simulator. Default off.
    /// </summary>
    public bool PlayTypingAnimation
    {
        get => _playTypingAnimation;
        set
        {
            _playTypingAnimation = value;
            if (!value && _isTyping)
            {
                StopTyping();
            }
        }
    }

    private bool _isTyping;

    /// <summary>
    /// FEAT-ANIM-10: Starts the typing animation and broadcasts the typing indicator if enabled.
    /// </summary>
    public void StartTyping()
    {
        if (!PlayTypingAnimation || !_client.Network.Connected) return;
        if (_isTyping) return;
        _isTyping = true;
        _client.Self.AnimationStart(Animations.TYPE, true);
        _client.Self.Chat(string.Empty, 0, ChatType.StartTyping);
    }

    /// <summary>
    /// FEAT-ANIM-10: Stops the typing animation and clears the typing indicator.
    /// </summary>
    public void StopTyping()
    {
        if (!_isTyping) return;
        _isTyping = false;
        if (_client.Network.Connected)
        {
            _client.Self.AnimationStop(Animations.TYPE, true);
            _client.Self.Chat(string.Empty, 0, ChatType.StopTyping);
        }
    }

    /// <summary>
    /// Stops all animations on the self avatar that were triggered by any of the given source object IDs
    /// (e.g. when an attachment or seat object is detached or stood up from), matching Linden Lab's
    /// LLVOAvatarSelf::stopMotionFromSource.
    /// </summary>
    public List<Guid> StopMotionsFromSources(IEnumerable<Guid> sourceIds)
    {
        var sourceSet = sourceIds is HashSet<Guid> set ? set : new HashSet<Guid>(sourceIds);
        sourceSet.Remove(Guid.Empty);
        if (sourceSet.Count == 0) return new List<Guid>();

        var stopped = new List<Guid>();
        lock (_selfAnimationSources)
        {
            foreach (var (animId, srcId) in _selfAnimationSources)
            {
                if (sourceSet.Contains(srcId))
                {
                    stopped.Add(animId);
                }
            }
            foreach (var animId in stopped)
            {
                _selfAnimationSources.Remove(animId);
            }
        }

        if (stopped.Count > 0)
        {
            foreach (var animId in stopped)
            {
                _client.Self.AnimationStop(new UUID(animId), true);
            }
            Console.Error.WriteLine($"[GridSession] StopMotionsFromSources: stopped {stopped.Count} animation(s) from {sourceSet.Count} source(s)");
        }

        return stopped;
    }

    /// <summary>
    /// FEAT-AVATAR-02 / FEAT-ANIM-04: Stops all animations currently playing on the self avatar
    /// by sending an AnimationStop request to the simulator for each signalled animation.
    /// </summary>
    public void StopAllSelfAnimations()
    {
        if (!_client.Network.Connected) return;

        var animIds = new List<UUID>(_client.Self.SignaledAnimations.Keys);
        foreach (var id in animIds)
        {
            _client.Self.AnimationStop(id, true);
        }
    }

    /// <summary>Sends an AgentUpdate to move the avatar.</summary>
    /// <param name="bodyRotation">The avatar body-facing orientation (yaw-only, SL coordinates).</param>
    /// <param name="cameraRotation">The render camera's full orientation in SL coordinates. Used for
    /// HeadRotation when <see cref="HeadFollowsCamera"/> is true.</param>
    /// <param name="cameraPosition">The RENDER camera's region-local position (System.Numerics,
    /// SL Z-up axes), or null to keep anchoring the interest camera on the avatar's facing. The
    /// sim centres its interest list on <c>CameraCenter</c>, so without this it streams objects
    /// around LibreMetaverse's default region-centre camera (128,128,20), not where the user is
    /// looking -- BUG-NET-01.</param>
    /// <param name="cameraForward">The render camera's forward direction (region-local, SL axes);
    /// only used when <paramref name="cameraPosition"/> is supplied.</param>
    /// <param name="cameraFar">Interest / draw distance in metres; ignored when &lt;= 0.</param>
    /// <param name="fast">True when running (double-tap forward, Shift held, or Always Run mode).</param>
    public void SetMovement(bool forward, bool backward, bool left, bool right, bool up, bool down,
        System.Numerics.Quaternion bodyRotation,
        System.Numerics.Quaternion? cameraRotation = null,
        bool fly = false,
        System.Numerics.Vector3? cameraPosition = null,
        System.Numerics.Vector3? cameraForward = null,
        float cameraFar = 0f,
        bool fast = false)
    {
        if (!_client.Network.Connected) return;

        // Map Godot/SLNG axes to LibreMetaverse (which uses OpenSim/SL axes: X forward, Y left, Z up)
        var slBodyQuat = new LibreMetaverse.Quaternion(bodyRotation.X, bodyRotation.Y, bodyRotation.Z, bodyRotation.W);
        var slCameraQuat = cameraRotation.HasValue
            ? new LibreMetaverse.Quaternion(cameraRotation.Value.X, cameraRotation.Value.Y, cameraRotation.Value.Z, cameraRotation.Value.W)
            : slBodyQuat;

        // Interest camera. With a real render-camera pose, anchor CameraCenter there (BUG-NET-01);
        // otherwise fall back to the pre-existing "look along body facing from wherever the camera
        // already is" behaviour.
        if (cameraPosition is { } camPos)
        {
            var slPos = new LibreMetaverse.Vector3(camPos.X, camPos.Y, camPos.Z);
            var slFwd = cameraForward is { } f
                ? new LibreMetaverse.Vector3(f.X, f.Y, f.Z)
                : LibreMetaverse.Vector3.UnitX * slCameraQuat;
            _client.Self.Movement.Camera.LookAt(slPos, slPos + slFwd);
        }
        else
        {
            _client.Self.Movement.Camera.LookDirection(LibreMetaverse.Vector3.UnitX * (HeadFollowsCamera ? slCameraQuat : slBodyQuat));
        }

        if (cameraFar > 0f)
            _client.Self.Movement.Camera.Far = cameraFar;

        // FEAT-ANIM-10: BodyRotation is always the avatar's body facing; HeadRotation follows
        // the camera rotation if HeadFollowsCamera is enabled, or stays aligned with the body if disabled.
        // FEAT-UI-30: hiding your own group title is NOT a local display setting -- it is a bit
        // in the AgentUpdate packet (AU_FLAGS_HIDETITLE, llviewermessage.cpp's send_agent_update
        // from gAgent.isGroupTitleHidden(), fed by the stock RenderHideGroupTitle setting). The
        // SIMULATOR then stops broadcasting the title, so it disappears for everyone, not just
        // for you. Set on every update because Movement.Flags travels with the packet.
        _client.Self.Movement.Flags = HideOwnGroupTitle
            ? LibreMetaverse.AgentFlags.HideTitle
            : LibreMetaverse.AgentFlags.None;

        _client.Self.Movement.BodyRotation = slBodyQuat;
        _client.Self.Movement.HeadRotation = HeadFollowsCamera ? slCameraQuat : slBodyQuat;

        _client.Self.Movement.AtPos = forward;
        _client.Self.Movement.AtNeg = backward;
        _client.Self.Movement.FastAt = fast;
        _client.Self.Movement.LeftPos = left;
        _client.Self.Movement.LeftNeg = right;
        _client.Self.Movement.UpPos = up;
        _client.Self.Movement.UpNeg = down;
        _client.Self.Movement.Fly = fly;

        if (_client.Self.Movement.AlwaysRun != fast)
        {
            _client.Self.Movement.AlwaysRun = fast;
        }

        // Send the update to the server
        _client.Self.Movement.SendUpdate(false);
    }

    private static readonly Lazy<IReadOnlyDictionary<LibreMetaverse.UUID, string>> _builtinAnimNames = new(() => LibreMetaverse.Animations.ToDictionary());

    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> _knownAnimNames = new();

    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _unknownAnimIds = new();

    public void RegisterAnimationName(Guid assetId, string name)
    {
        if (!string.IsNullOrEmpty(name))
        {
            _knownAnimNames[assetId] = name;
        }
    }

    public string? ResolveAnimationName(Guid id)
    {
        if (_knownAnimNames.TryGetValue(id, out var known))
            return known;

        if (_unknownAnimIds.ContainsKey(id))
            return null;

        var uuid = new LibreMetaverse.UUID(id);
        if (_builtinAnimNames.Value.TryGetValue(uuid, out var builtinName))
        {
            _knownAnimNames[id] = builtinName;
            return builtinName;
        }

        // Try lookup in inventory store by indexing nodes once
        if (!_inventoryStoreIndexed && _client.Inventory?.Store?.RootNode != null)
        {
            _inventoryStoreIndexed = true;
            var stack = new Stack<LibreMetaverse.InventoryNode>();
            stack.Push(_client.Inventory.Store.RootNode);
            if (_client.Inventory.Store.LibraryRootNode != null)
                stack.Push(_client.Inventory.Store.LibraryRootNode);

            while (stack.Count > 0)
            {
                var node = stack.Pop();
                if (node.Data is LibreMetaverse.InventoryItem item)
                {
                    if (item.AssetUUID != LibreMetaverse.UUID.Zero)
                    {
                        _knownAnimNames[item.AssetUUID.Guid] = item.Name;
                    }
                    _knownAnimNames[item.UUID.Guid] = item.Name;
                }

                try
                {
                    foreach (var child in node.Nodes.Values) stack.Push(child);
                }
                catch (InvalidOperationException) { }
            }

            if (_knownAnimNames.TryGetValue(id, out var foundAfterIndex))
                return foundAfterIndex;
        }

        _unknownAnimIds[id] = 1;
        return null;
    }
}
