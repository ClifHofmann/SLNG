using System.Numerics;

namespace SLNG.Net;

/// <summary>
/// Represents a local chat message received from the simulator.
/// </summary>
public record ChatMessageEvent(string FromName, string Message, byte ChatType);

/// <summary>
/// Represents a spatial update for a simulator object or avatar.
/// </summary>
public record ObjectUpdateEvent(uint LocalId, Vector3 Position, Quaternion Rotation);

/// <summary>
/// Represents a raw 16x16 chunk of terrain height data from the simulator.
/// </summary>
public record TerrainPatchEvent(int X, int Y, int PatchSize, float[] HeightMap);
