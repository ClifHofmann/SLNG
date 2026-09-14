namespace SLNG.Core;

/// <summary>
/// SL's Reflection Probe prim property — the <c>0x90</c> ExtraParams block
/// (<c>LLReflectionProbeParams</c>, llprimitive.h:183-221).
///
/// This is how a mirror is identified in Second Life, and the reason matters: it is an explicit
/// flag a creator sets in the Build floater's Features tab, NOT something inferred from a face's
/// material. The reference viewer's hero-probe manager selects its one real-time probe from
/// objects where <c>isReflectionProbe() &amp;&amp; getReflectionProbeIsBox()</c>
/// (llheroprobemanager.cpp:141), registered by <c>LLVOVolume::updateReflectionProbePtr</c>
/// (llvovolume.cpp:4536) precisely when <c>getReflectionProbeIsMirror()</c> is true.
///
/// FEAT-RENDER-22 guessed at mirrors instead — glTF materials with roughness ≤ 0.25 — which
/// matches nothing SL actually transmits: a region can be full of mirrors and contain no glTF
/// material at all (measured: 1289 legacy materials and zero PBR ones in the region this was
/// found in, hero probe therefore never armed once in a whole session).
///
/// LibreMetaverse does not parse this block. <c>ExtraParamType.ReflectionProbe = 0x90</c> exists
/// in its enum, but <c>Primitive.SetExtraParamsFromBytes</c> has no case for it and skips the
/// payload — so the values have to be read out of the raw ObjectUpdate bytes, the same way
/// <c>GridSession</c> already reads the Light block's presence.
/// </summary>
/// <param name="Ambiance">How much ambient light the probe contributes (viewer default 0).</param>
/// <param name="ClipDistance">Near-clip for the probe's own render (viewer default 0).</param>
/// <param name="Flags">The raw flag byte; prefer the properties below.</param>
public readonly record struct ReflectionProbeParams(float Ambiance, float ClipDistance, byte Flags)
{
    /// <summary>FLAG_BOX_VOLUME — the probe's influence volume is a box rather than a sphere.
    /// The hero-probe manager requires it (llheroprobemanager.cpp:141).</summary>
    public const byte FlagBoxVolume = 0x01;

    /// <summary>FLAG_DYNAMIC — render dynamic objects (avatars) into this probe.</summary>
    public const byte FlagDynamic = 0x02;

    /// <summary>FLAG_MIRROR — "this probe is used for reflections on realtime mirrors"
    /// (llprimitive.h:190). The one that makes an object a mirror.</summary>
    public const byte FlagMirror = 0x04;

    public bool IsBox => (Flags & FlagBoxVolume) != 0;
    public bool IsDynamic => (Flags & FlagDynamic) != 0;
    public bool IsMirror => (Flags & FlagMirror) != 0;

    /// <summary>Wire size of the block: two little-endian F32s and a flag byte
    /// (<c>LLReflectionProbeParams::pack</c>, llprimitive.cpp:1837).</summary>
    public const int WireSize = 9;
}
