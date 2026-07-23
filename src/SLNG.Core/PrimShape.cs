namespace SLNG.Core;

/// <summary>
/// Protocol-neutral description of a primitive's procedural shape — everything needed to
/// regenerate its mesh, with no LibreMetaverse types. Mirrors the fields of SL's prim
/// construction data (profile + path + cut/hollow/twist/taper/shear).
///
/// It is a value type with structural equality, so it doubles as a cache key and a cheap
/// "did the shape change?" comparison: two prims with identical shapes compare equal and
/// share one generated mesh.
/// </summary>
/// <param name="ProfileCurve">The RAW packed profile-curve byte from LibreMetaverse's
/// Primitive.ConstructionData.profileCurve -- NOT the masked ProfileCurve property. The low
/// nibble is the profile curve type (Circle/Square/Triangle/...), the high nibble is the hollow
/// cut's own shape (HoleType Same/Circle/Square/Triangle, pre-shifted by LibreMetaverse into
/// 0x00/0x10/0x20/0x30). Both nibbles must survive intact so PrimMeshService can reconstruct a
/// hollow prim's hole shape, not just its outer profile.</param>
public readonly record struct PrimShape(
    byte ProfileCurve,
    byte PathCurve,
    float PathBegin,
    float PathEnd,
    float PathScaleX,
    float PathScaleY,
    float PathShearX,
    float PathShearY,
    float PathTaperX,
    float PathTaperY,
    float PathTwist,
    float PathTwistBegin,
    float PathRadiusOffset,
    float PathSkew,
    float PathRevolutions,
    float ProfileBegin,
    float ProfileEnd,
    float ProfileHollow,
    byte PCode);
