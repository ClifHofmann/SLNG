namespace SLNG.Core.Components;

/// <summary>
/// The per-particle blend factors of an SL particle system (<c>PSYS_PART_BLEND_FUNC_SOURCE</c> /
/// <c>PSYS_PART_BLEND_FUNC_DEST</c>), as a protocol-neutral enum so no LibreMetaverse type crosses
/// the boundary (AGENTS.md's layering rule).
///
/// <para>Values match the wire encoding, which is also LibreMetaverse's
/// <c>ParticleSystem.ParticleDataBlock.BlendFunc</c>. Note that <c>One</c> is 0 and <c>Zero</c> is
/// 1 — the obvious-looking order is the wrong one, so map by NAME rather than casting a literal.
/// </para>
///
/// <para>The default pair is <see cref="SourceAlpha"/> / <see cref="OneMinusSourceAlpha"/>, i.e.
/// ordinary alpha blending. A destination factor of <see cref="One"/> is ADDITIVE, which is what
/// flames, glows and light shafts are built from: overlapping quads accumulate towards white
/// instead of each one occluding the last. Rendering those with plain alpha blending draws every
/// sprite as a flat opaque card with a visible silhouette (live comparison against Firestorm on a
/// candle flame, 2026-09-09).</para>
///
/// <para>The reference viewer applies them per draw call —
/// <c>lldrawpoolalpha.cpp:774</c>, <c>gGL.blendFunc((eBlendFactor) params.mBlendFuncSrc,
/// (eBlendFactor) params.mBlendFuncDst, …)</c>. Not to be confused with
/// <c>SlParticleDataFlags.Emissive</c>, which is a separate thing: that one makes the face
/// fullbright and adds it to the ALPHA channel for the glow/bloom pass
/// (<c>lldrawpoolalpha.cpp:832-839</c>, "don't touch color, add to alpha (glow)").</para>
/// </summary>
public enum SlParticleBlendFunc : byte
{
    One = 0,
    Zero = 1,
    DestColor = 2,
    SourceColor = 3,
    OneMinusDestColor = 4,
    OneMinusSourceColor = 5,
    DestAlpha = 6,
    SourceAlpha = 7,
    OneMinusDestAlpha = 8,
    OneMinusSourceAlpha = 9,
}
