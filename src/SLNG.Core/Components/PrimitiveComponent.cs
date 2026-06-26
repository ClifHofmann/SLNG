using System.Numerics;
using SLNG.Core.ECS;

namespace SLNG.Core.Components;

/// <summary>
/// Represents the visual properties of a simulator primitive (Prim).
/// </summary>
public class PrimitiveComponent : IComponent
{
    public Vector3 Scale { get; set; }
    
    /// <summary>
    /// The fundamental shape of the primitive (e.g. Box, Sphere, Cylinder).
    /// Maps to LibreMetaverse's ProfileCurve enum.
    /// </summary>
    public byte ProfileCurve { get; set; }

    public PrimitiveComponent(Vector3 scale, byte profileCurve)
    {
        Scale = scale;
        ProfileCurve = profileCurve;
    }
}
