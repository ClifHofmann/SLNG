namespace SLNG.Core;

/// <summary>Physics collision representation, distinct from the visual mesh -- what you
/// collide with vs. what you see. Values match LibreMetaverse's PhysicsShapeType exactly (and,
/// numerically, OpenSim's PhysShapeType) so GridSession can cast directly at the SLNG.Net
/// boundary. 255 ("invalid") is deliberately NOT a member here -- it's an internal wire sentinel
/// meaning "leave extra-physics data alone," not a real user-facing choice (see
/// GridSession.SetObjectFlags).</summary>
public enum PrimPhysicsShapeType : byte
{
    Prim = 0,
    None = 1,
    ConvexHull = 2,
}
