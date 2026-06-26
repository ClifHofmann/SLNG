using System.Numerics;
using SLNG.Core.ECS;

namespace SLNG.Core.Components;

/// <summary>
/// Component representing spatial position and orientation in the world.
/// </summary>
public class TransformComponent : IComponent
{
    public Vector3 Position { get; set; }
    public Quaternion Rotation { get; set; }

    public TransformComponent()
    {
        Position = Vector3.Zero;
        Rotation = Quaternion.Identity;
    }

    public TransformComponent(Vector3 position, Quaternion rotation)
    {
        Position = position;
        Rotation = rotation;
    }
}
