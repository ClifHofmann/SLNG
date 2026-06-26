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
public record ObjectUpdateEvent(ulong RegionHandle, uint LocalId, Vector3 Position, Quaternion Rotation, Vector3 Scale, byte ProfileCurve, bool IsMesh, Guid MeshId, Guid TextureId, Guid RenderMaterialId, Vector4 ColorTint) : IWorldEvent;

/// <summary>Represents an update for an avatar.</summary>
public record AvatarUpdateEvent(ulong RegionHandle, uint LocalId, Guid AgentId, Vector3 Position, Quaternion Rotation, string FirstName, string LastName, bool IsLocalAgent) : IWorldEvent;

/// <summary>Represents the removal of an object from the simulator's interest list.</summary>
public record ObjectRemovedEvent(ulong RegionHandle, uint LocalId) : IWorldEvent;

/// <summary>Represents a raw 16x16 chunk of terrain height data from the simulator.</summary>
public record TerrainPatchEvent(ulong RegionHandle, int X, int Y, int PatchSize, float[] HeightMap) : IWorldEvent;

/// <summary>Represents a simulator disconnection or departure.</summary>
public record RegionDisconnectedEvent(ulong RegionHandle) : IWorldEvent;
