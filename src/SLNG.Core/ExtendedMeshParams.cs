namespace SLNG.Core;

/// <summary>
/// SL's Extended Mesh prim property -- the <c>0x70</c> ExtraParams block
/// (<c>LLExtendedMeshParams</c>, <c>PARAMS_EXTENDED_MESH</c>, llprimitive.h:109).
///
/// Its payload is a single little-endian <c>U32</c> of flags (<c>pack</c>/<c>unpack</c>,
/// llprimitive.cpp:2273-2285), of which exactly one is defined today: the animated-mesh bit
/// (<see cref="FlagAnimatedMesh"/>, "Animated Mesh" in the Build floater's Features tab). An
/// object whose ROOT prim carries it gets a control avatar in the reference viewer and is skinned
/// to that skeleton instead of being drawn as a plain static mesh
/// (<c>LLVOVolume::isAnimatedObject</c>, llvovolume.cpp:3854-3863).
///
/// LibreMetaverse does not parse this block: its <c>ExtraParamType</c> has no <c>0x70</c> and
/// <c>Primitive.SetExtraParamsFromBytes</c> steps over the payload by its declared length, so the
/// flag has to be read out of the raw ObjectUpdate bytes -- the same way the Light and
/// Reflection-Probe blocks are (see <see cref="ReflectionProbeParams"/>).
/// </summary>
public static class ExtendedMeshParams
{
    /// <summary>The ExtraParams entry type of the block (<c>PARAMS_EXTENDED_MESH</c>).</summary>
    public const ushort ParamType = 0x70;

    /// <summary><c>ANIMATED_MESH_ENABLED_FLAG</c> (llprimitive.h:357): bit 0 of the flags word.</summary>
    public const uint FlagAnimatedMesh = 0x1;

    /// <summary>Wire size of the payload: one <c>U32</c> of flags.</summary>
    public const int WireSize = 4;
}
