namespace SLNG.Core.Components;

/// <summary>
/// The emission pattern a script sets with <c>PSYS_SRC_PATTERN</c>. Declared <c>[Flags]</c>
/// because the viewer tests it bitwise rather than by equality
/// (llviewerpartsource.cpp:346-437), and in that order: Drop wins over Explode, which wins
/// over the two angle patterns.
/// </summary>
[Flags]
public enum SlParticlePattern : byte
{
    /// <summary>No pattern set -- the viewer's fall-through: emitted at the source, no velocity.</summary>
    None = 0x00,

    /// <summary>Particles appear at the source with zero velocity.</summary>
    Drop = 0x01,

    /// <summary>Particles fly out in a uniformly random direction on the unit sphere.</summary>
    Explode = 0x02,

    /// <summary>A 2D fan between the inner and outer angle, in the source's local XZ plane.</summary>
    Angle = 0x04,

    /// <summary>The <see cref="Angle"/> fan swept around the source's local Z axis into a cone.</summary>
    AngleCone = 0x08,

    /// <summary>Deprecated. The viewer matches neither angle branch for this value and falls
    /// through to "no velocity", so SLNG does the same rather than inventing behaviour.</summary>
    AngleConeEmpty = 0x10,
}
