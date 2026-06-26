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

    public bool IsMesh { get; set; }
    public Guid MeshId { get; set; }

    /// <summary>UUID of the prim's default-face texture, or empty if untextured.</summary>
    public Guid TextureId { get; set; }

    /// <summary>UUID of the prim's PBR glTF material, or empty if classic material.</summary>
    public Guid RenderMaterialId { get; set; }

    /// <summary>Base color tint (RGBA) applied to the texture/material.</summary>
    public Vector4 ColorTint { get; set; }

    public PrimitiveComponent(Vector3 scale, byte profileCurve, bool isMesh = false, Guid meshId = default, Guid textureId = default, Guid renderMaterialId = default, Vector4 colorTint = default)
    {
        Scale = scale;
        ProfileCurve = profileCurve;
        IsMesh = isMesh;
        MeshId = meshId;
        TextureId = textureId;
        RenderMaterialId = renderMaterialId;
        ColorTint = colorTint;
    }
}
