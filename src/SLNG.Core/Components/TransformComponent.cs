using System.Numerics;
using SLNG.Core.ECS;

namespace SLNG.Core.Components;

/// <summary>
/// Component representing spatial position and orientation in the world.
/// </summary>
public class TransformComponent : IComponent
{
    /// <summary>World-space position (region-local metres). For a linked child prim this is
    /// composed from the parent; the renderer always reads this.</summary>
    public Vector3 Position { get; set; }

    /// <summary>World-space orientation.</summary>
    public Quaternion Rotation { get; set; }

    /// <summary>Position as sent by the simulator: relative to the parent for a linked child,
    /// world-space for a root. Kept so the world transform can be recomposed when the parent
    /// arrives or moves.</summary>
    public Vector3 LocalPosition { get; set; }

    /// <summary>Orientation as sent by the simulator (relative for a child).</summary>
    public Quaternion LocalRotation { get; set; }

    /// <summary>LocalID of the parent prim in a linkset, or 0 if this is a root/unlinked.</summary>
    public uint ParentLocalId { get; set; }

    public TransformComponent()
    {
        Position = Vector3.Zero;
        Rotation = Quaternion.Identity;
        LocalRotation = Quaternion.Identity;
    }

    public TransformComponent(Vector3 position, Quaternion rotation)
    {
        Position = position;
        Rotation = rotation;
        LocalPosition = position;
        LocalRotation = rotation;
    }
}
