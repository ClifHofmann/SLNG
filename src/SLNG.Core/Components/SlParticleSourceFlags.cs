namespace SLNG.Core.Components;

/// <summary>
/// The particle SOURCE flags -- the 32-bit field in the ObjectUpdate particle block's
/// system section (LibreMetaverse: <c>Primitive.ParticleSystem.PartFlags</c>, viewer:
/// <c>LLPartSysData::mFlags</c>).
///
/// <para>These are NOT the per-particle flags a script sets with <c>PSYS_PART_FLAGS</c> --
/// those live in <see cref="SlParticleDataFlags"/>, a different field in a different section
/// of the same block. The two overlap numerically (both use bits 0 and 1), so reading one
/// where the other is meant silently produces plausible-looking nonsense rather than an
/// error; keeping them in separate enums is what makes that mistake a compile error.</para>
/// </summary>
[Flags]
public enum SlParticleSourceFlags : uint
{
    None = 0,

    /// <summary>Particle acceleration and velocity are relative to the object's rotation.</summary>
    ObjectRelative = 0x01,

    /// <summary>The angle parameters use the corrected interpretation. When this is CLEAR the
    /// source is using the deprecated angles, and the viewer applies an extra rotation of
    /// <c>OuterAngle</c> about the local X axis to every emission direction
    /// (llviewerpartsource.cpp:405-410).</summary>
    UseNewAngle = 0x02,
}
