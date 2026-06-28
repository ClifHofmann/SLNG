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

/// <summary>Represents a spatial update for a simulator object or avatar.</summary>
/// <param name="ParentLocalId">Local ID of the parent object, or 0 if unparented.</param>
/// <param name="AttachmentPoint">SL AttachmentPoint enum byte value; non-zero when the object
/// is worn on an avatar (i.e. ParentLocalId is an avatar's LocalId).</param>
public record ObjectUpdateEvent(
    ulong RegionHandle, uint LocalId,
    Vector3 Position, Quaternion Rotation, Vector3 Scale,
    byte ProfileCurve, bool IsMesh, Guid MeshId, Guid TextureId, Guid RenderMaterialId, Vector4 ColorTint,
    uint ParentLocalId, byte AttachmentPoint,
    PrimShape Shape = default
) : IWorldEvent;

/// <summary>Represents an update for an avatar.</summary>
public record AvatarUpdateEvent(ulong RegionHandle, uint LocalId, Guid AgentId, Vector3 Position, Quaternion Rotation, string FirstName, string LastName, bool IsLocalAgent) : IWorldEvent;

/// <summary>Represents the removal of an object from the simulator's interest list.</summary>
public record ObjectRemovedEvent(ulong RegionHandle, uint LocalId) : IWorldEvent;

/// <summary>Represents a raw 16x16 chunk of terrain height data from the simulator.</summary>
public record TerrainPatchEvent(ulong RegionHandle, int X, int Y, int PatchSize, float[] HeightMap) : IWorldEvent;
public record TerrainSettingsEvent(
    ulong RegionHandle,
    Guid Detail0, Guid Detail1, Guid Detail2, Guid Detail3,
    float[] StartHeights, float[] HeightRanges,
    float WaterHeight
) : IWorldEvent;

/// <summary>Represents a simulator disconnection or departure.</summary>
public record RegionDisconnectedEvent(ulong RegionHandle) : IWorldEvent;

/// <summary>Represents an update to an avatar's visual appearance and baked textures.</summary>
public record AvatarAppearanceEvent(ulong RegionHandle, Guid AgentId, byte[] VisualParams, Dictionary<int, Guid> BakedTextures) : IWorldEvent;

/// <summary>Represents the set of animations currently playing on an avatar.</summary>
public record AvatarAnimationEvent(Guid AgentId, List<Guid> AnimationIds) : IWorldEvent;
