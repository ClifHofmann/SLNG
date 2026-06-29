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

    /// <summary>
    /// Procedural shape of a non-mesh prim. Used to regenerate real prim geometry
    /// (profile/path/cut/hollow/twist) instead of a box placeholder. Ignored when
    /// <see cref="IsMesh"/> is true.
    /// </summary>
    public PrimShape Shape { get; set; }

    /// <summary>True if this is a sculpted prim (not a mesh asset): its geometry comes from a
    /// sculpt-map texture (<see cref="SculptId"/>) interpreted per <see cref="SculptType"/>.</summary>
    public bool IsSculpt { get; set; }

    /// <summary>UUID of the sculpt-map texture, when <see cref="IsSculpt"/> is true.</summary>
    public Guid SculptId { get; set; }

    /// <summary>LibreMetaverse SculptType byte (Sphere/Torus/Plane/Cylinder).</summary>
    public byte SculptType { get; set; }

    /// <summary>Per-face textures/colors, indexed by SL face number. Null = use the single
    /// <see cref="TextureId"/>/<see cref="ColorTint"/> for the whole object.</summary>
    public FaceTexture[]? Faces { get; set; }

    public PrimitiveComponent(Vector3 scale, byte profileCurve, bool isMesh = false, Guid meshId = default, Guid textureId = default, Guid renderMaterialId = default, Vector4 colorTint = default, PrimShape shape = default, bool isSculpt = false, Guid sculptId = default, byte sculptType = 0, FaceTexture[]? faces = null)
    {
        Scale = scale;
        ProfileCurve = profileCurve;
        IsMesh = isMesh;
        MeshId = meshId;
        TextureId = textureId;
        RenderMaterialId = renderMaterialId;
        ColorTint = colorTint;
        Shape = shape;
        IsSculpt = isSculpt;
        SculptId = sculptId;
        SculptType = sculptType;
        Faces = faces;
    }
}
