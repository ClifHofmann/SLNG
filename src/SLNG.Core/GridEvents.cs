using System.Numerics;

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
public record ChatMessageEvent(string FromName, string Message, byte ChatType);

/// <summary>Resolves a user or group UUID (Creator, Owner, Group, ...) to a display name.
/// Identity-cache data, not world simulation state, so intentionally not an
/// <see cref="IWorldEvent"/> -- consumers (UI) subscribe directly on GridSession.</summary>
public record NameResolvedEvent(Guid Id, string Name);

/// <summary>The simulator's urgent-message channel -- e.g. "Object physics cancelled because
/// it exceeds limits for physical prims" when an ObjectFlagUpdate is silently rejected server-
/// side. Not world-state, so intentionally not an <see cref="IWorldEvent"/>.</summary>
public record AlertMessageEvent(string Message);

/// <summary>A friend's online/offline presence changed. Identity/social state, not world
/// simulation state, so intentionally not an <see cref="IWorldEvent"/> -- consumers (UI)
/// subscribe directly on GridSession, same as <see cref="NameResolvedEvent"/>.</summary>
public record FriendStatusEvent(Guid FriendId, bool IsOnline);

/// <summary>A 1:1 instant message from another avatar (LibreMetaverse's
/// <c>InstantMessageDialog.MessageFromAgent</c> only -- friendship offers, teleport requests,
/// group notices etc. ride the same wire event but aren't modeled here yet). Not world-state, so
/// intentionally not an <see cref="IWorldEvent"/>.</summary>
public record InstantMessageEvent(Guid FromAgentId, string FromAgentName, string Message, Guid SessionId);

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
    bool IsFullUpdate = true
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
public record AvatarUpdateEvent(ulong RegionHandle, uint LocalId, Guid AgentId, Vector3 Position, Quaternion Rotation, string FirstName, string LastName, bool IsLocalAgent, float ScaleZ = 0f, Vector3 Velocity = default) : IWorldEvent;

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
