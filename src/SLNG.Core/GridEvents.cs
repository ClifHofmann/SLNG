using System.Numerics;
using SLNG.Core.Components;

namespace SLNG.Core;

/// <summary>
/// Marker for events that mutate world state. Implementing this lets them be queued
/// and applied to the <see cref="ECS.World"/> on a single thread by
/// <see cref="WorldSimulation"/>.
/// </summary>
public interface IWorldEvent
{
}

/// <summary>
/// Represents a local chat message received from the simulator. Not a world-state
/// mutation, so it is intentionally not an <see cref="IWorldEvent"/>.
/// </summary>
/// <param name="SourceId">UUID of the speaker — an agent or a scripted object — or
/// <see cref="Guid.Empty"/> for system lines. Trailing/optional so existing call sites and
/// tests are unaffected.</param>
/// <param name="FromAgent">True when the sim tagged the source as another avatar (as opposed to
/// an object or the system). Lets the UI turn a real resident's name into a profile link
/// (FEAT-UI-13) without mis-linking object chat.</param>
public record ChatMessageEvent(string FromName, string Message, byte ChatType, Guid SourceId = default, bool FromAgent = false);

/// <summary>Resolves a user or group UUID (Creator, Owner, Group, ...) to a display name.
/// Identity-cache data, not world simulation state, so intentionally not an
/// <see cref="IWorldEvent"/> -- consumers (UI) subscribe directly on GridSession.</summary>
public record NameResolvedEvent(Guid Id, string Name) : IWorldEvent;

/// <summary>The simulator's urgent-message channel -- e.g. "Object physics cancelled because
/// it exceeds limits for physical prims" when an ObjectFlagUpdate is silently rejected server-
/// side. Not world-state, so intentionally not an <see cref="IWorldEvent"/>.</summary>
public record AlertMessageEvent(string Message);

/// <summary>Stage of the login handshake, mirrored (by name) from LibreMetaverse's own
/// <c>LoginStatus</c> enum so the real, server-driven handshake progress can be surfaced to the
/// UI without an <c>OpenMetaverse</c>/<c>LibreMetaverse</c> type crossing the <c>SLNG.Net</c>
/// boundary (see AGENTS.md's layering rules). Deliberately does not include <c>None</c> --
/// callers only ever observe stages once a login attempt is actually in flight.</summary>
public enum LoginStage
{
    ConnectingToLogin,
    ReadingResponse,
    Redirecting,
    ConnectingToSim,
    Success,
    Failed,
}

/// <summary>A real, server-driven login handshake progress notification -- relayed 1:1 from
/// LibreMetaverse's <c>NetworkManager.LoginProgress</c> event, not simulated/time-based. Not
/// world-state, so intentionally not an <see cref="IWorldEvent"/>.</summary>
public record LoginProgressEvent(LoginStage Stage, string Message);

/// <summary>A friend's online/offline presence changed. Identity/social state, not world
/// simulation state, so intentionally not an <see cref="IWorldEvent"/> -- consumers (UI)
/// subscribe directly on GridSession, same as <see cref="NameResolvedEvent"/>.</summary>
public record FriendStatusEvent(Guid FriendId, bool IsOnline);

/// <summary>A 1:1 instant message from another avatar (LibreMetaverse's
/// <c>InstantMessageDialog.MessageFromAgent</c> only -- friendship offers, teleport requests,
/// group notices etc. ride the same wire event but aren't modeled here yet). Not world-state, so
/// intentionally not an <see cref="IWorldEvent"/>.</summary>
public record InstantMessageEvent(Guid FromAgentId, string FromAgentName, string Message, Guid SessionId);

/// <summary>The agent's group membership list arrived (or was refreshed). Identity/social state,
/// not world simulation state, so intentionally not an <see cref="IWorldEvent"/>.</summary>
public record GroupsUpdatedEvent(IReadOnlyList<GroupEntry> Groups);

/// <summary>One message in a group chat session.
///
/// Group chat does NOT arrive as <c>InstantMessageDialog.MessageFromAgent</c> — it comes in as
/// <c>SessionSend</c>, and its <c>GroupIM</c> flag is not always set on the wire (a message from
/// an already-open session carries only the session id). LibreMetaverse's own
/// <c>AgentManager.IsGroupMessage</c> is the authoritative test and is what
/// <c>GridSession.OnInstantMessage</c> uses, rather than inspecting the dialog by hand.</summary>
/// <param name="GroupId">Group UUID — the same value as the chat session id.</param>
/// <param name="FromAgentId">Speaker's agent id; <see cref="Guid.Empty"/> for a system line.</param>
public record GroupChatMessageEvent(Guid GroupId, Guid FromAgentId, string FromAgentName, string Message);

/// <summary>Result of joining a group's chat session. A join must succeed before
/// <c>GridSession.SendGroupMessage</c> can deliver anything to that group.</summary>
public record GroupChatJoinedEvent(Guid GroupId, string SessionName, bool Success);

/// <summary>Somebody invited the agent to a group. Answer with
/// <c>GridSession.RespondToGroupInvitation</c>; until then nothing is sent, which is what the
/// real viewer does while its "Join group?" notification sits on screen.</summary>
/// <param name="GroupId">The group to join — and the address the accept/decline reply is sent to.
/// The invite carries it in the message's <c>FromAgentID</c> field, not a group field
/// (llimprocessing.cpp:864 <c>group_id = from_group ? from_id : aux_id</c>; LibreMetaverse
/// exposes no aux id, so an invitation not sent by the group itself is out of reach).</param>
/// <param name="SessionId">The invite's IM session id — the viewer's <c>transaction_id</c>, which
/// the reply must echo back or the server cannot match it to the invitation.</param>
/// <param name="FromName">Who/what sent the invitation, for display.</param>
/// <param name="Message">Server-composed text naming the group, its charter and the fee.</param>
/// <param name="MembershipFee">L$ charged on joining, decoded from the invitation's binary
/// bucket, or 0 when the bucket is absent/malformed.</param>
public record GroupInvitationEvent(Guid GroupId, Guid SessionId, string FromName, string Message, int MembershipFee);

/// <summary>Represents a spatial update for a simulator object or avatar.</summary>
/// <param name="ParentLocalId">Local ID of the parent object, or 0 if unparented.</param>
/// <param name="AttachmentPoint">SL AttachmentPoint enum byte value; non-zero when the object
/// is worn on an avatar (i.e. ParentLocalId is an avatar's LocalId).</param>
public record ObjectUpdateEvent(
    ulong RegionHandle, uint LocalId,
    Vector3 Position, Quaternion Rotation, Vector3 Scale,
    byte ProfileCurve, bool IsMesh, Guid MeshId, Guid TextureId, Guid RenderMaterialId, Vector4 ColorTint,
    float RepeatU = 1.0f, float RepeatV = 1.0f, float OffsetU = 0.0f, float OffsetV = 0.0f, float TextureRotation = 0.0f,
    uint ParentLocalId = 0, byte AttachmentPoint = 0,
    PrimShape Shape = default,
    bool IsSculpt = false, Guid SculptId = default, byte SculptType = 0,
    FaceTexture[]? Faces = null,
    Guid ObjectId = default,
    bool IsPhysical = false, bool IsTemporary = false, bool IsPhantom = false, bool CastsShadows = true,
    // SL's point-light ("Light") prim property -- an ExtraParams block, not a PrimFlags bit.
    // Subject to the same terse-update staleness as the flags above (see IsFullUpdate).
    bool LightEnabled = false, Vector3 LightColor = default, float LightIntensity = 0f, float LightRadius = 0f, float LightFalloff = 0f,
    // Classic material (Stone/Metal/.../Rubber) -- read from the same PrimData block as
    // ProfileCurve/Shape above, so unlike the flags/light fields it's current on every update,
    // full or terse; NOT gated by IsFullUpdate.
    PrimMaterial Material = PrimMaterial.Wood,
    // LibreMetaverse ClickAction byte (0=Touch, 1=Sit, etc)
    byte ClickAction = 0,
    // ImprovedTerseObjectUpdate (fast position/rotation streaming for moving objects) never
    // carries flags on the wire -- LibreMetaverse leaves Primitive.Flags at whatever the last
    // full update said, which is stale the moment a flag was just changed locally. IsPhysical/
    // IsTemporary/IsPhantom/CastsShadows above are only trustworthy when this is true; a terse-
    // sourced event must not be allowed to overwrite them (see WorldSimulation.ApplyObjectUpdate).
    bool IsFullUpdate = true,
    // World-space velocity (m/s) and the originating sim's time dilation (0-1), same purpose as
    // AvatarUpdateEvent's identically-named fields: WorldSimulation.ExtrapolateMovement dead-
    // reckons a physically-moving object's (falling, rolling, pushed) Position between packets
    // from these, exactly like it already does for avatars.
    Vector3 Velocity = default, float TimeDilation = 1f,
    // The DEFAULT face's texgen. The per-face array in Faces carries its own, but a prim whose
    // faces are all identical sends no per-face entries at all, and without this such a prim
    // loses its texgen completely. Raw SL value -- see FaceTexture.TexGen.
    byte TexGen = 0,
    // llSetTextureAnim state (the ObjectUpdate TextureAnim block), or null if the object has
    // none. Like the flags above this only exists on a FULL update -- ImprovedTerseObjectUpdate
    // carries no TextureAnim block at all -- so it is applied under the same IsFullUpdate guard.
    TextureAnimation? TextureAnim = null,
    // The DEFAULT face's legacy Blinn-Phong material id (normal + specular map). Separate from
    // RenderMaterialId, which is the glTF PBR one -- a face carries both independently.
    Guid LegacyMaterialId = default,
    // Particle system parameters for rendering GPUParticles3D.
    ParticleSystemData? Particles = null,
    // The DEFAULT face's fullbright flag. Per-face entries in Faces carry their own; a prim
    // whose faces are all identical sends none, so this covers that case. See FaceTexture.Fullbright.
    bool Fullbright = false
) : IWorldEvent;

/// <summary>Represents an update for an avatar. <paramref name="ScaleZ"/> is DIAGNOSTIC ONLY
/// (2026-07-22, round 9): the avatar object's own wire-transmitted Scale.Z (LibreMetaverse:
/// Avatar/Primitive.Scale, decoded from the same ObjectUpdate/TerseObjectUpdate as everything
/// else — see linden_llvoavatar.cpp's getScale(), a quantity the real viewer treats as DISTINCT
/// from mBodySize.z/computeBodySize()'s output). Both avatars were independently measured
/// under-height by ~14-15cm vs. their real Firestorm-displayed height, ruling out a remote-
/// specific bug — logged here so a live session can check whether THIS simulator-tracked value
/// (analogous to llGetAgentSize()) matches Firestorm's number better than our own ComputeBodySize
/// port does, before trusting either as ground truth. Not yet used in any rendering/position
/// math. Defaults to 0f so existing call sites/tests keep compiling unchanged.</summary>
/// <param name="Velocity">Wire-transmitted world-space velocity (m/s), from the same ObjectUpdate/
/// TerseObjectUpdate as Position. Drives WorldSimulation.ExtrapolateMovement's dead-reckoning
/// between packets -- mirrors the real viewer's LLViewerObject::interpolateLinearMotion, which
/// extrapolates from the last reported velocity rather than holding position static (or fighting
/// it with client-side input prediction) until the next packet arrives.</param>
/// <param name="TimeDilation">The originating simulator's time dilation (0-1; 1 = running at full
/// real-time speed, lower under load -- LibreMetaverse's RegionData.TimeDilation, normalized from
/// its raw ushort wire form the same way ObjectManager.UpdateDilation does). A busy/laggy sim
/// (e.g. an OSGrid megaregion under load) can sit well below 1 while a quiet one stays near it.
/// Scales ExtrapolateMovement's per-frame dead-reckoning step, exactly like LibreMetaverse's own
/// InterpolationService (`adjSeconds = seconds * sim.Stats.Dilation`) -- without this, extrapolating
/// at full real-time speed on a dilated sim overshoots what the sim actually simulated, so the next
/// (correct, but now further away) packet reads as an extra correction pop on top of whatever
/// packet-rate judder already exists.</param>
/// <param name="SittingOnLocalId">MVP2-1: the seat prim's scene-local id if this avatar is sitting
/// on an object, 0 if standing/ground-sitting. Position/Rotation above are already resolved to
/// world space by GridSession (mirroring LibreMetaverse's own AgentManager.SimPosition/SimRotation
/// walk of the parent chain) — nothing downstream needs to special-case a seated avatar's
/// transform, only gate movement/ground-clamp on this flag.</param>
/// <summary>An avatar's position and state as the simulator reports it.
///
/// <paramref name="SupportPlane"/> is SL's collision plane for this avatar: xyz is the plane
/// normal in region space, w its constant, and the signed distance from the plane is
/// <c>dot(position, normal) - w</c>. The simulator computes it in Havok and ships it in the
/// avatar's own update — it is the server telling us what the avatar is standing on, including
/// prims, with no dependence on our own colliders having streamed in.
///
/// Null means the simulator has not sent one (it is also cleared across a region change), which
/// is NOT the same as "standing on nothing" and must not be treated as such.</summary>
public record AvatarUpdateEvent(ulong RegionHandle, uint LocalId, Guid AgentId, Vector3 Position, Quaternion Rotation, string FirstName, string LastName, bool IsLocalAgent, float ScaleZ = 0f, Vector3 Velocity = default, float TimeDilation = 1f, uint SittingOnLocalId = 0, Vector4? SupportPlane = null) : IWorldEvent;

/// <summary>Represents the removal of an object from the simulator's interest list.</summary>
public record ObjectRemovedEvent(ulong RegionHandle, uint LocalId) : IWorldEvent;

/// <summary>Physics collision shape and material response (Features tab "Physics" section) --
/// unlike most other prim data, this does NOT ride along ObjectUpdate; the simulator only sends
/// it in response to an explicit object-select request (see GridSession.SelectObject /
/// PhysicsProperties subscription), delivered asynchronously over the EventQueue CAP.</summary>
public record PhysicsPropertiesEvent(
    ulong RegionHandle, uint LocalId,
    PrimPhysicsShapeType ShapeType, float Density, float Friction, float Restitution, float GravityMultiplier
) : IWorldEvent;

/// <summary>Represents the properties of an object (name, description, creator, owner, etc.).</summary>
public record ObjectPropertiesEvent(
    ulong RegionHandle,
    Guid ObjectId,
    string Name,
    string Description,
    Guid CreatorId,
    Guid OwnerId,
    Guid GroupId,
    bool OwnerCanMove = true,
    bool OwnerCanModify = true, bool OwnerCanCopy = true, bool OwnerCanTransfer = true
) : IWorldEvent;

/// <summary>Represents a raw 16x16 chunk of terrain height data from the simulator.
/// <paramref name="RegionSizeX"/>/<paramref name="RegionSizeY"/> are the region's size in
/// metres (256 for a classic region, larger for a varregion).</summary>
public record TerrainPatchEvent(ulong RegionHandle, int X, int Y, int PatchSize, float[] HeightMap,
    int RegionSizeX = 256, int RegionSizeY = 256) : IWorldEvent;
public record TerrainSettingsEvent(
    ulong RegionHandle,
    Guid Detail0, Guid Detail1, Guid Detail2, Guid Detail3,
    float[] StartHeights, float[] HeightRanges,
    float WaterHeight,
    int RegionSizeX = 256, int RegionSizeY = 256
) : IWorldEvent;

/// <summary>Represents a simulator disconnection or departure.</summary>
public record RegionDisconnectedEvent(ulong RegionHandle) : IWorldEvent;

/// <summary>Represents an update to an avatar's visual appearance and baked textures.
/// <paramref name="HoverOffsetZ"/> is the AppearanceHover Z offset (LibreMetaverse:
/// Avatar.HoverHeight.Z, sourced from AvatarAppearancePacket.AppearanceHover) — a per-avatar,
/// user-configured "Hover" shape-slider-style adjustment (commonly used to fix a specific mesh
/// body/shoe's ground contact) that the real viewer adds directly onto the avatar root position
/// (LLVOAvatar::updateRootPositionAndRotation: <c>root_pos += LLVector3d(getHoverOffset())</c>),
/// separate from and in addition to the halfBodySize/PelvisToFoot correction. Verified against
/// linden_llvoavatar.cpp (contents.mHoverOffsetWasSet, applied only for !isSelf() — remote
/// avatars specifically) and AvatarManager.cs's AvatarAppearanceHandler, which parses this SAME
/// field alongside VisualParams from the identical AvatarAppearance packet. Defaults to 0f so
/// existing call sites/tests that don't care about hover keep compiling unchanged.</summary>
public record AvatarAppearanceEvent(ulong RegionHandle, Guid AgentId, byte[] VisualParams, Dictionary<int, Guid> BakedTextures, float HoverOffsetZ = 0f) : IWorldEvent;

/// <summary>Represents the set of animations currently playing on an avatar.</summary>
public record AvatarAnimationEvent(Guid AgentId, List<Guid> AnimationIds) : IWorldEvent;

/// <summary>An LSL <c>llDialog</c> popup request from an in-world object's script. Carries the
/// simulator-assigned reply <paramref name="Channel"/> (not a dialog-session UUID -- LibreMetaverse's
/// ScriptDialogEventArgs has no such id) and up to 12 <paramref name="ButtonLabels"/>. Identity/UI
/// state, not world simulation state, so intentionally not an <see cref="IWorldEvent"/>.</summary>
public record ScriptDialogEvent(
    Guid ObjectId, string ObjectName, Guid OwnerId, string OwnerName,
    string Message, int Channel, IReadOnlyList<string> ButtonLabels);
