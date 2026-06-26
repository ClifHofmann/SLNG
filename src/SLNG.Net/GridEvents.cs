using System.Numerics;

namespace SLNG.Net;

/// <summary>
/// Represents a local chat message received from the simulator.
/// </summary>
public record ChatMessageEvent(string FromName, string Message, byte ChatType);

/// <summary>
/// Represents a spatial update for a simulator object or avatar.
/// </summary>
public record ObjectUpdateEvent(ulong RegionHandle, uint LocalId, Vector3 Position, Quaternion Rotation, Vector3 Scale, byte ProfileCurve, bool IsMesh, Guid MeshId);

/// <summary>
/// Represents the removal of an object from the simulator's interest list.
/// </summary>
public record ObjectRemovedEvent(ulong RegionHandle, uint LocalId);

/// <summary>
/// Represents a raw 16x16 chunk of terrain height data from the simulator.
/// </summary>
public record TerrainPatchEvent(ulong RegionHandle, int X, int Y, int PatchSize, float[] HeightMap);

/// <summary>
/// Represents a simulator disconnection or departure.
/// </summary>
public record RegionDisconnectedEvent(ulong RegionHandle);
