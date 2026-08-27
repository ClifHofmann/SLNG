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

    /// <summary>Classic SL material (Stone/Metal/Glass/Wood/Flesh/Plastic/Rubber) -- collision
    /// sound/friction, not the PBR <see cref="RenderMaterialId"/>. Read from the same PrimData
    /// block as <see cref="ProfileCurve"/>, so it's current on every ObjectUpdate, full or terse.</summary>
    public PrimMaterial Material { get; set; } = PrimMaterial.Wood;

    public bool IsMesh { get; set; }
    public Guid MeshId { get; set; }

    /// <summary>UUID of the prim's default-face texture, or empty if untextured.</summary>
    public Guid TextureId { get; set; }

    /// <summary>UUID of the prim's PBR glTF material, or empty if classic material.</summary>
    public Guid RenderMaterialId { get; set; }

    /// <summary>UUID of the DEFAULT face's legacy Blinn-Phong material (normal + specular map),
    /// or empty. A face can carry this and <see cref="RenderMaterialId"/> independently -- they
    /// are two different material systems. Per-face ids live in <see cref="Faces"/>; this covers
    /// prims that send no per-face entries because every face is identical.</summary>
    public Guid LegacyMaterialId { get; set; }

    /// <summary>Base color tint (RGBA) applied to the texture/material.</summary>
    public Vector4 ColorTint { get; set; }

    public float RepeatU { get; set; } = 1.0f;
    public float RepeatV { get; set; } = 1.0f;
    public float OffsetU { get; set; } = 0.0f;
    public float OffsetV { get; set; } = 0.0f;
    public float Rotation { get; set; } = 0.0f;

    /// <summary>The default face's texgen (raw SL value; see <see cref="FaceTexture.TexGen"/>).
    /// Faces in <see cref="Faces"/> carry their own; this covers prims that send no per-face
    /// entries because every face is identical.</summary>
    public byte TexGen { get; set; } = FaceTexture.TexGenDefault;

    /// <summary>The default face's fullbright flag (see <see cref="FaceTexture.Fullbright"/>).
    /// Per-face entries in <see cref="Faces"/> carry their own; this covers prims that send no
    /// per-face entries.</summary>
    public bool Fullbright { get; set; }

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

    /// <summary>llSetTextureAnim state from the ObjectUpdate TextureAnim block, or null when the
    /// object has none. Drives the per-frame UV placement of the animated faces (see
    /// <see cref="TextureAnimator"/>); the face's own repeat/offset/rotation still supplies every
    /// component the animation does not itself drive.</summary>
    public TextureAnimation? TextureAnim { get; set; }

    /// <summary>LibreMetaverse ClickAction byte (0=Touch, 1=Sit, etc).</summary>
    public byte ClickAction { get; set; }

    /// <summary>Particle system parameters for rendering GPUParticles3D.</summary>
    public ParticleSystemData? Particles { get; set; }

    /// <summary>SL attachment-point byte (0 = not worn). Stored so a worn object can be linked
    /// to its avatar even if the avatar entity streams in after this prim.</summary>
    public byte AttachmentPoint { get; set; }

    /// <summary>Mirrors LibreMetaverse's PrimFlags.Physics/Temporary/Phantom/CastShadows bits
    /// (from ObjectUpdate) -- the Build/Inspector "Object" tab checkboxes read and toggle these.</summary>
    public bool IsPhysical { get; set; }
    public bool IsTemporary { get; set; }
    public bool IsPhantom { get; set; }
    public bool CastsShadows { get; set; } = true;

    /// <summary>SL's point-light ("Light") prim property. Mirrors LibreMetaverse's
    /// Primitive.LightData ExtraParams block, which the wire protocol has no separate enable
    /// bit for -- Intensity == 0 means "off" by convention (see GridSession.SetObjectLight).</summary>
    public bool LightEnabled { get; set; }
    public Vector3 LightColor { get; set; } = Vector3.One;
    public float LightIntensity { get; set; } = 1.0f;
    public float LightRadius { get; set; } = 10.0f;
    public float LightFalloff { get; set; } = 1.0f;

    /// <summary>Physics collision shape and material response (Features tab "Physics" section).
    /// Only sourced from the server via an explicit PhysicsProperties request (piggybacked on
    /// object selection, see GridSession) -- these defaults match the sentinel values
    /// SetObjectFlags already sent as placeholders before this feature existed, not any real
    /// server-confirmed state, until that request completes.</summary>
    public PrimPhysicsShapeType PhysicsShapeType { get; set; } = PrimPhysicsShapeType.Prim;
    public float PhysicsGravity { get; set; } = 1.0f;
    public float PhysicsFriction { get; set; } = 0.6f;
    public float PhysicsDensity { get; set; } = 1000f;
    public float PhysicsRestitution { get; set; } = 0.5f;

    /// <summary>True only once WorldSimulation has applied a real PhysicsPropertiesEvent --
    /// distinguishes "the PhysicsX fields above are confirmed server state" from "still just
    /// this component's constructor defaults," which matters because ordinary ObjectUpdates
    /// (position/texture/etc changes) fire the same NotifyComponentUpdated as a physics update
    /// but never touch these fields, and code reacting to that notify (e.g.
    /// ObjectEditWindow.SendObjectFlags's own known-physics baseline) must not mistake unrelated
    /// updates for a confirmation that the defaults are real.</summary>
    public bool HasPhysicsProperties { get; set; }

    public PrimitiveComponent(Vector3 scale, byte profileCurve, bool isMesh = false, Guid meshId = default, Guid textureId = default, Guid renderMaterialId = default, Vector4 colorTint = default, float repeatU = 1.0f, float repeatV = 1.0f, float offsetU = 0.0f, float offsetV = 0.0f, float rotation = 0.0f, PrimShape shape = default, bool isSculpt = false, Guid sculptId = default, byte sculptType = 0, FaceTexture[]? faces = null, byte texGen = FaceTexture.TexGenDefault, ParticleSystemData? particles = null, bool fullbright = false)
    {
        Scale = scale;
        ProfileCurve = profileCurve;
        IsMesh = isMesh;
        MeshId = meshId;
        TextureId = textureId;
        RenderMaterialId = renderMaterialId;
        ColorTint = colorTint;
        RepeatU = repeatU;
        RepeatV = repeatV;
        OffsetU = offsetU;
        OffsetV = offsetV;
        Rotation = rotation;
        Shape = shape;
        IsSculpt = isSculpt;
        SculptId = sculptId;
        SculptType = sculptType;
        Faces = faces;
        TexGen = texGen;
        Particles = particles;
        Fullbright = fullbright;
    }
}
